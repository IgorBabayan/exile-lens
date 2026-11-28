using System.Text.Json;
using System.Text.Json.Nodes;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;
using ExileLens.Infrastructure.Trade;

namespace ExileLens.Tests;

[TestClass]
public sealed class MercenaryWarrantTests
{
    private const string StatsJson = """
        {"result":[{"id":"mercenary","label":"Mercenary","entries":[
          {"id":"mercenary.skill_34665","text":"Burning Arrow","type":"mercenary"},
          {"id":"mercenary.skill_24179","text":"Alchemist's Mark","type":"mercenary"},
          {"id":"mercenary.skill_50122","text":"Abyssal Spray","type":"mercenary"},
          {"id":"mercenary.support_52447","text":"Lesser Faster Attacks","type":"mercenary"},
          {"id":"mercenary.support_987","text":"Faster Attacks","type":"mercenary"},
          {"id":"mercenary.support_50485","text":"Greater Faster Attacks","type":"mercenary"},
          {"id":"mercenary.support_32052","text":"Greater Fork","type":"mercenary"},
          {"id":"mercenary.support_10997","text":"Greater Ignite Chance","type":"mercenary"},
          {"id":"mercenary.support_50303","text":"Ignite Chance","type":"mercenary"},
          {"id":"mercenary.support_1","text":"Lesser Increased Area of Effect","type":"mercenary"},
          {"id":"mercenary.support_2","text":"Increased Area of Effect","type":"mercenary"},
          {"id":"mercenary.support_3","text":"Greater Area of Effect","type":"mercenary"},
          {"id":"mercenary.support_5293","text":"Return (Tier: 3)","type":"mercenary"}
        ]},{"id":"explicit","label":"Explicit","entries":[
          {"id":"explicit.stat_life","text":"+# to maximum Life","type":"explicit"}
        ]}]}
        """;

    private static StatIndex Stats()
    {
        using var doc = JsonDocument.Parse(StatsJson);
        return new StatIndex(TradeDataProvider.ParseStats(doc.RootElement).ToList());
    }

    [TestMethod]
    public void WarrantParsesMercenarySkillsAndSupports()
    {
        var item = Fixture.Parse("poe1/mercenary-warrant-flamequiver.txt", GameId.Poe1);

        Assert.AreEqual(ItemCategory.MercenaryWarrant, item.Category);
        Assert.AreEqual("Mercenary Warrant", item.BaseType);
        Assert.AreEqual("Alara, the Cyaxan Sister", item.Name);
        Assert.AreEqual(83, item.ItemLevel);
        Assert.AreEqual(0, item.Modifiers.Count, "skills are not item modifiers");

        var merc = item.Mercenary!;
        Assert.AreEqual("Infamous Flamequiver", merc.Build);
        Assert.IsTrue(merc.IsInfamous);
        Assert.AreEqual(83, merc.Level);
        CollectionAssert.AreEqual(
            new[] { "Burning Arrow", "Alchemist's Mark", "Artillery Ballista", "Molten Well", "Dash", "Grace" },
            merc.Skills.Select(s => s.Name).ToArray());

        var burning = merc.Skills[0];
        Assert.AreEqual(5, burning.Supports.Count);
        Assert.AreEqual(new MercenarySupport("Faster Attacks", 2), burning.Supports[0]);
        Assert.AreEqual(new MercenarySupport("Greater Fork", 3), burning.Supports[2]);
        Assert.AreEqual(0, merc.Skills[1].Supports.Count);
    }

    [TestMethod]
    public void WarrantIsNotBulkPricedAndSecondExampleParses()
    {
        Assert.IsFalse(Games.Poe1.BulkPricedCategories.Contains(ItemCategory.MercenaryWarrant), "every warrant is a different mercenary");
        Assert.IsTrue(Games.Poe1.SupportedCategories.Contains(ItemCategory.MercenaryWarrant));

        var item = Fixture.Parse("poe1/mercenary-warrant-chaplain.txt", GameId.Poe1);
        var merc = item.Mercenary!;
        Assert.AreEqual("Callus, Lich Lord's Vassal", item.Name);
        Assert.AreEqual("Chaplain of the Well", merc.Build);
        Assert.IsFalse(merc.IsInfamous);
        Assert.AreEqual(6, merc.Skills.Count);
        Assert.AreEqual(new MercenarySupport("Return", 3), merc.Skills[0].Supports[1]);
    }

    [TestMethod]
    public void SupportResolvesItsTierAndBetter()
    {
        var stats = Stats();
        CollectionAssert.AreEqual(new[] { "mercenary.support_987", "mercenary.support_50485" }, stats.ResolveMercenarySupport("Faster Attacks", 2));
        CollectionAssert.AreEqual(
            new[] { "mercenary.support_52447", "mercenary.support_987", "mercenary.support_50485" }, stats.ResolveMercenarySupport("Lesser Faster Attacks", 1));
        CollectionAssert.AreEqual(new[] { "mercenary.support_2", "mercenary.support_3" }, stats.ResolveMercenarySupport("Increased Area of Effect", 2));
        CollectionAssert.AreEqual(new[] { "mercenary.support_32052" }, stats.ResolveMercenarySupport("Greater Fork", 3));
        CollectionAssert.AreEqual(new[] { "mercenary.support_5293" }, stats.ResolveMercenarySupport("Return", 3), "a tier suffix in the trade text is ignored");
        Assert.AreEqual(0, stats.ResolveMercenarySupport("Unknown Support", 2).Length);
    }

    [TestMethod]
    public void WarrantSearchesMainSkillWithTopTierSupportsInOneLink()
    {
        var item = Fixture.Parse("poe1/mercenary-warrant-flamequiver.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var filters = builder.CreateDefaultFilters(item, Stats(), options);

        var skill = filters.Single(f => f is { Kind: FilterKind.MercenarySkill, Label: "Burning Arrow" });
        Assert.IsTrue(skill.Enabled, "the main skill drives the price");
        Assert.IsFalse(filters.Single(f => f is { Kind: FilterKind.MercenarySkill, Label: "Alchemist's Mark" }).Enabled);
        var supports = filters.Where(f => f.Kind == FilterKind.MercenarySupport && f.ParentKey == skill.Key).ToList();
        CollectionAssert.AreEqual(
            new[] { "Faster Attacks (Tier 2)", "Greater Fork (Tier 3)", "Greater Ignite Chance (Tier 3)" }, supports.Select(s => s.Label).ToArray(),
            "supports unknown to the trade data are left out");
        CollectionAssert.AreEqual(new[] { false, true, true }, supports.Select(s => s.Enabled).ToArray(), "top-tier supports are on by default");
        Assert.IsFalse(filters.Any(f => f.Kind == FilterKind.Corrupted));

        var query = builder.Build(item, filters, options)["query"]!;
        Assert.AreEqual("Mercenary Warrant", query["type"]!["option"]!.GetValue<string>());
        Assert.AreEqual("mercenary_warrant", query["type"]!["discriminator"]!.GetValue<string>());
        Assert.AreEqual(83d, query["filters"]!["misc_filters"]!["filters"]!["ilvl"]!["min"]!.GetValue<double>());

        var groups = query["stats"]!.AsArray();
        Assert.AreEqual(0, groups[0]!["filters"]!.AsArray().Count, "the linked skill is searched inside its mercenary group");
        var link = groups[1]!;
        Assert.AreEqual("mercenary", link["type"]!.GetValue<string>());
        Assert.AreEqual(3, link["value"]!["min"]!.GetValue<int>(), "skill + two supports");
        CollectionAssert.AreEqual(
            new[] { "mercenary.skill_34665", "mercenary.support_32052", "mercenary.support_10997" },
            link["filters"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public void EnabledSupportPullsInItsSkillAndLoneSkillIsAnAndFilter()
    {
        var item = Fixture.Parse("poe1/mercenary-warrant-flamequiver.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var filters = builder.CreateDefaultFilters(item, Stats(), options);
        foreach (var f in filters.Where(f => f.Kind == FilterKind.MercenarySupport))
        {
            f.Enabled = false;
        }

        filters.Single(f => f.Label == "Alchemist's Mark").Enabled = true;
        filters.Single(f => f.Label == "Burning Arrow").Enabled = false;
        filters.Single(f => f.Label == "Faster Attacks (Tier 2)").Enabled = true;

        var stats = builder.Build(item, filters, options)["query"]!["stats"]!.AsArray();
        Assert.AreEqual(2, stats.Count);
        CollectionAssert.AreEqual(new[] { "mercenary.skill_24179" }, stats[0]!["filters"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "mercenary.skill_34665", "mercenary.support_987", "mercenary.support_50485" },
            stats[1]!["filters"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToArray(), "a tier-2 support also matches its tier-3 version");
        Assert.AreEqual(2, stats[1]!["value"]!["min"]!.GetValue<int>());
    }
}
