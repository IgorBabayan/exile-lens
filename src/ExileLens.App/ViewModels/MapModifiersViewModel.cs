using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.App.Services;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.Maps;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;

namespace ExileLens.App.ViewModels;

public sealed partial class ModifierEntryViewModel : ObservableObject
{
    private readonly MapModifiersViewModel _owner;
    private bool _loading;

    public ModifierEntryViewModel(MapModifiersViewModel owner, MapModifierDefinition definition, ModifierRule? rule)
    {
        _owner = owner;
        Definition = definition;
        _loading = true;
        _level = rule?.Level ?? DangerLevel.Unclassified;
        _note = rule?.Note;
        _warnAtText = Formatting.Format(rule?.Thresholds.FirstOrDefault(t => t.Level == DangerLevel.Warning)?.Min);
        _dangerAtText = Formatting.Format(rule?.Thresholds.FirstOrDefault(t => t.Level == DangerLevel.Dangerous)?.Min);
        _loading = false;
    }

    public MapModifierDefinition Definition { get; }

    public string Description => Definition.Description;

    public string Category => Definition.Category;

    public bool HasValue => Definition.Pattern.Contains('#', StringComparison.Ordinal);

    public IBrush Brush => _owner.Palette.Brush(Level);

    public IBrush SoftBrush => _owner.Palette.SoftBrush(Level);

    public string LevelLabel => DangerPalette.Label(Level);

    public bool HasThresholds => !string.IsNullOrWhiteSpace(WarnAtText) || !string.IsNullOrWhiteSpace(DangerAtText);

    [ObservableProperty]
    private DangerLevel _level;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private string? _warnAtText;

    [ObservableProperty]
    private string? _dangerAtText;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnLevelChanged(DangerLevel value)
    {
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(SoftBrush));
        OnPropertyChanged(nameof(LevelLabel));
    }

    partial void OnNoteChanged(string? value) => Save();

    partial void OnWarnAtTextChanged(string? value) => Save();

    partial void OnDangerAtTextChanged(string? value) => Save();

    /// <summary>Single click: Unclassified → Safe → Warning → Dangerous → Unclassified.</summary>
    [RelayCommand]
    private void Cycle()
    {
        Level = Level.Next();
        _owner.SetLevel(this, Level);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void Save()
    {
        OnPropertyChanged(nameof(HasThresholds));
        if (_loading)
        {
            return;
        }

        var thresholds = new List<ValueThreshold>(2);
        // Dangerous first: the first matching threshold wins.
        if (Formatting.TryParseDouble(DangerAtText, out var danger) && danger is { } d)
        {
            thresholds.Add(new ValueThreshold { Min = d, Level = DangerLevel.Dangerous });
        }

        if (Formatting.TryParseDouble(WarnAtText, out var warn) && warn is { } w)
        {
            thresholds.Add(new ValueThreshold { Min = w, Level = DangerLevel.Warning });
        }

        _owner.SaveRule(new ModifierRule { ModifierId = Definition.Id, Level = Level, Note = string.IsNullOrWhiteSpace(Note) ? null : Note, Thresholds = thresholds });
    }
}

public sealed record CombinationRowViewModel(CombinationRule Rule, string Description);

public sealed partial class MapModifiersViewModel : PageViewModel
{
    private const string AllCategories = "All categories";
    private const string AllLevels = "All levels";
    private readonly IModifierProfileService _profiles;
    private readonly IGameDetectionService _detection;
    private readonly DispatcherTimer _searchDebounce;
    private List<ModifierEntryViewModel> _all = [];
    private bool _suppressProfileEvents;

    public MapModifiersViewModel(IModifierProfileService profiles, IGameRegistry games, IGameDetectionService detection, DangerPalette palette)
    {
        _profiles = profiles;
        _detection = detection;
        Palette = palette;
        GameOptions = games.All.Select(g => new GameOption(g.Game, g.DisplayName)).ToList();
        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _searchDebounce!.Stop();
            ApplyFilter();
        });
        profiles.Changed += (_, _) =>
        {
            if (!_suppressProfileEvents)
            {
                Dispatcher.UIThread.Post(ReloadProfiles);
            }
        };
    }

    internal DangerPalette Palette { get; }

    public IReadOnlyList<GameOption> GameOptions { get; }

    public IReadOnlyList<string> LevelOptions { get; } = [AllLevels, "Dangerous", "Warning", "Unclassified", "Safe"];

    public ObservableCollection<string> CategoryOptions { get; } = [AllCategories];

    public ObservableCollection<ModifierProfile> Profiles { get; } = [];

    public ObservableCollection<ModifierEntryViewModel> Visible { get; } = [];

    public ObservableCollection<CombinationRowViewModel> Combinations { get; } = [];

    [ObservableProperty]
    private GameOption? _selectedGame;

    [ObservableProperty]
    private ModifierProfile? _selectedProfile;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _isActiveProfile;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private string _selectedLevel = AllLevels;

    [ObservableProperty]
    private string _combinationName = "Dangerous combination";

    [ObservableProperty]
    private string? _escalateText;

    [ObservableProperty]
    private string? _status;

    public string Summary => $"{Visible.Count} of {_all.Count} modifiers · {_all.Count(m => m.Level != DangerLevel.Unclassified)} classified";

    public override void OnServicesReady()
    {
        var game = _detection.Resolve(null);
        SelectedGame = GameOptions.FirstOrDefault(o => o.Game == game) ?? GameOptions[0];
    }

    partial void OnSelectedGameChanged(GameOption? value) => ReloadProfiles();

    partial void OnSelectedProfileChanged(ModifierProfile? value)
    {
        ProfileName = value?.Name ?? string.Empty;
        LoadEntries();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    partial void OnSelectedLevelChanged(string value) => ApplyFilter();

    private GameId Game => SelectedGame?.Game ?? GameId.Poe2;

    private void ReloadProfiles()
    {
        var keepId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profiles.GetProfiles(Game))
        {
            Profiles.Add(p);
        }

        var active = _profiles.GetActiveProfile(Game);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == keepId) ?? Profiles.FirstOrDefault(p => p.Id == active.Id) ?? Profiles.FirstOrDefault();
        IsActiveProfile = SelectedProfile?.Id == active.Id;
    }

    private void LoadEntries()
    {
        var profile = SelectedProfile;
        _all = profile is null
            ? []
            : _profiles.GetAllModifiers(Game)
                .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
                .Select(d => new ModifierEntryViewModel(this, d, profile.Rules.GetValueOrDefault(d.Id)))
                .ToList();

        CategoryOptions.Clear();
        CategoryOptions.Add(AllCategories);
        foreach (var c in _all.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            CategoryOptions.Add(c);
        }

        if (!CategoryOptions.Contains(SelectedCategory))
        {
            SelectedCategory = AllCategories;
        }

        Combinations.Clear();
        if (profile is not null)
        {
            foreach (var c in profile.Combinations)
            {
                var names = c.ModifierIds.Select(id => _all.FirstOrDefault(e => e.Definition.Id == id)?.Description ?? id);
                Combinations.Add(new CombinationRowViewModel(c, $"{string.Join("  +  ", names)}  →  {DangerPalette.Label(c.Level)}"));
            }

            EscalateText = profile.EscalateWarningsAt?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            IsActiveProfile = profile.Id == _profiles.GetActiveProfile(Game).Id;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        Visible.Clear();
        foreach (var e in _all)
        {
            if (text.Length > 0 && !e.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SelectedCategory != AllCategories && !string.Equals(e.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SelectedLevel != AllLevels && DangerPalette.Label(e.Level) != SelectedLevel)
            {
                continue;
            }

            Visible.Add(e);
        }

        OnPropertyChanged(nameof(Summary));
    }

    internal void SetLevel(ModifierEntryViewModel entry, DangerLevel level)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        WithoutReload(() => _profiles.SetLevel(SelectedProfile.Id, entry.Definition.Id, level));
        OnPropertyChanged(nameof(Summary));
    }

    internal void SaveRule(ModifierRule rule)
    {
        if (SelectedProfile is not null)
        {
            WithoutReload(() => _profiles.UpdateRule(SelectedProfile.Id, rule));
        }
    }

    private void WithoutReload(Action action)
    {
        _suppressProfileEvents = true;
        try
        {
            action();
        }
        finally
        {
            _suppressProfileEvents = false;
        }
    }

    [RelayCommand]
    private void BulkSet(string level)
    {
        if (SelectedProfile is null || !Enum.TryParse<DangerLevel>(level, out var parsed))
        {
            return;
        }

        var targets = Visible.Where(v => v.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Status = "Select modifiers with the checkboxes first.";
            return;
        }

        WithoutReload(() => _profiles.SetLevels(SelectedProfile.Id, targets.Select(t => t.Definition.Id), parsed));
        foreach (var t in targets)
        {
            t.Level = parsed;
            t.IsSelected = false;
        }

        Status = $"{targets.Count} modifiers set to {DangerPalette.Label(parsed)}.";
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var select = Visible.Any(v => !v.IsSelected);
        foreach (var v in Visible)
        {
            v.IsSelected = select;
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        var p = _profiles.Create(Game, "New profile");
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Id == p.Id);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var p = _profiles.Duplicate(SelectedProfile.Id, SelectedProfile.Name + " copy");
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Id == p.Id);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(ProfileName))
        {
            return;
        }

        _profiles.Rename(SelectedProfile.Id, ProfileName);
        Status = "Profile renamed.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            _profiles.Delete(SelectedProfile.Id);
            SelectedProfile = null;
            ReloadProfiles();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (SelectedProfile is not null)
        {
            _profiles.SetActiveProfile(Game, SelectedProfile.Id);
            IsActiveProfile = true;
            Status = $"\"{SelectedProfile.Name}\" is now used for map checks.";
        }
    }

    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var saved = await FileDialogService.SaveTextAsync("Export profile", $"{SelectedProfile.Name}.json", _profiles.Export(SelectedProfile.Id));
        Status = saved ? "Profile exported." : null;
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var json = await FileDialogService.OpenTextAsync("Import profile");
        if (json is null)
        {
            return;
        }

        try
        {
            var p = _profiles.Import(json, Game);
            ReloadProfiles();
            SelectedProfile = Profiles.FirstOrDefault(x => x.Id == p.Id);
            Status = $"Imported \"{p.Name}\".";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            Status = "That file is not a valid Exile Lens profile.";
        }
    }

    [RelayCommand]
    private void AddCombination()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var ids = _all.Where(e => e.IsSelected).Select(e => e.Definition.Id).ToList();
        if (ids.Count < 2)
        {
            Status = "Select at least two modifiers to define a combination.";
            return;
        }

        var list = SelectedProfile.Combinations.ToList();
        list.Add(new CombinationRule { Name = string.IsNullOrWhiteSpace(CombinationName) ? "Combination" : CombinationName.Trim(), ModifierIds = ids, Level = DangerLevel.Dangerous });
        _profiles.SetCombinations(SelectedProfile.Id, list, SelectedProfile.EscalateWarningsAt);
        foreach (var e in _all)
        {
            e.IsSelected = false;
        }
    }

    [RelayCommand]
    private void RemoveCombination(CombinationRowViewModel row)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        _profiles.SetCombinations(SelectedProfile.Id, SelectedProfile.Combinations.Where(c => c.Id != row.Rule.Id), SelectedProfile.EscalateWarningsAt);
    }

    partial void OnEscalateTextChanged(string? value)
    {
        if (SelectedProfile is null || !int.TryParse(value, out var n) && !string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        int? limit = string.IsNullOrWhiteSpace(value) ? null : n;
        if (limit != SelectedProfile.EscalateWarningsAt)
        {
            WithoutReload(() => _profiles.SetCombinations(SelectedProfile.Id, SelectedProfile.Combinations, limit));
        }
    }
}
