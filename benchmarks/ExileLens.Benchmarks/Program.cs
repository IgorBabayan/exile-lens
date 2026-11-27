using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ExileLens.Application.Storage;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;
using ExileLens.Game.Poe2;

BenchmarkSwitcher.FromAssembly(typeof(ParserBenchmarks).Assembly).Run(args);

/// <summary>Targets: parsing &lt; 5 ms, map analysis &lt; 10 ms, disenchant &lt; 5 ms (spec §10). Typical results are in microseconds.</summary>
[MemoryDiagnoser]
public class ParserBenchmarks
{
    private readonly ItemTextParser _parser = new();
    private string _advanced = string.Empty;
    private string _waystone = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _advanced = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "poe2", "rare-body-hybrid-advanced.txt"));
        _waystone = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "poe2", "waystone-t15.txt"));
    }

    [Benchmark]
    public ParsedItem? ParseAdvancedRare()
    {
        _parser.TryParse(_advanced, GameId.Poe2, out var item, out _);
        return item;
    }

    [Benchmark]
    public ParsedItem? ParseWaystone()
    {
        _parser.TryParse(_waystone, GameId.Poe2, out var item, out _);
        return item;
    }
}

[MemoryDiagnoser]
public class AnalysisBenchmarks
{
    private readonly MapModifierAnalyzer _analyzer = new();
    private ParsedItem _waystone = null!;
    private ParsedItem _rare = null!;
    private MapModifierCatalog _catalog = null!;
    private ModifierProfile _profile = null!;
    private DisenchantmentEstimator _disenchant = null!;
    private CurrencyRates _rates = null!;
    private StatIndex _stats = null!;

    [GlobalSetup]
    public void Setup()
    {
        var parser = new ItemTextParser();
        parser.TryParse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "poe2", "waystone-t15.txt")), GameId.Poe2, out var w, out _);
        parser.TryParse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "poe2", "rare-body-hybrid-advanced.txt")), GameId.Poe2, out var r, out _);
        _waystone = w!;
        _rare = r!;
        var module = new Poe2GameModule(new AppPaths(Path.Combine(Path.GetTempPath(), "exilelens-bench")));
        _catalog = new MapModifierCatalog(GameId.Poe2, module.MapModifiers);
        _profile = new ModifierProfile { Game = GameId.Poe2 };
        foreach (var d in module.MapModifiers)
        {
            _profile.Rules[d.Id] = new ModifierRule { ModifierId = d.Id, Level = DangerLevel.Warning };
        }

        _disenchant = new DisenchantmentEstimator(module.RewardRules);
        _rates = new CurrencyRates("Exalted Orb", [new("Regal Orb", 0.4m)], DateTimeOffset.UtcNow, "bench");

        // A realistic stat dataset is ~10k entries; synthesize a similar volume.
        var stats = Enumerable.Range(0, 10_000).Select(i => new TradeStat($"explicit.stat_{i}", $"+# to Synthetic Stat {i}", "explicit"))
            .Append(new TradeStat("explicit.stat_life", "+# to maximum Life", "explicit"));
        _stats = new StatIndex(stats);
    }

    [Benchmark]
    public MapAnalysisResult AnalyzeWaystone() => _analyzer.Analyze(_waystone, _profile, _catalog);

    [Benchmark]
    public RewardEstimate? Disenchant() => _disenchant.Estimate(_rare, _rates, "Exalted Orb");

    [Benchmark]
    public bool ResolveStat() => _stats.TryResolve("explicit", "# to maximum Life", false, out _);
}
