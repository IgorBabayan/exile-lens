using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.Application.History;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;

namespace ExileLens.App.ViewModels;

public sealed record PriceHistoryRow(string When, string Game, string Item, string Detail, string Value, string? Disenchant);

public sealed record MapHistoryRow(string When, string Game, string Map, string Overall, string Profile, string Modifiers);

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private bool _dirty = true;
    private bool _active;

    public HistoryViewModel(IHistoryService history, ISettingsService settings)
    {
        _history = history;
        _settings = settings;
        history.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _dirty = true;
            if (_active)
            {
                Rebuild();
            }
        });
    }

    public IReadOnlyList<string> GameFilters { get; } = ["All games", "Path of Exile", "Path of Exile 2"];

    public ObservableCollection<PriceHistoryRow> PriceRows { get; } = [];

    public ObservableCollection<MapHistoryRow> MapRows { get; } = [];

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedGameFilter = "All games";

    public decimal? MaxPriceChecks
    {
        get => _settings.Current.History.MaxPriceChecks;
        set
        {
            _settings.Update(s => s.History.MaxPriceChecks = (int)Math.Clamp(value ?? 200, 10, 5000));
            OnPropertyChanged();
        }
    }

    public decimal? MaxMapChecks
    {
        get => _settings.Current.History.MaxMapChecks;
        set
        {
            _settings.Update(s => s.History.MaxMapChecks = (int)Math.Clamp(value ?? 200, 10, 5000));
            OnPropertyChanged();
        }
    }

    public override void OnActivated()
    {
        _active = true;
        if (_dirty)
        {
            Rebuild();
        }
    }

    public override void OnDeactivated() => _active = false;

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSelectedGameFilterChanged(string value) => Rebuild();

    [RelayCommand]
    private void Clear() => _history.Clear();

    private bool GameMatches(GameId game) => SelectedGameFilter switch
    {
        "Path of Exile" => game == GameId.Poe1,
        "Path of Exile 2" => game == GameId.Poe2,
        _ => true,
    };

    private void Rebuild()
    {
        _dirty = false;
        var q = SearchText.Trim();
        PriceRows.Clear();
        foreach (var p in _history.PriceChecks)
        {
            if (!GameMatches(p.Game) || (q.Length > 0 && !p.ItemName.Contains(q, StringComparison.OrdinalIgnoreCase) && !p.BaseType.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = p.MarketLow is { } lo && p.MarketHigh is { } hi
                ? $"{Formatting.Range(lo, hi)} {Formatting.Plural(p.ValuationCurrency ?? string.Empty, hi)}"
                : "No estimate";
            PriceRows.Add(new PriceHistoryRow(
                p.Timestamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                p.Game == GameId.Poe1 ? "PoE 1" : "PoE 2",
                p.ItemName,
                $"{p.BaseType}{(p.ItemLevel is { } il ? $" · ilvl {il}" : string.Empty)} · {p.League}",
                value,
                p.DisenchantSummary));
        }

        MapRows.Clear();
        foreach (var m in _history.MapChecks)
        {
            if (!GameMatches(m.Game) || (q.Length > 0 && !m.MapName.Contains(q, StringComparison.OrdinalIgnoreCase) && !m.Modifiers.Exists(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            MapRows.Add(new MapHistoryRow(
                m.Timestamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                m.Game == GameId.Poe1 ? "PoE 1" : "PoE 2",
                m.Tier is { } t ? $"Tier {t} · {m.MapName}" : m.MapName,
                Services.DangerPalette.Label(m.Overall),
                m.ProfileName,
                string.Join(" · ", m.Modifiers)));
        }
    }
}
