using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.App.Services;
using ExileLens.Application.Capture;
using ExileLens.Application.Chat;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Platform;
using ExileLens.Application.Settings;
using ExileLens.Application.Updates;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;

namespace ExileLens.App.ViewModels;

public sealed record GameSelectionOption(GameSelection Value, string Label);

public sealed record ThemeOption(ThemeMode Value, string Label);

public sealed record PlacementOption(OverlayPlacement Value, string Label);

public sealed record ListingStatusOption(TradeListingStatus Value, string Label);

/// <summary>Drop-down row for a trade buyout currency; icons arrive asynchronously after the page is first shown.</summary>
public sealed partial class PriceCurrencyOption(TradePriceCurrency currency) : ObservableObject
{
    public TradePriceCurrency Currency { get; } = currency;

    public string Label => Currency.Label;

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private Bitmap? _secondIcon;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IAutoStartService _autoStart;
    private readonly IUpdateService _updates;
    private readonly ITradeDataProvider _tradeData;
    private readonly ItemIconLoader _icons;
    private readonly IGameModule _iconGame;
    private UpdateInfo? _update;
    private bool _currencyIconsRequested;

    public SettingsViewModel(
        ISettingsService settings,
        IGameRegistry games,
        ILeagueService leagues,
        IAutoStartService autoStart,
        IUpdateService updates,
        ITradeDataProvider tradeData,
        ItemIconLoader icons)
    {
        _settings = settings;
        _autoStart = autoStart;
        _updates = updates;
        _tradeData = tradeData;
        _icons = icons;

        // The currency list mirrors the PoE 1 trade site, so its art comes from the PoE 1 static data.
        _iconGame = games.Get(GameId.Poe1);
        PriceCurrencies = TradePriceCurrencies.All.Select(c => new PriceCurrencyOption(c)).ToArray();
        GameOptions = games.All.Select(g => new GameOption(g.Game, g.DisplayName)).ToList();
        _shortcutGame = GameOptions.FirstOrDefault(o => o.Game == GameId.Poe1) ?? GameOptions[0];
        LeagueSelectors = games.All.Select(g => new LeagueSelectorViewModel(g, settings, leagues)).ToList();
        CurrentVersion = updates.CurrentVersion.ToString();
        ChatCommands = new ObservableCollection<ChatCommandViewModel>(settings.Current.Chat.Commands.Select(CreateChatRow));
        RefreshChatErrors();
    }

    public IReadOnlyList<GameSelectionOption> GameSelections { get; } =
    [
        new(GameSelection.Auto, "Detect automatically"),
        new(GameSelection.Poe1, "Path of Exile"),
        new(GameSelection.Poe2, "Path of Exile 2"),
    ];

    public IReadOnlyList<GameOption> GameOptions { get; }

    public IReadOnlyList<ThemeOption> Themes { get; } = [new(ThemeMode.System, "Follow system"), new(ThemeMode.Light, "Light"), new(ThemeMode.Dark, "Dark")];

    public IReadOnlyList<PlacementOption> Placements { get; } =
    [
        new(OverlayPlacement.NearCursor, "Next to the cursor"),
        new(OverlayPlacement.LeftOfGame, "Left side of the game"),
        new(OverlayPlacement.RightOfGame, "Right side of the game"),
        new(OverlayPlacement.CenterOfGame, "Center of the game"),
    ];

    public IReadOnlyList<string> Languages { get; } = ["English"];

    public IReadOnlyList<string> AccentPresets { get; } = ["#C96442", "#B0623F", "#7D8F69", "#5B7DB1", "#8B6BB5", "#B5895B"];

    public string CurrentVersion { get; }

    public string MarketProvider => "poe.ninja (currency & bulk items) + official trade (item listings)";

    public string? AutoStartNote => _autoStart.IsSupported ? null : "Start with the operating system is not supported on this platform.";

    // ---------- General ----------
    public GameSelectionOption SelectedGameMode
    {
        get => GameSelections.First(o => o.Value == _settings.Current.General.Game);
        set => Set(s => s.General.Game = value.Value);
    }

    public GameOption PreferredGame
    {
        get => GameOptions.First(o => o.Game == _settings.Current.General.PreferredGame);
        set => Set(s => s.General.PreferredGame = value.Game);
    }

    public bool StartWithSystem
    {
        get => _settings.Current.General.StartWithSystem;
        set
        {
            Set(s => s.General.StartWithSystem = value);
            if (_autoStart.IsSupported)
            {
                _autoStart.SetEnabled(value, _settings.Current.General.StartMinimized);
            }
        }
    }

    public bool StartMinimized
    {
        get => _settings.Current.General.StartMinimized;
        set
        {
            Set(s => s.General.StartMinimized = value);
            if (_autoStart.IsSupported && _settings.Current.General.StartWithSystem)
            {
                _autoStart.SetEnabled(true, value);
            }
        }
    }

    public bool MinimizeToTray { get => _settings.Current.General.MinimizeToTray; set => Set(s => s.General.MinimizeToTray = value); }

    public bool CloseToTray { get => _settings.Current.General.CloseToTray; set => Set(s => s.General.CloseToTray = value); }

    public bool UseAdvancedCopy { get => _settings.Current.General.UseAdvancedCopy; set => Set(s => s.General.UseAdvancedCopy = value); }

    public string Language { get => "English"; set => Set(s => s.General.Language = "en"); }

    // ---------- Appearance ----------
    public ThemeOption Theme
    {
        get => Themes.First(t => t.Value == _settings.Current.Appearance.Theme);
        set => Set(s => s.Appearance.Theme = value.Value);
    }

    public string AccentColor
    {
        get => _settings.Current.Appearance.AccentColor;
        set
        {
            if (Avalonia.Media.Color.TryParse(value, out _))
            {
                Set(s => s.Appearance.AccentColor = value);
            }
        }
    }

    [RelayCommand]
    private void UseAccent(string hex) => AccentColor = hex;

    public double FontSize { get => _settings.Current.Appearance.FontSize; set => Set(s => s.Appearance.FontSize = Math.Round(Math.Clamp(value, 11, 17), 1)); }

    public double OverlayScale { get => _settings.Current.Appearance.OverlayScale; set => Set(s => s.Appearance.OverlayScale = Math.Round(Math.Clamp(value, 0.75, 1.6), 2)); }

    public double OverlayOpacity { get => _settings.Current.Appearance.OverlayOpacity; set => Set(s => s.Appearance.OverlayOpacity = Math.Round(Math.Clamp(value, 0.5, 1), 2)); }

    public bool AnimationsEnabled { get => _settings.Current.Appearance.AnimationsEnabled; set => Set(s => s.Appearance.AnimationsEnabled = value); }

    public PlacementOption Placement
    {
        get => Placements.First(p => p.Value == _settings.Current.Appearance.OverlayPlacement);
        set => Set(s => s.Appearance.OverlayPlacement = value.Value);
    }

    public bool CloseOnClickOutside { get => _settings.Current.Appearance.CloseOverlayOnClickOutside; set => Set(s => s.Appearance.CloseOverlayOnClickOutside = value); }

    public bool CloseOnGameFocusLoss { get => _settings.Current.Appearance.CloseOverlayOnGameFocusLoss; set => Set(s => s.Appearance.CloseOverlayOnGameFocusLoss = value); }

    // ---------- Shortcuts (per game) ----------
    [ObservableProperty]
    private GameOption _shortcutGame;

    [ObservableProperty]
    private string? _shortcutError;

    partial void OnShortcutGameChanged(GameOption value)
    {
        OnPropertyChanged(nameof(PriceCheckHotkey));
        OnPropertyChanged(nameof(MapCheckHotkey));
        OnPropertyChanged(nameof(CloseOverlayHotkey));
    }

    public string PriceCheckHotkey { get => G.PriceCheckHotkey; set => SetShortcut(g => g.PriceCheckHotkey = value); }

    public string MapCheckHotkey { get => G.MapCheckHotkey; set => SetShortcut(g => g.MapCheckHotkey = value); }

    public string CloseOverlayHotkey { get => G.CloseOverlayHotkey; set => SetShortcut(g => g.CloseOverlayHotkey = value); }

    private GameSettings G => _settings.Current.For(ShortcutGame.Game);

    private void SetShortcut(Action<GameSettings> mutate, [CallerMemberName] string? name = null)
    {
        // Validate on a copy so conflicts are reported before they take effect.
        var copy = new GameSettings { PriceCheckHotkey = G.PriceCheckHotkey, MapCheckHotkey = G.MapCheckHotkey, CloseOverlayHotkey = G.CloseOverlayHotkey };
        mutate(copy);
        var errors = GameActionCoordinator.Validate(copy, out _);
        ShortcutError = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Select(e => $"{GameActionCoordinator.Describe(e.Key)}: {e.Value}"));
        if (errors.Count == 0)
        {
            var game = ShortcutGame.Game;
            _settings.Update(s => mutate(s.For(game)));
            RefreshChatErrors();
        }

        OnPropertyChanged(name);
    }

    [RelayCommand]
    private void ResetShortcuts()
    {
        var game = ShortcutGame.Game;
        _settings.Update(s =>
        {
            var g = s.For(game);
            g.PriceCheckHotkey = "Ctrl+D";
            g.MapCheckHotkey = "Alt+D";
            g.CloseOverlayHotkey = "Escape";
        });
        ShortcutError = null;
        OnShortcutGameChanged(ShortcutGame);
        RefreshChatErrors();
    }

    // ---------- Chat ----------
    public bool ChatEnabled { get => _settings.Current.Chat.Enabled; set => Set(s => s.Chat.Enabled = value); }

    public ObservableCollection<ChatCommandViewModel> ChatCommands { get; }

    [RelayCommand]
    private void AddChatCommand()
    {
        var command = new ChatCommand();

        // Copy-on-write: a background save may be enumerating the current list.
        _settings.Update(s => s.Chat.Commands = [.. s.Chat.Commands, command]);
        ChatCommands.Add(CreateChatRow(command));
    }

    [RelayCommand]
    private void RemoveChatCommand(ChatCommandViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _settings.Update(s => s.Chat.Commands = s.Chat.Commands.Where(c => !ReferenceEquals(c, row.Model)).ToList());
        ChatCommands.Remove(row);
        RefreshChatErrors();
    }

    [RelayCommand]
    private void ResetChatCommands()
    {
        var defaults = ChatSettings.CreateDefaultCommands();
        _settings.Update(s => s.Chat.Commands = defaults);
        ChatCommands.Clear();
        foreach (var command in defaults)
        {
            ChatCommands.Add(CreateChatRow(command));
        }

        RefreshChatErrors();
    }

    private ChatCommandViewModel CreateChatRow(ChatCommand command) => new(command, _settings, RefreshChatErrors);

    private void RefreshChatErrors()
    {
        var errors = ChatCommandRules.Validate(_settings.Current, out _);
        foreach (var row in ChatCommands)
        {
            row.Error = errors.GetValueOrDefault(row.Model);
        }
    }

    // ---------- Leagues (per game) ----------
    public IReadOnlyList<LeagueSelectorViewModel> LeagueSelectors { get; }

    [RelayCommand]
    private Task RefreshLeaguesAsync()
    {
        var loads = new Task[LeagueSelectors.Count];
        for (var i = 0; i < loads.Length; i++)
        {
            loads[i] = LeagueSelectors[i].LoadAsync(force: true);
        }

        return Task.WhenAll(loads);
    }

    // ---------- Trade ----------
    public IReadOnlyList<ListingStatusOption> ListingStatuses { get; } =
        Enum.GetValues<TradeListingStatus>().Select(v => new ListingStatusOption(v, TradeListingStatuses.Label(v))).ToArray();

    public ListingStatusOption ListingStatus
    {
        get => ListingStatuses.FirstOrDefault(o => o.Value == _settings.Current.Trade.ListingStatus) ?? ListingStatuses[(int)TradeListingStatus.InstantBuyout];
        set
        {
            if (value is not null)
            {
                Set(s => s.Trade.ListingStatus = value.Value);
            }
        }
    }

    public IReadOnlyList<PriceCurrencyOption> PriceCurrencies { get; }

    public PriceCurrencyOption PreferredCurrency
    {
        get
        {
            var selected = TradePriceCurrencies.Find(_settings.Current.Trade.PriceCurrency);
            foreach (var option in PriceCurrencies)
            {
                if (ReferenceEquals(option.Currency, selected))
                {
                    return option;
                }
            }

            return PriceCurrencies[0];
        }
        set
        {
            if (value is not null)
            {
                Set(s => s.Trade.PriceCurrency = value.Currency.Key);
            }
        }
    }

    /// <summary>
    /// Fetches the id → URL map once (disk cached for a week), then decodes each icon off the UI thread through the
    /// shared icon cache; repeated ids (chaos) share one download and one bitmap.
    /// </summary>
    private async Task LoadCurrencyIconsAsync()
    {
        IReadOnlyDictionary<string, string> urls;
        try
        {
            urls = await _tradeData.GetCurrencyIconsAsync(_iconGame, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            urls = new Dictionary<string, string>();
        }

        if (urls.Count == 0)
        {
            _currencyIconsRequested = false; // retry the next time the page is opened
            return;
        }

        var loads = new List<Task>(PriceCurrencies.Count);
        foreach (var option in PriceCurrencies)
        {
            var ids = option.Currency.IconIds;
            if (ids.Length > 0 && urls.TryGetValue(ids[0], out var first))
            {
                loads.Add(SetIconAsync(first, b => option.Icon = b));
            }

            if (ids.Length > 1 && urls.TryGetValue(ids[1], out var second))
            {
                loads.Add(SetIconAsync(second, b => option.SecondIcon = b));
            }
        }

        await Task.WhenAll(loads);
    }

    private async Task SetIconAsync(string url, Action<Bitmap> apply)
    {
        if (_icons.TryGetCached(url) is { } cached)
        {
            apply(cached);
            return;
        }

        // Resumes on the UI thread, where the bound property is set.
        if (await _icons.LoadAsync(url, CancellationToken.None) is { } bitmap)
        {
            apply(bitmap);
        }
    }

    public decimal? MaxListings { get => _settings.Current.Trade.MaxListings; set => Set(s => s.Trade.MaxListings = (int)Math.Clamp(value ?? 20, 5, 100)); }

    public decimal? CacheMinutes { get => _settings.Current.Trade.CacheMinutes; set => Set(s => s.Trade.CacheMinutes = (int)Math.Clamp(value ?? 5, 0, 120)); }

    public bool AutoSearch { get => _settings.Current.Trade.AutoSearch; set => Set(s => s.Trade.AutoSearch = value); }

    public string MinimumTradeValue
    {
        get => Formatting.Amount(_settings.Current.Trade.MinimumTradeValue);
        set
        {
            if (decimal.TryParse(value?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0)
            {
                Set(s => s.Trade.MinimumTradeValue = d);
            }
        }
    }

    public double ModifierMinPercent { get => _settings.Current.Trade.ModifierMinPercent; set => Set(s => s.Trade.ModifierMinPercent = Math.Round(Math.Clamp(value, 50, 100))); }

    public bool EnableModifiersByDefault { get => _settings.Current.Trade.EnableModifiersByDefault; set => Set(s => s.Trade.EnableModifiersByDefault = value); }

    public string NinjaBaseUrl
    {
        get => _settings.Current.Trade.NinjaBaseUrl ?? string.Empty;
        set => Set(s => s.Trade.NinjaBaseUrl = Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? value.TrimEnd('/') : null);
    }

    // ---------- Map modifier colors ----------
    public string SafeColor { get => _settings.Current.Maps.SafeColor; set => SetColor(value, (s, v) => s.Maps.SafeColor = v); }

    public string WarningColor { get => _settings.Current.Maps.WarningColor; set => SetColor(value, (s, v) => s.Maps.WarningColor = v); }

    public string DangerousColor { get => _settings.Current.Maps.DangerousColor; set => SetColor(value, (s, v) => s.Maps.DangerousColor = v); }

    public string UnclassifiedColor { get => _settings.Current.Maps.UnclassifiedColor; set => SetColor(value, (s, v) => s.Maps.UnclassifiedColor = v); }

    public bool SortByDanger { get => _settings.Current.Maps.SortByDanger; set => Set(s => s.Maps.SortByDanger = value); }

    // ---------- Disenchantment ----------
    public bool DisenchantEnabled { get => _settings.Current.Rewards.DisenchantEnabled; set => Set(s => s.Rewards.DisenchantEnabled = value); }

    public bool SalvageEnabled { get => _settings.Current.Rewards.SalvageEnabled; set => Set(s => s.Rewards.SalvageEnabled = value); }

    public bool ShowComparison { get => _settings.Current.Rewards.ShowComparison; set => Set(s => s.Rewards.ShowComparison = value); }

    public bool ShowSuggestedAction { get => _settings.Current.Rewards.ShowSuggestedAction; set => Set(s => s.Rewards.ShowSuggestedAction = value); }

    // ---------- Updates ----------
    public bool CheckForUpdatesOnStart { get => _settings.Current.General.CheckForUpdates; set => Set(s => s.General.CheckForUpdates = value); }

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _downloadProgress;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        IsBusy = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _update = await _updates.CheckAsync(cts.Token);
            if (_update is null)
            {
                UpdateStatus = $"You're up to date ({_updates.CurrentVersion}).";
                ReleaseNotes = null;
                CanInstall = false;
            }
            else
            {
                UpdateStatus = $"Version {_update.Version} is available.";
                ReleaseNotes = _update.ReleaseNotes;
                CanInstall = _update.ExpectedSha256 is not null;
                if (!CanInstall)
                {
                    UpdateStatus += " It has no published checksum, so it can only be downloaded manually from the release page.";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            UpdateStatus = "Could not reach GitHub to check for updates.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs only when the user clicks "Download and install" (explicit confirmation).</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p * 100);
            var path = await _updates.DownloadAsync(_update, progress, CancellationToken.None);
            UpdateStatus = "Download verified. Starting the installer — your settings and profiles are kept.";
            await _settings.FlushAsync();
            _updates.Install(path);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or IOException)
        {
            UpdateStatus = $"Update failed: {ex.Message} Your current version is unchanged.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_update is not null)
        {
            Formatting.OpenUrl(_update.HtmlUrl);
        }
    }

    public override void OnActivated()
    {
        if (!_currencyIconsRequested)
        {
            _currencyIconsRequested = true;
            _ = LoadCurrencyIconsAsync();
        }

        // Cheap when cached: the shared list is reused and the pickers are rebuilt only if it changed.
        foreach (var selector in LeagueSelectors)
        {
            _ = selector.LoadAsync(force: false);
        }
    }

    private void Set(Action<AppSettings> mutate, [CallerMemberName] string? name = null)
    {
        _settings.Update(mutate);
        OnPropertyChanged(name);
    }

    private void SetColor(string value, Action<AppSettings, string> apply, [CallerMemberName] string? name = null)
    {
        if (Avalonia.Media.Color.TryParse(value, out _))
        {
            _settings.Update(s => apply(s, value));
        }

        OnPropertyChanged(name);
    }
}
