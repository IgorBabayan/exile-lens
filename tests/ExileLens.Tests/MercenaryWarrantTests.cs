using System.Text.Json;
using System.Text.Json.Nodes;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;
using ExileLens.Infrastructure.Trade;

namespace ExileLens.Tests;

[TestClass]
public sealed class MercenaryWarrantTests
{
    // Shape of the live api/trade/data/stats "Mercenary" group: skills by name, supports as "Name (Tier N)".
    private const string StatsJson = """
        {"result":[{"id":"mercenary","label":"Mercenary","entries":[
          {"id":"mercenary.skill_34665","text":"Burning Arrow","type":"mercenary"},
          {"id":"mercenary.skill_24179","text":"Alchemist's Mark","type":"mercenary"},
          {"id":"mercenary.skill_50122","text":"Abyssal Spray","type":"mercenary"},
          {"id":"mercenary.support_52447","text":"Lesser Faster Attacks (Tier 1)","type":"mercenary"},
          {"id":"mercenary.support_987","text":"Faster Attacks (Tier 2)","type":"mercenary"},
          {"id":"mercenary.support_50485","text":"Greater Faster Attacks (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_6927","text":"DoT Multiplier (Tier 2)","type":"mercenary"},
          {"id":"mercenary.support_37460","text":"Greater DoT Multiplier (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_32052","text":"Greater Fork (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_10997","text":"Greater Ignite Chance (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_64281","text":"Swift Affliction (Tier 2)","type":"mercenary"},
          {"id":"mercenary.support_65240","text":"Greater Swift Affliction (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_1","text":"Lesser Increased Area of Effect (Tier 1)","type":"mercenary"},
          {"id":"mercenary.support_2","text":"Increased Area of Effect (Tier 2)","type":"mercenary"},
          {"id":"mercenary.support_3","text":"Greater Area of Effect (Tier 3)","type":"mercenary"},
          {"id":"mercenary.support_5293","text":"Return (Tier 3)","type":"mercenary"}
        ]},{"id":"explicit","label":"Explicit","entries":[
          {"id":"explicit.stat_life","text":"+# to maximum Life","type":"explicit"}
        ]}]}
        """;

    private static StatIndex Stats()
    {
        using var doc = JsonDocument.Parse(StatsJson);
        return new StatIndex(TradeDataProvider.ParseStats(doc.RootElement).ToList());
    }

    private static readonly TradeTypeVariant Flamequiver = new("FireBowInfamous", "mercenary_warrant");

    private static (ParsedItem Item, TradeQueryBuilder Builder, TradeQueryOptions Options, IReadOnlyList<SearchFilter> Filters) Flamequiver_()
    {
        var item = Fixture.Parse("poe1/mercenary-warrant-flamequiver.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout) { TypeVariant = Flamequiver };
        return (item, builder, options, builder.CreateDefaultFilters(item, Stats(), options));
    }

    private static string[] Ids(JsonNode group) => group["filters"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToArray();

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
        Assert.AreEqual("Mercenary Warrant (Infamous Flamequiver)", merc.TradeItemText);
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
    public void TradeSpellingOfTiersResolvesTheCopiedSupport()
    {
        var stats = Stats();
        Assert.IsTrue(stats.TryResolveMercenarySupport("Faster Attacks", 2, out var exact), "\"(Tier 2)\" on trade matches \"(Tier: 2)\" in the copy");
        Assert.AreEqual("mercenary.support_987", exact);
        CollectionAssert.AreEqual(new[] { "mercenary.support_987", "mercenary.support_50485" }, stats.ResolveMercenarySupport("Faster Attacks", 2));
        CollectionAssert.AreEqual(
            new[] { "mercenary.support_52447", "mercenary.support_987", "mercenary.support_50485" }, stats.ResolveMercenarySupport("Lesser Faster Attacks", 1));
        CollectionAssert.AreEqual(new[] { "mercenary.support_2", "mercenary.support_3" }, stats.ResolveMercenarySupport("Increased Area of Effect", 2));
        CollectionAssert.AreEqual(new[] { "mercenary.support_32052" }, stats.ResolveMercenarySupport("Greater Fork", 3));
        CollectionAssert.AreEqual(new[] { "mercenary.support_5293" }, stats.ResolveMercenarySupport("Return", 3));
        Assert.AreEqual(0, stats.ResolveMercenarySupport("Unknown Support", 2).Length);
    }

    [TestMethod]
    public void EverySkillAndSupportIsListedUnderItsSkill()
    {
        var (_, _, _, filters) = Flamequiver_();

        var skills = filters.Where(f => f.Kind == FilterKind.MercenarySkill).ToList();
        CollectionAssert.AreEqual(
            new[] { "Burning Arrow", "Alchemist's Mark", "Artillery Ballista", "Molten Well", "Dash", "Grace" }, skills.Select(s => s.Label).ToArray(),
            "skills unknown to the trade data are listed too");
        Assert.IsNull(skills.Single(s => s.Label == "Dash").StatId);

        var burning = skills[0];
        var supports = filters.Where(f => f.Kind == FilterKind.MercenarySupport && f.ParentKey == burning.Key).ToList();
        CollectionAssert.AreEqual(
            new[] { "Faster Attacks", "DoT Multiplier", "Greater Fork", "Greater Ignite Chance", "Swift Affliction" }, supports.Select(s => s.Label).ToArray());
        CollectionAssert.AreEqual(new int?[] { 2, 2, 3, 3, 2 }, supports.Select(s => s.Tier).ToArray());
        Assert.IsTrue(supports.All(s => s.StatId is not null));
        Assert.AreEqual(4, filters.Count(f => f.Kind == FilterKind.MercenarySupport && f.ParentKey == skills[3].Key), "Molten Well keeps its four supports");
        Assert.IsFalse(filters.Any(f => f.Kind == FilterKind.Corrupted));
    }

    [TestMethod]
    public void DefaultSearchIsBuildTypeAndOneLinkGroup()
    {
        var (item, builder, options, filters) = Flamequiver_();

        var burning = filters.First(f => f.Kind == FilterKind.MercenarySkill);
        Assert.IsTrue(burning.Enabled, "the main skill drives the price");
        CollectionAssert.AreEqual(
            new[] { false, false, true, true, false },
            filters.Where(f => f.ParentKey == burning.Key).Select(f => f.Enabled).ToArray(), "top-tier supports are on by default");

        var query = builder.Build(item, filters, options)["query"]!;
        Assert.AreEqual("FireBowInfamous", query["type"]!["option"]!.GetValue<string>(), "the build is the trade type");
        Assert.AreEqual("mercenary_warrant", query["type"]!["discriminator"]!.GetValue<string>());
        Assert.AreEqual(83d, query["filters"]!["misc_filters"]!["filters"]!["ilvl"]!["min"]!.GetValue<double>());

        var groups = query["stats"]!.AsArray();
        Assert.AreEqual(1, groups.Count, "anonymous searches allow a single stat group");
        Assert.AreEqual("mercenary", groups[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(3, groups[0]!["value"]!["min"]!.GetValue<int>(), "skill + two supports");
        CollectionAssert.AreEqual(new[] { "mercenary.skill_34665", "mercenary.support_32052", "mercenary.support_10997" }, Ids(groups[0]!));
    }

    [TestMethod]
    public void WithoutBuildDataTheWarrantSearchesThePlainBase()
    {
        var item = Fixture.Parse("poe1/mercenary-warrant-flamequiver.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var query = builder.Build(item, builder.CreateDefaultFilters(item, Stats(), options), options)["query"]!;
        Assert.AreEqual("Mercenary Warrant", query["type"]!.GetValue<string>(), "no discriminator without the build's trade type");
    }

    [TestMethod]
    public void LowerTierSupportAlsoMatchesItsHigherTiersInTheLinkGroup()
    {
        var (item, builder, options, filters) = Flamequiver_();
        foreach (var f in filters.Where(f => f.Kind == FilterKind.MercenarySupport))
        {
            f.Enabled = f.Label == "Faster Attacks";
        }

        var groups = builder.Build(item, filters, options)["query"]!["stats"]!.AsArray();
        Assert.AreEqual(1, groups.Count);
        CollectionAssert.AreEqual(new[] { "mercenary.skill_34665", "mercenary.support_987", "mercenary.support_50485" }, Ids(groups[0]!));
        Assert.AreEqual(2, groups[0]!["value"]!["min"]!.GetValue<int>());
    }

    [TestMethod]
    public void SeveralSkillsFoldIntoTheAndGroup()
    {
        var (item, builder, options, filters) = Flamequiver_();
        filters.Single(f => f.Label == "Alchemist's Mark").Enabled = true;
        filters.Single(f => f.Label == "Burning Arrow").Enabled = false; // pulled back in by its enabled supports

        var groups = builder.Build(item, filters, options)["query"]!["stats"]!.AsArray();
        Assert.AreEqual(1, groups.Count, "still a single stat group");
        Assert.AreEqual("and", groups[0]!["type"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { "mercenary.skill_34665", "mercenary.support_32052", "mercenary.support_10997", "mercenary.skill_24179" }, Ids(groups[0]!),
            "supports at their copied tier");
    }

    [TestMethod]
    public void ItemDataMapsWarrantBuildsToTheirTradeType()
    {
        const string json = """
            {"result":[{"id":"maps","label":"Maps","entries":[
              {"type":"Mercenary Warrant","text":"Mercenary Warrant"},
              {"type":"FireBowInfamous","text":"Mercenary Warrant (Infamous Flamequiver)","disc":"mercenary_warrant"},
              {"type":"EleBowRangerClonesNoble","text":"Mercenary Warrant (Infamous Manyshot)","disc":"mercenary_warrant"}]},
             {"id":"gems","label":"Gems","entries":[{"type":"Arc","text":"Arc of Oscillating","disc":"alt_y"}]},
             {"id":"accessory","label":"Accessories","entries":[
              {"name":"Astramentis","type":"Onyx Amulet","text":"Astramentis Onyx Amulet","flags":{"unique":true}}]}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var data = TradeDataProvider.ParseItems(doc.RootElement);

        Assert.AreEqual(Flamequiver, data.Gems["Mercenary Warrant (Infamous Flamequiver)"]);
        Assert.AreEqual(new TradeTypeVariant("EleBowRangerClonesNoble", "mercenary_warrant"), data.Gems["mercenary warrant (infamous manyshot)"]);
        Assert.AreEqual(new TradeTypeVariant("Arc", "alt_y"), data.Gems["Arc of Oscillating"]);
    }
}
