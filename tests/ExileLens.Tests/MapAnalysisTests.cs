using ExileLens.Core.Items;
using ExileLens.Core.Maps;

namespace ExileLens.Tests;

[TestClass]
public sealed class MapAnalysisTests
{
    private static readonly MapModifierAnalyzer Analyzer = new();
    private static MapModifierCatalog Poe2Catalog => new(GameId.Poe2, Games.Poe2.MapModifiers);

    private static ModifierProfile Deadeye()
    {
        var p = new ModifierProfile { Name = "Lightning Arrow Deadeye", Game = GameId.Poe2 };
        p.Rules["poe2.recovery"] = new ModifierRule { ModifierId = "poe2.recovery", Level = DangerLevel.Dangerous };
        p.Rules["poe2.extra-fire"] = new ModifierRule { ModifierId = "poe2.extra-fire", Level = DangerLevel.Warning };
        p.Rules["poe2.attack-speed"] = new ModifierRule { ModifierId = "poe2.attack-speed", Level = DangerLevel.Warning };
        p.Rules["poe2.increased-life"] = new ModifierRule { ModifierId = "poe2.increased-life", Level = DangerLevel.Safe };
        p.Rules["poe2.stun-threshold"] = new ModifierRule { ModifierId = "poe2.stun-threshold", Level = DangerLevel.Safe };
        return p;
    }

    [TestMethod]
    public void MockupWaystoneIsDangerousWithExpectedCountsAndOrder()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var result = Analyzer.Analyze(item, Deadeye(), Poe2Catalog);

        Assert.AreEqual(DangerLevel.Dangerous, result.Overall);
        Assert.AreEqual(2, result.SafeCount);
        Assert.AreEqual(2, result.WarningCount);
        Assert.AreEqual(1, result.DangerousCount);
        Assert.AreEqual(0, result.UnclassifiedCount);
        CollectionAssert.AreEqual(
            new[] { DangerLevel.Dangerous, DangerLevel.Warning, DangerLevel.Warning, DangerLevel.Safe, DangerLevel.Safe },
            result.Modifiers.Select(m => m.Level).ToArray());
        Assert.IsTrue(result.Modifiers.All(m => m.IsKnown));
    }

    [TestMethod]
    public void UnclassifiedModifiersMakeMapGrayNeverGreen()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var profile = new ModifierProfile { Game = GameId.Poe2 };
        profile.Rules["poe2.increased-life"] = new ModifierRule { ModifierId = "poe2.increased-life", Level = DangerLevel.Safe };

        var result = Analyzer.Analyze(item, profile, Poe2Catalog);

        Assert.AreEqual(DangerLevel.Unclassified, result.Overall);
        Assert.AreEqual(4, result.UnclassifiedCount);
    }

    [TestMethod]
    public void AllSafeMapIsGreen()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var profile = Deadeye();
        foreach (var rule in profile.Rules.Values)
        {
            rule.Level = DangerLevel.Safe;
        }

        Assert.AreEqual(DangerLevel.Safe, Analyzer.Analyze(item, profile, Poe2Catalog).Overall);
    }

    [TestMethod]
    public void UnidentifiedMapIsNeverSafe()
    {
        var item = Fixture.Parse("poe2/waystone-unidentified.txt", GameId.Poe2);
        var result = Analyzer.Analyze(item, Deadeye(), Poe2Catalog);

        Assert.AreEqual(DangerLevel.Unclassified, result.Overall);
        Assert.IsTrue(result.ModifiersUnavailable);
        Assert.IsNotNull(result.Note);
    }

    [TestMethod]
    public void NonMapItemIsReportedNotAnalyzed()
    {
        var item = Fixture.Parse("poe2/rare-ring-simple.txt", GameId.Poe2);
        var result = Analyzer.Analyze(item, Deadeye(), Poe2Catalog);
        Assert.IsTrue(result.ModifiersUnavailable);
        Assert.AreNotEqual(DangerLevel.Safe, result.Overall);
    }

    [TestMethod]
    public void ThresholdRulesDependOnValue()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2); // 40% less recovery
        var profile = Deadeye();
        profile.Rules["poe2.recovery"] = new ModifierRule
        {
            ModifierId = "poe2.recovery",
            Level = DangerLevel.Warning,
            Thresholds =
            [
                new ValueThreshold { Max = 30, Level = DangerLevel.Warning },
                new ValueThreshold { Min = 31, Level = DangerLevel.Dangerous },
            ],
        };
        var result = Analyzer.Analyze(item, profile, Poe2Catalog);
        Assert.AreEqual(DangerLevel.Dangerous, result.Modifiers.Single(m => m.ModifierId == "poe2.recovery").Level);

        profile.Rules["poe2.recovery"].Thresholds[1].Min = 50;
        result = Analyzer.Analyze(item, profile, Poe2Catalog);
        Assert.AreEqual(DangerLevel.Warning, result.Modifiers.Single(m => m.ModifierId == "poe2.recovery").Level,
            "40 matches neither threshold → falls back to the base level");
    }

    [TestMethod]
    public void NegativeValuesUseMagnitudeForThresholds()
    {
        var item = Fixture.Parse("poe1/cemetery-map.txt", GameId.Poe1); // -12% max res
        var catalog = new MapModifierCatalog(GameId.Poe1, Games.Poe1.MapModifiers);
        var profile = new ModifierProfile { Game = GameId.Poe1 };
        profile.Rules["poe1.max-res"] = new ModifierRule
        {
            ModifierId = "poe1.max-res",
            Level = DangerLevel.Safe,
            Thresholds = [new ValueThreshold { Min = 10, Max = 15, Level = DangerLevel.Warning }],
        };

        var result = Analyzer.Analyze(item, profile, catalog);
        Assert.AreEqual(DangerLevel.Warning, result.Modifiers.Single(m => m.ModifierId == "poe1.max-res").Level);
    }

    [TestMethod]
    public void SameModifierCanBeRedForOneBuildAndGreenForAnother()
    {
        var item = Fixture.Parse("poe1/cemetery-map.txt", GameId.Poe1);
        var catalog = new MapModifierCatalog(GameId.Poe1, Games.Poe1.MapModifiers);
        var melee = new ModifierProfile { Name = "Melee", Game = GameId.Poe1 };
        melee.Rules["poe1.no-regen"] = new ModifierRule { ModifierId = "poe1.no-regen", Level = DangerLevel.Dangerous };
        var caster = melee.Clone("Caster");
        caster.Rules["poe1.no-regen"].Level = DangerLevel.Safe;

        Assert.AreEqual(DangerLevel.Dangerous, Analyzer.Analyze(item, melee, catalog).Modifiers[0].Level);
        Assert.AreEqual(DangerLevel.Safe, Analyzer.Analyze(item, caster, catalog).Modifiers.Single(m => m.ModifierId == "poe1.no-regen").Level);
        Assert.AreEqual(DangerLevel.Dangerous, melee.Rules["poe1.no-regen"].Level, "clone is deep");
    }

    [TestMethod]
    public void CombinationOfWarningsEscalatesToDangerous()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var profile = Deadeye();
        profile.Rules["poe2.recovery"].Level = DangerLevel.Safe;
        profile.Combinations.Add(new CombinationRule
        {
            Name = "Fire + speed",
            ModifierIds = ["poe2.extra-fire", "poe2.attack-speed"],
            Level = DangerLevel.Dangerous,
        });

        var result = Analyzer.Analyze(item, profile, Poe2Catalog);
        Assert.AreEqual(DangerLevel.Dangerous, result.Overall);
        Assert.AreEqual("Fire + speed", result.Combinations.Single().Name);
        Assert.AreEqual(0, result.DangerousCount, "individual modifiers keep their own level");
    }

    [TestMethod]
    public void CombinationDoesNotTriggerWhenOneModifierMissing()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var profile = Deadeye();
        profile.Rules["poe2.recovery"].Level = DangerLevel.Safe;
        profile.Combinations.Add(new CombinationRule { Name = "x", ModifierIds = ["poe2.extra-fire", "poe2.extra-cold"] });

        var result = Analyzer.Analyze(item, profile, Poe2Catalog);
        Assert.AreEqual(DangerLevel.Warning, result.Overall);
        Assert.AreEqual(0, result.Combinations.Count);
    }

    [TestMethod]
    public void WarningCountEscalation()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var profile = Deadeye();
        profile.Rules["poe2.recovery"].Level = DangerLevel.Safe;
        profile.EscalateWarningsAt = 2;

        Assert.AreEqual(DangerLevel.Dangerous, Analyzer.Analyze(item, profile, Poe2Catalog).Overall);
    }

    [TestMethod]
    public void UnknownModifiersGetStableAutoIds()
    {
        var catalog = new MapModifierCatalog(GameId.Poe2, []);
        Assert.IsFalse(catalog.ResolveId("Monsters are Spooky", out var id1));
        Assert.IsFalse(catalog.ResolveId("monsters  are spooky", out var id2));
        Assert.AreEqual(id1, id2);
        StringAssert.StartsWith(id1, "auto:");
    }

    [TestMethod]
    public void CatalogMatchesPlusSignedPatterns()
    {
        Assert.IsTrue(Poe2Catalog.ResolveId(ModifierText.Normalize("+35% to Monster Critical Damage Bonus"), out var id));
        Assert.AreEqual("poe2.crit-damage", id);
    }

    [TestMethod]
    public void DangerLevelCyclesForSingleClickClassification()
    {
        var level = DangerLevel.Unclassified;
        level = level.Next();
        Assert.AreEqual(DangerLevel.Safe, level);
        level = level.Next().Next().Next();
        Assert.AreEqual(DangerLevel.Unclassified, level);
    }
}
