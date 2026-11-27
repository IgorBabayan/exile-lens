using System.Text.Json;
using System.Text.Json.Nodes;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;
using ExileLens.Infrastructure.Trade;

namespace ExileLens.Tests;

[TestClass]
public sealed class PriceEstimationTests
{
    private static MarketListing L(decimal value, string? account = null) =>
        new(Guid.NewGuid().ToString("N"), new CurrencyAmount(value, "Exalted Orb"), value, account ?? Guid.NewGuid().ToString("N"), true, null, null);

    [TestMethod]
    public void BaitListingsBelowMedianAreIgnored()
    {
        var listings = new List<MarketListing> { L(0.1m), L(0.2m), L(12), L(12), L(13), L(14), L(15), L(16), L(16), L(18) };
        var e = ListingPriceEstimator.Estimate(listings, 24, "Exalted Orb", DateTimeOffset.UtcNow);

        Assert.AreEqual(0.1m, e.Lowest, "raw lowest is still reported");
        Assert.IsTrue(e.RangeLow >= 12m, $"range low {e.RangeLow} must ignore bait");
        Assert.AreEqual(24, e.MatchingListings);
        Assert.AreEqual(8, e.ConsideredListings);
        Assert.IsTrue(e.IsListingPrice);
    }

    [TestMethod]
    public void OneListingPerSellerIsConsidered()
    {
        var listings = new List<MarketListing> { L(5, "flooder"), L(5, "flooder"), L(5, "flooder"), L(9, "a"), L(10, "b") };
        var e = ListingPriceEstimator.Estimate(listings, 5, "Exalted Orb", DateTimeOffset.UtcNow);
        Assert.AreEqual(3, e.ConsideredListings);
    }

    [TestMethod]
    public void NoListingsMeansNoEstimateNotZero()
    {
        var e = ListingPriceEstimator.Estimate([], 0, "Exalted Orb", DateTimeOffset.UtcNow);
        Assert.IsFalse(e.HasValue);
        Assert.AreEqual(EstimateConfidence.None, e.Confidence);
        Assert.IsNull(e.Median);
    }

    [TestMethod]
    public void UnconvertibleListingsAreSkipped()
    {
        var listings = new List<MarketListing>
        {
            new("x", new CurrencyAmount(3, "Mystery Coin"), null, "a", true, null, null),
            L(10, "b"),
        };
        var e = ListingPriceEstimator.Estimate(listings, 2, "Exalted Orb", DateTimeOffset.UtcNow);
        Assert.AreEqual(1, e.ConsideredListings);
        Assert.AreEqual(EstimateConfidence.Low, e.Confidence);
    }

    [TestMethod]
    public void ManyTightListingsGiveHighConfidence()
    {
        var listings = Enumerable.Range(0, 20).Select(i => L(10 + (i * 0.1m))).ToList();
        var e = ListingPriceEstimator.Estimate(listings, 40, "Exalted Orb", DateTimeOffset.UtcNow);
        Assert.AreEqual(EstimateConfidence.High, e.Confidence);
    }

    [TestMethod]
    public void CurrencyConversionUsesDecimalMath()
    {
        var rates = new CurrencyRates("Exalted Orb", [new("Divine Orb", 300m), new("Regal Orb", 0.3m)], DateTimeOffset.UtcNow, "t");
        Assert.IsTrue(rates.TryConvert(2m, "Divine Orb", "Exalted Orb", out var ex));
        Assert.AreEqual(600m, ex);
        Assert.IsTrue(rates.TryConvert(1m, "Regal Orb", "Divine Orb", out var div));
        Assert.AreEqual(0.001m, div);
        Assert.IsFalse(rates.TryConvert(1m, "Unknown", "Exalted Orb", out _));
        Assert.IsTrue(rates.TryConvert(1m, "exalted orb", "Exalted Orb", out _), "names are case-insensitive");
    }
}

[TestClass]
public sealed class TradeQueryTests
{
    private static StatIndex Stats() => new(
    [
        new TradeStat("explicit.stat_life", "+# to maximum Life", "explicit"),
        new TradeStat("explicit.stat_fire", "+#% to Fire Resistance", "explicit"),
        new TradeStat("explicit.stat_cold", "+#% to Cold Resistance", "explicit"),
        new TradeStat("explicit.stat_lightning", "+#% to Lightning Resistance", "explicit"),
        new TradeStat("implicit.stat_rarity", "#% increased Rarity of Items found", "implicit"),
        new TradeStat("explicit.stat_armour_local", "#% increased Armour (Local)", "explicit"),
        new TradeStat("explicit.stat_armour_global", "#% increased Armour", "explicit"),
        new TradeStat("explicit.stat_ms", "#% increased Movement Speed", "explicit"),
    ]);

    [TestMethod]
    public void StatIndexResolvesAcrossPlusSignsAndKinds()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        var index = Stats();
        Assert.IsTrue(index.TryResolve(item.Modifiers.Single(m => m.Kind == ModifierKind.Implicit), false, out var imp));
        Assert.AreEqual("implicit.stat_rarity", imp.Id);
        Assert.IsTrue(index.TryResolve(item.Modifiers.Single(m => m.Pattern == "# to maximum Life"), false, out var life));
        Assert.AreEqual("explicit.stat_life", life.Id);
    }

    [TestMethod]
    public void ReducedIsResolvedAsNegatedIncreased()
    {
        Assert.IsTrue(Stats().TryResolve("explicit", ModifierText.Normalize("10% reduced Movement Speed"), false, out var s));
        Assert.AreEqual("explicit.stat_ms", s.Id);
        Assert.IsTrue(s.Negated);
    }

    [TestMethod]
    public void LocalStatsArePreferredForArmourPieces()
    {
        var index = Stats();
        Assert.IsTrue(index.TryResolve("explicit", "#% increased Armour", preferLocal: true, out var local));
        Assert.AreEqual("explicit.stat_armour_local", local.Id);
        Assert.IsTrue(index.TryResolve("explicit", "#% increased Armour", preferLocal: false, out var global));
        Assert.AreEqual("explicit.stat_armour_global", global.Id);
    }

    [TestMethod]
    public void Poe2RareQueryHasTypeRarityAndEnabledStats()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe2.FilterLayout, 90);
        var filters = builder.CreateDefaultFilters(item, Stats(), options);

        var life = filters.Single(f => f.StatId == "explicit.stat_life");
        Assert.IsTrue(life.Enabled);
        Assert.AreEqual(76d, life.Min, "90% of 85 floored");
        filters.Single(f => f.StatId == "explicit.stat_cold").Enabled = false;

        var json = builder.Build(item, filters, options);
        var query = json["query"]!.AsObject();
        Assert.AreEqual("Gold Ring", query["type"]!.GetValue<string>());
        Assert.IsNull(query["name"], "rare names are random and must not be searched");
        Assert.AreEqual("nonunique", query["filters"]!["type_filters"]!["filters"]!["rarity"]!["option"]!.GetValue<string>());
        var statIds = query["stats"]![0]!["filters"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToList();
        CollectionAssert.Contains(statIds, "explicit.stat_life");
        CollectionAssert.DoesNotContain(statIds, "explicit.stat_cold");
        Assert.AreEqual("asc", json["sort"]!["price"]!.GetValue<string>());
    }

    [TestMethod]
    public void UniqueQueryUsesNameAndTypeWithModsDisabled()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var filters = builder.CreateDefaultFilters(item, Stats(), options);
        Assert.IsTrue(filters.Where(f => f.Kind == FilterKind.Stat).All(f => !f.Enabled));

        var query = builder.Build(item, filters, options)["query"]!;
        Assert.AreEqual("Astramentis", query["name"]!.GetValue<string>());
        Assert.AreEqual("Onyx Amulet", query["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void FoulbornUniqueSearchesBaseNameWithFoulbornFilter()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-armour.txt", GameId.Poe1);
        Assert.IsTrue(item.IsFoulborn);
        Assert.AreEqual("Foulborn Zahndethus' Cassock", item.DisplayName, "display keeps the in-game name");

        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var query = builder.Build(item, builder.CreateDefaultFilters(item, Stats(), options), options)["query"]!;
        Assert.AreEqual("Zahndethus' Cassock", query["name"]!.GetValue<string>());
        Assert.AreEqual("Sage's Robe", query["type"]!.GetValue<string>());
        Assert.AreEqual("true", query["filters"]!["misc_filters"]!["filters"]!["foulborn_item"]!["option"]!.GetValue<string>());
    }

    [TestMethod]
    public void RegularUniqueExcludesFoulbornCopies()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1);
        Assert.IsFalse(item.IsFoulborn);

        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var query = builder.Build(item, builder.CreateDefaultFilters(item, Stats(), options), options)["query"]!;
        Assert.AreEqual("false", query["filters"]!["misc_filters"]!["filters"]!["foulborn_item"]!["option"]!.GetValue<string>());
    }

    private const string GreenDreamManaStat = "explicit.stat_1535088208";

    private static StatIndex JewelStats() => new(
    [
        new TradeStat("explicit.stat_cold_as_chaos", "Gain #% of Cold Damage as Extra Chaos Damage", "explicit"),
        new TradeStat(GreenDreamManaStat,
            "Passives granting Cold Resistance or all Elemental Resistances in Radius\nalso grant increased Maximum Mana at #% of its value", "explicit"),
        new TradeStat("explicit.stat_other_radius",
            "Passives granting Fire Resistance or all Elemental Resistances in Radius\nalso grant increased Maximum Life at #% of its value", "explicit"),
    ]);

    [TestMethod]
    public void MultilineModifierBecomesOneFilter()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-jewel-multiline.txt", GameId.Poe1);
        Assert.AreEqual(3, item.Modifiers.Count, "the clipboard still carries two lines for the radius modifier");

        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var stats = builder.CreateDefaultFilters(item, JewelStats(), options).Where(f => f.Kind == FilterKind.Stat).ToList();

        Assert.AreEqual(2, stats.Count);
        var radius = stats.Single(f => f.StatId == GreenDreamManaStat);
        StringAssert.StartsWith(radius.Label, "Passives granting Cold Resistance");
        StringAssert.EndsWith(radius.Label, "at 75% of its value");
        Assert.AreEqual(75d, radius.RolledValue, "the value comes from the second line");
        Assert.AreEqual(75d, radius.Min, "uniques search from the rolled value");

        radius.Enabled = true;
        var filters = builder.Build(item, stats, options)["query"]!["stats"]![0]!["filters"]!.AsArray();
        Assert.AreEqual(1, filters.Count);
        Assert.AreEqual(GreenDreamManaStat, filters[0]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public void LoneLineOfMultilineStatStillResolves()
    {
        var index = JewelStats();
        Assert.IsTrue(index.TryResolve("explicit", ModifierText.Normalize("also grant increased Maximum Mana at 75% of its value"), false, out var s));
        Assert.AreEqual(GreenDreamManaStat, s.Id);
    }

    [TestMethod]
    public void UniqueStatMinimumsUseTheRolledValue()
    {
        var item = Fixture.Parse("poe1/unique-helmet-keystone.txt", GameId.Poe1);
        var index = new StatIndex(
        [
            new TradeStat("explicit.stat_es_global", "+# to maximum Energy Shield", "explicit"),
            new TradeStat("explicit.stat_4052037485", "+# to maximum Energy Shield (Local)", "explicit"),
            new TradeStat("explicit.stat_3767873853", "Reflects # Physical Damage to Melee Attackers", "explicit"),
            new TradeStat("explicit.stat_3441651621", "+# Physical Damage taken from Attack Hits", "explicit"),
            new TradeStat("explicit.stat_98977150", "Pain Attunement", "explicit"),
        ]);

        var options = new TradeQueryOptions(Games.Poe1.FilterLayout, 90);
        var stats = new TradeQueryBuilder().CreateDefaultFilters(item, index, options).Where(f => f.Kind == FilterKind.Stat).ToList();

        Assert.AreEqual(4, stats.Count, "keystone lines are searchable too");
        var es = stats.Single(f => f.StatId == "explicit.stat_4052037485");
        Assert.AreEqual(225d, es.Min, "a perfect roll must not search from 90% of itself");
        Assert.AreEqual(5d, stats.Single(f => f.StatId == "explicit.stat_3767873853").Min);
        Assert.AreEqual(25d, stats.Single(f => f.StatId == "explicit.stat_3441651621").Min);
        Assert.IsNull(stats.Single(f => f.StatId == "explicit.stat_98977150").Min);
    }

    [TestMethod]
    public void Poe1SocketsAndLinksUseSocketFilters()
    {
        var item = Fixture.Parse("poe1/six-link-armour.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var filters = builder.CreateDefaultFilters(item, null, options);
        var query = builder.Build(item, filters, options)["query"]!;
        var sockets = query["filters"]!["socket_filters"]!["filters"]!;
        Assert.AreEqual(6d, sockets["links"]!["min"]!.GetValue<double>());
        Assert.AreEqual(6d, sockets["sockets"]!["min"]!.GetValue<double>());
    }

    [TestMethod]
    public void QueryDefaultsToInstantBuyoutWithoutPriceFilter()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout);
        var query = builder.Build(item, builder.CreateDefaultFilters(item, null, options), options)["query"]!;
        Assert.AreEqual("securable", query["status"]!["option"]!.GetValue<string>());
        Assert.IsNull(query["filters"]!["trade_filters"]);
    }

    [TestMethod]
    public void QueryAppliesListingStatusAndPriceCurrency()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe1.FilterLayout, Status: TradeListingStatus.InPersonOnline,
            PriceOption: TradePriceCurrencies.Find("divine").ApiId(GameId.Poe1));
        var query = builder.Build(item, builder.CreateDefaultFilters(item, null, options), options)["query"]!;
        Assert.AreEqual("online", query["status"]!["option"]!.GetValue<string>());
        Assert.AreEqual("divine", query["filters"]!["trade_filters"]!["filters"]!["price"]!["option"]!.GetValue<string>());
    }

    [TestMethod]
    public void PriceCurrencyCatalogFallsBackToAny()
    {
        Assert.AreSame(TradePriceCurrencies.Any, TradePriceCurrencies.Find(null));
        Assert.AreSame(TradePriceCurrencies.Any, TradePriceCurrencies.Find("unknown"));
        Assert.IsNull(TradePriceCurrencies.Find("chromatic").ApiId(GameId.Poe1));
        Assert.AreEqual("chrome", TradePriceCurrencies.Find("chrome").ApiId(GameId.Poe1));
        Assert.IsNull(TradePriceCurrencies.Find("chrome").ApiId(GameId.Poe2), "PoE 2 has no Chromatic Orb option");
        Assert.AreEqual(19, TradePriceCurrencies.All.Count);
    }

    [TestMethod]
    public void WaystoneQueryFiltersTier()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);
        var builder = new TradeQueryBuilder();
        var options = new TradeQueryOptions(Games.Poe2.FilterLayout);
        var query = builder.Build(item, builder.CreateDefaultFilters(item, null, options), options)["query"]!;
        var tier = query["filters"]!["map_filters"]!["filters"]!["map_tier"]!;
        Assert.AreEqual(15d, tier["min"]!.GetValue<double>());
        Assert.AreEqual(15d, tier["max"]!.GetValue<double>());
    }

    [TestMethod]
    public void TradeFetchResponseIsParsed()
    {
        const string json = """
            {"result":[
              {"id":"a1","listing":{"indexed":"2026-11-20T10:00:00Z","account":{"name":"Seller#1234","online":{"league":"Standard"}},"price":{"type":"~price","amount":12,"currency":"exalted"}},"item":{"name":"Doom Circle","typeLine":"Gold Ring","ilvl":82}},
              {"id":"a2","listing":{"account":{"name":"Offline#1","online":null},"price":{"amount":1.5,"currency":"divine"}},"item":{"typeLine":"Gold Ring"}},
              {"id":"a3","listing":{"account":{"name":"NoPrice"}}},
              null
            ]}
            """;
        using var doc = JsonDocument.Parse(json);
        var listings = OfficialTradeClient.ParseFetch(doc.RootElement);

        Assert.AreEqual(2, listings.Count);
        Assert.AreEqual(12m, listings[0].Amount);
        Assert.AreEqual("exalted", listings[0].Currency);
        Assert.IsTrue(listings[0].Online);
        Assert.IsFalse(listings[1].Online);
        Assert.AreEqual("Doom Circle, Gold Ring · ilvl 82", listings[0].ItemSummary);
    }

    [TestMethod]
    public void TradeSearchUrlIncludesRealmForPoe2()
    {
        var client = new OfficialTradeClient(new HttpClient(), new Infrastructure.Http.RateLimiter(TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OfficialTradeClient>.Instance);
        Assert.AreEqual("https://www.pathofexile.com/trade2/search/poe2/Rise%20of%20the%20Abyssal/abc",
            client.BuildSearchUrl(Games.Poe2, "Rise of the Abyssal", "abc"));
        Assert.AreEqual("https://www.pathofexile.com/trade/search/Standard/xyz",
            client.BuildSearchUrl(Games.Poe1, "Standard", "xyz"));
        StringAssert.Contains(client.BuildSearchUrl(Games.Poe2, "Standard", new JsonObject { ["query"] = new JsonObject() }), "?q=");
    }

    [TestMethod]
    public void StatsResponseKeepsMultilineEntriesWhole()
    {
        const string json = """{"result":[{"label":"Explicit","entries":[{"id":"explicit.stat_1","text":"+# to maximum Life","type":"explicit"},{"id":"explicit.stat_2","text":"Line one #\nLine two #","type":"explicit"}]}]}""";
        using var doc = JsonDocument.Parse(json);
        var stats = TradeDataProvider.ParseStats(doc.RootElement).ToList();
        Assert.AreEqual(2, stats.Count);
        Assert.AreEqual("explicit.stat_2", stats[1].Id);
        Assert.AreEqual("Line one #\nLine two #", stats[1].Text);
    }

    [TestMethod]
    public void StaticDataYieldsCurrencyIconUrls()
    {
        const string json = """{"result":[{"id":"Currency","label":"Currency","entries":[{"id":"chaos","text":"Chaos Orb","image":"/gen/image/abc/Chaos.png"},{"id":"divine","text":"Divine Orb","image":"https://web.poecdn.com/gen/image/def/Divine.png"},{"id":"noimage","text":"No image"}]},{"id":"Fragments","entries":[{"id":"frag","image":"/x.png"}]}]}""";
        using var doc = JsonDocument.Parse(json);
        var icons = TradeDataProvider.ParseCurrencyIcons(doc.RootElement);
        Assert.AreEqual(2, icons.Count);
        Assert.AreEqual("https://web.poecdn.com/gen/image/abc/Chaos.png", icons["chaos"]);
        Assert.AreEqual("https://web.poecdn.com/gen/image/def/Divine.png", icons["DIVINE"]);
    }

    [TestMethod]
    public void EveryPriceCurrencyExceptAnyHasAnIcon()
    {
        foreach (var currency in TradePriceCurrencies.All)
        {
            Assert.AreEqual(currency.Key != TradePriceCurrencies.AnyKey, currency.IconIds.Length > 0, currency.Key);
        }

        CollectionAssert.AreEqual(new[] { "chaos", "divine" }, TradePriceCurrencies.Find("chaos_divine").IconIds);
    }
}
