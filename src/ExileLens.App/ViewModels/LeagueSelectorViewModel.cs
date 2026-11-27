using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Settings;

namespace ExileLens.App.ViewModels;

/// <summary>An entry of the league picker. <see cref="Id"/> is null for "Automatic" and "Custom".</summary>
public sealed record LeagueOption(string? Id, string Label, bool IsCustom = false);

/// <summary>League picker for one game, shown on the Settings page.</summary>
public sealed partial class LeagueSelectorViewModel : ObservableObject
{
    private static readonly LeagueOption Automatic = new(null, "Automatic (current trade league)");
    private static readonly LeagueOption Custom = new(null, "Custom / private league…", IsCustom: true);

    private readonly ISettingsService _settings;
    private readonly ILeagueService _leagues;
    private readonly IGameModule _module;
    private IReadOnlyList<LeagueInfo>? _shown;
    private int _loadVersion;
    private bool _syncing;

    public LeagueSelectorViewModel(IGameModule module, ISettingsService settings, ILeagueService leagues)
    {
        _module = module;
        _settings = settings;
        _leagues = leagues;
        SyncSelection();
    }

    public string GameName => _module.DisplayName;

    public ObservableCollection<LeagueOption> Options { get; } = [Automatic, Custom];

    [ObservableProperty]
    private LeagueOption? _selected;

    [ObservableProperty]
    private string _customLeague = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool IsCustomSelected => Selected?.IsCustom == true;

    private string? Configured => _settings.Current.For(_module.Game).League;

    /// <summary>Loads the league list. Without <paramref name="force"/> the cached list is used and the picker is only rebuilt when it changed.</summary>
    public async Task LoadAsync(bool force)
    {
        var version = ++_loadVersion;
        if (force || _shown is null)
        {
            Status = "Loading leagues…";
        }

        var leagues = await _leagues.GetLeaguesAsync(_module, CancellationToken.None, force);
        if (version != _loadVersion)
        {
            return; // a newer load superseded this one
        }

        if (!ReferenceEquals(leagues, _shown))
        {
            _shown = leagues;
            Rebuild(leagues);
        }
        else
        {
            SyncSelection();
        }
    }

    partial void OnSelectedChanged(LeagueOption? value)
    {
        OnPropertyChanged(nameof(IsCustomSelected));
        if (_syncing || value is null)
        {
            return;
        }

        if (value.IsCustom)
        {
            // Keep the previous league until an ID is actually entered.
            if (!string.IsNullOrWhiteSpace(CustomLeague))
            {
                Save(CustomLeague.Trim());
            }
        }
        else
        {
            Save(value.Id);
        }

        UpdateStatus();
    }

    partial void OnCustomLeagueChanged(string value)
    {
        if (_syncing || !IsCustomSelected)
        {
            return;
        }

        Save(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        UpdateStatus();
    }

    private void Rebuild(IReadOnlyList<LeagueInfo> leagues)
    {
        _syncing = true;
        try
        {
            Options.Clear();
            Options.Add(Automatic);
            foreach (var league in leagues)
            {
                Options.Add(new LeagueOption(league.Id, league.Name));
            }

            Options.Add(Custom);
        }
        finally
        {
            _syncing = false;
        }

        SyncSelection();
    }

    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            var configured = Configured;
            LeagueOption selected;
            if (string.IsNullOrWhiteSpace(configured))
            {
                selected = Automatic;
            }
            else
            {
                selected = FindOption(configured) ?? Custom;
                if (selected.IsCustom)
                {
                    CustomLeague = configured;
                }
            }

            Selected = selected;
        }
        finally
        {
            _syncing = false;
        }

        UpdateStatus();
    }

    private LeagueOption? FindOption(string id)
    {
        foreach (var option in Options)
        {
            if (option.Id is not null && string.Equals(option.Id, id, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return null;
    }

    private void Save(string? league)
    {
        if (string.Equals(Configured, league, StringComparison.Ordinal))
        {
            return; // avoid a settings write (and Changed fan-out) when nothing changed
        }

        var game = _module.Game;
        _settings.Update(s => s.For(game).League = league);
    }

    private void UpdateStatus()
    {
        var active = _leagues.GetActiveLeague(_module.Game);
        if (_shown is { Count: 0 })
        {
            Status = $"Prices use: {active} · the league list could not be loaded, use Refresh to retry.";
        }
        else if (IsCustomSelected && string.IsNullOrWhiteSpace(CustomLeague))
        {
            Status = $"Prices use: {active} · enter the league ID exactly as on the trade site.";
        }
        else
        {
            Status = $"Prices use: {active}";
        }
    }
}
