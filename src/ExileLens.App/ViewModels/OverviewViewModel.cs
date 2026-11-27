using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.History;
using ExileLens.Application.Market;
using ExileLens.Application.Platform;
using ExileLens.Application.Settings;
using ExileLens.Application.Updates;
using ExileLens.Core.Maps;

namespace ExileLens.App.ViewModels;

public sealed record ShortcutRow(string Action, string Chord, string? Error)
{
    public bool HasError => Error is not null;
}

public sealed record RecentRow(string Title, string Detail, string When);

public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly IGameDetectionService _detection;
    private readonly ISettingsService _settings;
    private readonly IGameRegistry _games;
    private readonly ILeagueService _leagues;
    private readonly GameActionCoordinator _coordinator;
    private readonly IHistoryService _history;
    private readonly ICurrencyConversionService _rates;
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _timer;

    public OverviewViewModel(
        IGameDetectionService detection,
        ISettingsService settings,
        IGameRegistry games,
        ILeagueService leagues,
        GameActionCoordinator coordinator,
        IHistoryService history,
        ICurrencyConversionService rates,
        IGlobalHotkeyService hotkeys,
        IUpdateService updates,
        TimeProvider time)
    {
        _detection = detection;
        _settings = settings;
        _games = games;
        _leagues = leagues;
        _coordinator = coordinator;
        _history = history;
        _rates = rates;
        _hotkeys = hotkeys;
        _time = time;
        Version = $"Version {updates.CurrentVersion}";
        // Status is refreshed only while this page is visible.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        history.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshHistory);
        coordinator.RegistrationChanged += (_, _) => Dispatcher.UIThread.Post(RefreshShortcuts);
    }

    public string Version { get; }

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _league = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionText = string.Empty;

    [ObservableProperty]
    private string _marketStatus = string.Empty;

    [ObservableProperty]
    private string? _platformNote;

    public ObservableCollection<ShortcutRow> Shortcuts { get; } = [];

    public ObservableCollection<RecentRow> RecentPriceChecks { get; } = [];

    public ObservableCollection<RecentRow> RecentMapChecks { get; } = [];

    public bool HasRecentPriceChecks => RecentPriceChecks.Count > 0;

    public bool HasRecentMapChecks => RecentMapChecks.Count > 0;

    public override void OnActivated()
    {
        Refresh();
        RefreshShortcuts();
        RefreshHistory();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void Refresh()
    {
        var window = _detection.Detect();
        var game = _detection.Resolve(window);
        var module = _games.Get(game);
        GameName = module.DisplayName + (_settings.Current.General.Game == GameSelection.Auto ? " (auto-detected)" : string.Empty);
        League = _leagues.GetActiveLeague(game);
        IsConnected = window is not null;
        ConnectionText = window is null ? "Game not running" : $"{module.DisplayName} is running";
        var rates = _rates.Peek(game, League);
        MarketStatus = rates is null || rates.IsEmpty
            ? "Market data loads with the first price check"
            : $"{rates.Count} currency rates from {rates.Source} · updated {Formatting.Age(rates.FetchedAt, _time.GetUtcNow())}";
        PlatformNote = _hotkeys.Limitation;
    }

    private void RefreshShortcuts()
    {
        var game = _detection.Resolve(null);
        var g = _settings.Current.For(game);
        var errors = _coordinator.RegistrationErrors;
        Shortcuts.Clear();
        foreach (var (action, text) in new[] { (HotkeyAction.PriceCheck, g.PriceCheckHotkey), (HotkeyAction.MapCheck, g.MapCheckHotkey), (HotkeyAction.CloseOverlay, g.CloseOverlayHotkey) })
        {
            var chord = KeyChord.TryParse(text, out var c) ? c.ToString() : text;
            Shortcuts.Add(new ShortcutRow(GameActionCoordinator.Describe(action), chord, errors.GetValueOrDefault(action)));
        }
    }

    private void RefreshHistory()
    {
        var now = _time.GetUtcNow();
        RecentPriceChecks.Clear();
        foreach (var p in _history.PriceChecks.Take(5))
        {
            var value = p.MarketLow is { } lo && p.MarketHigh is { } hi
                ? $"{Formatting.Range(lo, hi)} {Formatting.Plural(p.ValuationCurrency ?? string.Empty, hi)}"
                : "No estimate";
            RecentPriceChecks.Add(new RecentRow(p.ItemName, value, Formatting.Age(p.Timestamp, now)));
        }

        RecentMapChecks.Clear();
        foreach (var m in _history.MapChecks.Take(5))
        {
            RecentMapChecks.Add(new RecentRow(m.Tier is { } t ? $"Tier {t} · {m.MapName}" : m.MapName, $"{LevelText(m.Overall)} · {m.ProfileName}", Formatting.Age(m.Timestamp, now)));
        }

        OnPropertyChanged(nameof(HasRecentPriceChecks));
        OnPropertyChanged(nameof(HasRecentMapChecks));
    }

    private static string LevelText(DangerLevel level) => Services.DangerPalette.Label(level);
}
