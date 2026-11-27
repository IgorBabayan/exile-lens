using ExileLens.Core.Items;
using ExileLens.Core.Trade;

namespace ExileLens.Tests;

/// <summary>Synthesised bases, Foulborn (mutated) modifiers and prefix/suffix slots of price-check filters.</summary>
[TestClass]
public sealed class ModifierOriginTests
{
    [TestMethod]
    public void SynthesisedBaseLosesItsPrefix()
    {
        var item = Fixture.Parse("poe1/synthesised-rare-shield.txt", GameId.Poe1);

        Assert.AreEqual("Harmonic Spirit Shield", item.BaseType);
        Assert.AreEqual("Foe Song", item.Name);
        Assert.IsTrue(item.IsSynthesised);
        Assert.AreEqual(ItemCategory.Equipment, item.Category);
    }

    [TestMethod]
    public void SynthesisedRareSearchesPlainBaseWithSynthesisedFilter()
    {
        var item = Fixture.Parse("poe1/synthesised-rare-shield.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);

        var query = builder.Build(item, builder.CreateDefaultFilters(item, null, options), options)["query"]!;

        Assert.AreEqual("Harmonic Spirit Shield", query["type"]!.GetValue<string>());
        Assert.AreEqual("true", query["filters"]!["misc_filters"]!["filters"]!["synthesised_item"]!["option"]!.GetValue<string>());
    }

    [TestMethod]
    public void MutatedSuffixMarksFoulbornModifier()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-belt-mutated.txt", GameId.Poe1);

        var mutated = item.Modifiers.Single(m => m.Kind == ModifierKind.Mutated);
        Assert.AreEqual("Everlasting Sacrifice", mutated.Text, "the (mutated) marker is not part of the modifier text");
        Assert.AreEqual(4, item.Modifiers.Count, "flavour text is dropped, the mutated keystone is kept");
    }

    [TestMethod]
    public void MultilineMutatedModifierKeepsBothLinesMutated()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-helmet-multiline-mutated.txt", GameId.Poe1);

        var mutated = item.Modifiers.Where(m => m.Kind == ModifierKind.Mutated).ToList();
        Assert.AreEqual(2, mutated.Count);
        Assert.IsFalse(mutated.Any(m => m.Text.Contains("(mutated)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AdvancedFoulbornHeaderMarksMutatedModifier()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-shield-advanced.txt", GameId.Poe1);

        Assert.AreEqual(ModifierKind.Mutated, item.Modifiers.Single(m => m.Text == "Damage cannot be Reflected").Kind);
        Assert.AreEqual(ModifierKind.Explicit, item.Modifiers.Single(m => m.Pattern == "# to maximum Life").Kind);
    }

    [TestMethod]
    public void UnscalableValueNoteIsNotPartOfTheModifier()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-shield-advanced.txt", GameId.Poe1);

        Assert.IsFalse(item.Modifiers.Any(m => m.Text.Contains("Unscalable", StringComparison.Ordinal)));
        Assert.AreEqual("Chaos Damage taken does not bypass Energy Shield while not on Low Life",
            item.Modifiers.Single(m => m.Text.StartsWith("Chaos Damage", StringComparison.Ordinal)).Text);
        Assert.AreEqual("Reflect Shocks applied to you to all Enemies within # metres",
            item.Modifiers.Single(m => m.Text.StartsWith("Reflect", StringComparison.Ordinal)).Pattern);
    }

    [TestMethod]
    [DataRow("Damage cannot be Reflected — Unscalable Value (mutated)")]
    [DataRow("Damage cannot be Reflected (mutated) — Unscalable Value")]
    public void UnscalableNoteAndMutatedMarkerAreStrippedInEitherOrder(string line)
    {
        var body = ModifierText.StripKindSuffix(line, out var kind).ToString();

        Assert.AreEqual("Damage cannot be Reflected", body);
        Assert.AreEqual(ModifierKind.Mutated, kind);
    }

    [TestMethod]
    public void FoulbornUniqueUsesMutatedTradeFilter()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-shield-advanced.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);

        var misc = builder.Build(item, builder.CreateDefaultFilters(item, null, options), options)["query"]!["filters"]!["misc_filters"]!["filters"]!;

        Assert.AreEqual("true", misc["mutated"]!["option"]!.GetValue<string>());
        Assert.IsNull(misc["foulborn_item"]);
    }

    [TestMethod]
    public void MutatedModifierResolvesThroughExplicitStats()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-shield-advanced.txt", GameId.Poe1);
        var stats = new StatIndex(
        [
            new TradeStat("explicit.stat_no_reflect", "Damage cannot be Reflected", "explicit"),
            new TradeStat("explicit.stat_life", "+# to maximum Life", "explicit"),
        ]);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);

        var filter = builder.CreateDefaultFilters(item, stats, options).Single(f => f.StatId == "explicit.stat_no_reflect");

        Assert.AreEqual(ModifierKind.Mutated, filter.ModifierKind);
        Assert.AreEqual("Damage cannot be Reflected", filter.Label);
    }

    [TestMethod]
    public void AdvancedCopyGivesFiltersTheirAffixSlot()
    {
        var item = Fixture.Parse("poe1/rare-gloves-advanced.txt", GameId.Poe1);
        var stats = new StatIndex(
        [
            new TradeStat("explicit.stat_life", "+# to maximum Life", "explicit"),
            new TradeStat("fractured.stat_life", "+# to maximum Life", "fractured"),
            new TradeStat("explicit.stat_es", "+# to maximum Energy Shield (Local)", "explicit"),
            new TradeStat("explicit.stat_fire", "+#% to Fire Resistance", "explicit"),
            new TradeStat("crafted.stat_as", "#% increased Attack Speed", "crafted"),
        ]);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);

        var filters = builder.CreateDefaultFilters(item, stats, options).Where(f => f.Kind == FilterKind.Stat).ToList();

        Assert.AreEqual(4, filters.Count);
        var life = filters.Single(f => f.StatId == "fractured.stat_life");
        Assert.AreEqual(ModifierKind.Fractured, life.ModifierKind);
        Assert.AreEqual(AffixSlot.Prefix, life.Slot);
        Assert.AreEqual(AffixSlot.Prefix, filters.Single(f => f.StatId == "explicit.stat_es").Slot);
        Assert.AreEqual(AffixSlot.Suffix, filters.Single(f => f.StatId == "explicit.stat_fire").Slot);
        var crafted = filters.Single(f => f.StatId == "crafted.stat_as");
        Assert.AreEqual(ModifierKind.Crafted, crafted.ModifierKind);
        Assert.AreEqual(AffixSlot.Suffix, crafted.Slot);
    }

    [TestMethod]
    public void SimpleCopyLeavesAffixSlotUnknown()
    {
        var item = Fixture.Parse("poe1/synthesised-rare-shield.txt", GameId.Poe1);
        var stats = new StatIndex([new TradeStat("explicit.stat_es", "#% increased Energy Shield", "explicit")]);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);

        var filter = builder.CreateDefaultFilters(item, stats, options).Single(f => f.Kind == FilterKind.Stat);

        Assert.IsNull(filter.Slot);
    }
}
