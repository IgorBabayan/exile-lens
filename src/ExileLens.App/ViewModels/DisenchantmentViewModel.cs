using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Settings;
using ExileLens.Core.Rewards;

namespace ExileLens.App.ViewModels;

public sealed record RuleRow(string Kind, string Description, string Reward);

public sealed record UnitRow(string Name, string Detail, string Value);

/// <summary>Shows the versioned reward rules in effect and the user's valuation preferences.</summary>
public sealed partial class DisenchantmentViewModel(
    IGameRegistry games,
    IGameDetectionService detection,
    ISettingsService settings,
    ILeagueService leagues,
    ICurrencyConversionService rates) : PageViewModel
{
    public IReadOnlyList<GameOption> GameOptions { get; } = games.All.Select(g => new GameOption(g.Game, g.DisplayName)).ToList();

    public ObservableCollection<RuleRow> Rules { get; } = [];

    public ObservableCollection<UnitRow> Units { get; } = [];

    public ObservableCollection<string> Sources { get; } = [];

    [ObservableProperty]
    private GameOption? _selectedGame;

    [ObservableProperty]
    private string _rulesVersion = string.Empty;

    [ObservableProperty]
    private string _mechanicDescription = string.Empty;

    public bool DisenchantEnabled
    {
        get => settings.Current.Rewards.DisenchantEnabled;
        set => Set(s => s.Rewards.DisenchantEnabled = value);
    }

    public bool SalvageEnabled
    {
        get => settings.Current.Rewards.SalvageEnabled;
        set => Set(s => s.Rewards.SalvageEnabled = value);
    }

    public bool ShowComparison
    {
        get => settings.Current.Rewards.ShowComparison;
        set => Set(s => s.Rewards.ShowComparison = value);
    }

    public bool ShowSuggestedAction
    {
        get => settings.Current.Rewards.ShowSuggestedAction;
        set => Set(s => s.Rewards.ShowSuggestedAction = value);
    }

    public string ValuationCurrency
    {
        get => settings.Current.Rewards.ValuationCurrency ?? string.Empty;
        set => Set(s => s.Rewards.ValuationCurrency = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string MinimumTradeValue
    {
        get => Formatting.Amount(settings.Current.Trade.MinimumTradeValue);
        set
        {
            if (decimal.TryParse(value?.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0)
            {
                Set(s => s.Trade.MinimumTradeValue = d);
            }
        }
    }

    public override void OnActivated()
    {
        var game = detection.Resolve(null);
        if (SelectedGame is null)
        {
            SelectedGame = GameOptions.FirstOrDefault(o => o.Game == game) ?? GameOptions[0];
        }
        else
        {
            Load();
        }
    }

    partial void OnSelectedGameChanged(GameOption? value) => Load();

    private void Load()
    {
        if (SelectedGame is null)
        {
            return;
        }

        var module = games.Get(SelectedGame.Game);
        var r = module.RewardRules;
        RulesVersion = $"Rules verified for {module.DisplayName} {r.RulesVersion}";
        MechanicDescription = module.HasDisenchanting
            ? "Disenchanting destroys an item at a vendor for currency shards. Salvaging (a separate bench) destroys equipment for quality currency and Artificer's Shards — the two are never combined."
            : "Unique items disenchant at Rog (Kingsmarch) into Thaumaturgic Dust: base value per unique × item level (65–84) × quality (+2% per 1%), influences and corruption implicits (+50% each). Single-item vendor recipes are estimated too.";

        Rules.Clear();
        foreach (var rule in r.Disenchant)
        {
            Rules.Add(new RuleRow("Disenchant", rule.Description ?? rule.Id, Describe(rule.Rewards)));
        }

        foreach (var rule in r.Vendor)
        {
            Rules.Add(new RuleRow("Vendor", rule.Description ?? rule.Id, Describe(rule.Rewards)));
        }

        if (r.Salvage is { } s)
        {
            Rules.Add(new RuleRow("Salvage", "Per rune socket", $"{Formatting.Amount(s.PerSocket)} {s.SocketCurrency}"));
            foreach (var q in s.Quality)
            {
                Rules.Add(new RuleRow("Salvage", $"Quality on {string.Join(", ", q.ItemClasses.Take(4))}{(q.ItemClasses.Count > 4 ? "…" : string.Empty)}",
                    $"1 {q.Currency} per {s.QualityStep}% quality (+ chance between steps)"));
            }
        }

        Sources.Clear();
        foreach (var src in r.Sources)
        {
            Sources.Add(src);
        }

        var league = leagues.GetActiveLeague(module.Game);
        var snapshot = rates.Peek(module.Game, league);
        var valuation = string.IsNullOrWhiteSpace(settings.Current.Rewards.ValuationCurrency) ? module.DefaultValuationCurrency : settings.Current.Rewards.ValuationCurrency!;
        Units.Clear();
        foreach (var u in r.Units)
        {
            var value = RewardValuation.UnitValue(u.Name, r, snapshot, valuation);
            Units.Add(new UnitRow(u.Name, $"{u.StackToFull} make one {u.FullCurrency}",
                value is { } v ? $"{Formatting.Amount(v)} {Formatting.Plural(valuation, v)}" : "Rate unavailable"));
        }
    }

    private static string Describe(IEnumerable<RewardItem> rewards) =>
        string.Join(", ", rewards.Select(x => $"{Formatting.Amount(x.Quantity)} {Formatting.Plural(x.Currency, x.Quantity)}"));

    private void Set(Action<AppSettings> mutate, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        settings.Update(mutate);
        OnPropertyChanged(name);
    }
}
