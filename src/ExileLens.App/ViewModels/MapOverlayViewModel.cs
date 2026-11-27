using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.App.Services;
using ExileLens.Application.Maps;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;

namespace ExileLens.App.ViewModels;

public sealed partial class MapModifierRowViewModel(MapOverlayViewModel owner, AnalyzedMapModifier modifier, IBrush brush, Geometry? icon) : ObservableObject
{
    public string Text => modifier.Modifier.Text;
    public string ModifierId => modifier.ModifierId;
    public DangerLevel Level => modifier.Level;
    public IBrush Brush => brush;
    public Geometry? Icon => icon;
    public string? Note => modifier.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(modifier.Note);
    public string Tooltip => $"{DangerPalette.Label(modifier.Level)} — click to change";

    [RelayCommand]
    private void Cycle() => owner.Cycle(this);
}

public sealed record StatTile(string Label, int Count, IBrush Brush);

public sealed partial class MapOverlayViewModel : ObservableObject, IOverlayContent
{
    private readonly IModifierProfileService _profiles;
    private readonly IMapModifierAnalyzer _analyzer;
    private readonly DangerPalette _palette;
    private readonly ISettingsService _settings;
    private readonly Action _close;
    private MapAnalysisResult _result;

    public MapOverlayViewModel(MapAnalysisResult result, IModifierProfileService profiles, IMapModifierAnalyzer analyzer, DangerPalette palette, ISettingsService settings, Action close)
    {
        _result = result;
        _profiles = profiles;
        _analyzer = analyzer;
        _palette = palette;
        _settings = settings;
        _close = close;
        Rebuild();
    }

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _overallLabel = string.Empty;

    [ObservableProperty]
    private IBrush _overallBrush = Brushes.Gray;

    [ObservableProperty]
    private IBrush _overallSoftBrush = Brushes.Transparent;

    [ObservableProperty]
    private Geometry? _overallIcon;

    [ObservableProperty]
    private string _verdict = string.Empty;

    [ObservableProperty]
    private string? _bonuses;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private string _initial = "M";

    public ObservableCollection<StatTile> Tiles { get; } = [];

    public ObservableCollection<MapModifierRowViewModel> Modifiers { get; } = [];

    public ObservableCollection<string> Combinations { get; } = [];

    public bool HasCombinations => Combinations.Count > 0;

    public bool HasBonuses => !string.IsNullOrEmpty(Bonuses);

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public void Detach()
    {
    }

    [RelayCommand]
    private void Close() => _close();

    /// <summary>Single-click classification straight from the overlay; the map is re-analyzed immediately.</summary>
    internal void Cycle(MapModifierRowViewModel row)
    {
        var game = _result.Item.Game;
        var profile = _profiles.GetActiveProfile(game);
        _profiles.SetLevel(profile.Id, row.ModifierId, row.Level.Next());
        _result = _analyzer.Analyze(_result.Item, profile, _profiles.GetCatalog(game));
        Rebuild();
    }

    private void Rebuild()
    {
        var item = _result.Item;
        var kind = item.Category == ItemCategory.Waystone ? "Waystone" : "Map";
        Title = item.MapTier is { } t
            ? $"Tier {t} {(item.BaseType.Contains(kind, StringComparison.Ordinal) ? item.BaseType : kind)}"
            : item.DisplayName;
        Initial = $"T{item.MapTier}";
        Subtitle = $"Profile: {_result.ProfileName}";
        OverallLabel = DangerPalette.Label(_result.Overall);
        OverallBrush = _palette.Brush(_result.Overall);
        OverallSoftBrush = _palette.SoftBrush(_result.Overall);
        OverallIcon = DangerPalette.Icon(_result.Overall);
        Note = _result.Note;

        var bonuses = new List<string>(3);
        foreach (var (key, label) in new[] { ("Item Quantity", "Quantity"), ("Item Rarity", "Rarity"), ("Monster Pack Size", "Pack size") })
        {
            if (item.TryGetProperty(key, out var value))
            {
                bonuses.Add($"{label} {value.Replace(" (augmented)", string.Empty, StringComparison.Ordinal)}");
            }
        }

        Bonuses = bonuses.Count > 0 ? string.Join(" · ", bonuses) : null;

        Tiles.Clear();
        Tiles.Add(new StatTile("Safe", _result.SafeCount, _palette.Brush(DangerLevel.Safe)));
        Tiles.Add(new StatTile("Warning", _result.WarningCount, _palette.Brush(DangerLevel.Warning)));
        Tiles.Add(new StatTile("Dangerous", _result.DangerousCount, _palette.Brush(DangerLevel.Dangerous)));
        if (_result.UnclassifiedCount > 0)
        {
            Tiles.Add(new StatTile("Unclassified", _result.UnclassifiedCount, _palette.Brush(DangerLevel.Unclassified)));
        }

        Modifiers.Clear();
        int SourceIndex(ItemModifier mod)
        {
            for (var i = 0; i < item.Modifiers.Count; i++)
            {
                if (ReferenceEquals(item.Modifiers[i], mod))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        var ordered = _settings.Current.Maps.SortByDanger
            ? _result.Modifiers
            : _result.Modifiers.OrderBy(m => SourceIndex(m.Modifier)).ToList();
        foreach (var m in ordered)
        {
            Modifiers.Add(new MapModifierRowViewModel(this, m, _palette.Brush(m.Level), DangerPalette.Icon(m.Level)));
        }

        Combinations.Clear();
        foreach (var c in _result.Combinations)
        {
            Combinations.Add(c.Name);
        }

        Verdict = _result.ModifiersUnavailable
            ? "Modifiers could not be read — not marked as safe."
            : _result.Overall switch
            {
                DangerLevel.Dangerous => "Not recommended for your current build.",
                DangerLevel.Warning => "Playable with caution for your current build.",
                DangerLevel.Unclassified => "Some modifiers are not classified yet — click one to classify it.",
                _ => "Safe for your current build.",
            };

        OnPropertyChanged(nameof(HasCombinations));
        OnPropertyChanged(nameof(HasBonuses));
        OnPropertyChanged(nameof(HasNote));
    }
}

public sealed partial class NoticeOverlayViewModel(string title, string message, Action close) : ObservableObject, IOverlayContent
{
    public string Title => title;
    public string Message => message;
    public bool IsPinned => false;

    public void Detach()
    {
    }

    [RelayCommand]
    private void Close() => close();
}
