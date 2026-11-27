using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Rewards;

namespace ExileLens.Tests;

[TestClass]
public sealed class RewardTests
{
    private static CurrencyRates Poe2Rates(bool includeShardPrice = false)
    {
        var values = new Dictionary<string, decimal>
        {
            ["Regal Orb"] = 1m, // 1 regal = 1 exalted (illustrative)
            ["Orb of Transmutation"] = 0.05m,
            ["Orb of Chance"] = 0.5m,
            ["Artificer's Orb"] = 2m,
            ["Armourer's Scrap"] = 0.02m,
            ["Divine Orb"] = 200m,
        };
        if (includeShardPrice)
        {
            values["Regal Shard"] = 0.15m;
        }

        return new CurrencyRates("Exalted Orb", values, DateTimeOffset.UtcNow, "test");
    }

    private static DisenchantmentEstimator Disenchant => new(Games.Poe2.RewardRules);

    [TestMethod]
    public void RareWithFourAffixesGivesOneRegalShardValuedFromFullOrb()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, Poe2Rates(), "Exalted Orb")!;

        Assert.AreEqual("rare", r.RuleId);
        Assert.AreEqual("Regal Shard", r.Lines.Single().Currency);
        Assert.AreEqual(1m, r.Lines.Single().Quantity);
        Assert.AreEqual(0.1m, r.TotalValue, "1 shard = 1/10 Regal Orb");
        Assert.IsTrue(r.IsExact);
    }

    [TestMethod]
    public void DirectShardPriceWinsOverFractionalFallback()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, Poe2Rates(includeShardPrice: true), "Exalted Orb")!;
        Assert.AreEqual(0.15m, r.TotalValue);
    }

    [TestMethod]
    public void RareWithSixAffixesGivesTwoRegalShards()
    {
        var item = Fixture.Parse("poe2/rare-body-hybrid-advanced.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, Poe2Rates(), "Exalted Orb")!;
        Assert.AreEqual("rare-6-mods", r.RuleId);
        Assert.AreEqual(2m, r.Lines.Single().Quantity);
        Assert.AreEqual(0.2m, r.TotalValue);
    }

    [TestMethod]
    public void EstimatedAffixCountIsFlagged()
    {
        var item = Fixture.Parse("poe2/rare-ring-simple.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, Poe2Rates(), "Exalted Orb")!;
        Assert.IsFalse(r.IsExact);
        Assert.IsNotNull(r.Note);
    }

    [TestMethod]
    public void MagicGivesTransmutationShard()
    {
        var item = Fixture.Parse("poe2/magic-wand.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, Poe2Rates(), "Exalted Orb")!;
        Assert.AreEqual("Transmutation Shard", r.Lines.Single().Currency);
        Assert.AreEqual(0.005m, r.TotalValue);
    }

    [TestMethod]
    public void WithoutRatesQuantityIsShownWithoutValue()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        var r = Disenchant.Estimate(item, null, "Exalted Orb")!;
        Assert.AreEqual(1m, r.Lines.Single().Quantity);
        Assert.IsNull(r.TotalValue);
        Assert.IsNull(r.Lines.Single().Value);
    }

    [TestMethod]
    public void SalvageCountsSocketsAndQualitySeparately()
    {
        var item = Fixture.Parse("poe2/rare-body-hybrid-advanced.txt", GameId.Poe2); // 2 sockets, 12% quality
        var r = new SalvageEstimator(Games.Poe2.RewardRules).Estimate(item, Poe2Rates(), "Exalted Orb")!;

        Assert.AreEqual(RewardKind.Salvage, r.Kind);
        var shards = r.Lines.Single(l => l.Currency == "Artificer's Shard");
        Assert.AreEqual(2m, shards.Quantity);
        var scraps = r.Lines.Single(l => l.Currency == "Armourer's Scrap");
        Assert.AreEqual(2m, scraps.MinQuantity);
        Assert.AreEqual(3m, scraps.MaxQuantity);
        Assert.AreEqual(2.4m, scraps.Quantity);
        Assert.IsFalse(r.IsExact);
        Assert.AreEqual((2m * 0.2m) + (2.4m * 0.02m), r.TotalValue);
    }

    [TestMethod]
    public void JewelleryCannotBeSalvaged()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
        Assert.IsNull(new SalvageEstimator(Games.Poe2.RewardRules).Estimate(item, Poe2Rates(), "Exalted Orb"));
    }

    [TestMethod]
    public void Poe1SixLinkOutranksSixSockets()
    {
        var item = Fixture.Parse("poe1/six-link-armour.txt", GameId.Poe1);
        var r = new VendorValueEstimator(Games.Poe1.RewardRules).Estimate(item, null, "Chaos Orb")!;
        Assert.AreEqual("six-link", r.RuleId);
        Assert.AreEqual("Orb of Fusing", r.Lines.Single().Currency);
        Assert.AreEqual(20m, r.Lines.Single().Quantity);
    }

    [TestMethod]
    public void Poe1SixUnlinkedSocketsGiveJewellers()
    {
        var text = Fixture.Load("poe1/six-link-armour.txt").Replace("R-G-B-B-G-R", "R-G B-B G-R", StringComparison.Ordinal);
        Assert.IsTrue(new ItemTextParser().TryParse(text, GameId.Poe1, out var item, out _));
        var r = new VendorValueEstimator(Games.Poe1.RewardRules).Estimate(item!, null, "Chaos Orb")!;
        Assert.AreEqual("six-socket", r.RuleId);
        Assert.AreEqual(7m, r.Lines.Single().Quantity);
    }

    [TestMethod]
    public void Poe1LinkedRgbGivesChromatic()
    {
        var item = Fixture.Parse("poe1/rare-gloves.txt", GameId.Poe1);
        var r = new VendorValueEstimator(Games.Poe1.RewardRules).Estimate(item, null, "Chaos Orb")!;
        Assert.AreEqual("linked-rgb", r.RuleId);
    }

    [TestMethod]
    public void Poe1HasNoDisenchantRulesAndPoe2NoVendorRules()
    {
        var poe1Item = Fixture.Parse("poe1/rare-gloves.txt", GameId.Poe1);
        Assert.IsNull(new DisenchantmentEstimator(Games.Poe1.RewardRules).Estimate(poe1Item, null, "Chaos Orb"));
        var poe2Item = Fixture.Parse("poe2/rare-body-hybrid-advanced.txt", GameId.Poe2);
        Assert.IsNull(new VendorValueEstimator(Games.Poe2.RewardRules).Estimate(poe2Item, null, "Exalted Orb"));
    }

    [TestMethod]
    public void RecommendsTradeWhenMarketBeatsRewards()
    {
        var market = new PriceEstimate("Exalted Orb", 10m, 14m, 12m, 16m, 24, 20, EstimateConfidence.Medium, DateTimeOffset.UtcNow, true, "t");
        var reward = new RewardEstimate(RewardKind.Disenchant, "rare", [], "Exalted Orb", 0.2m, true, null);
        var c = ValueRecommender.Recommend(market, 1m, reward);
        Assert.AreEqual(SuggestedAction.SellOnTrade, c.Action);
        Assert.AreEqual(12m, c.MarketValue, "uses the conservative low end");
    }

    [TestMethod]
    public void RecommendsDisenchantBelowMinimumTradeValue()
    {
        var market = new PriceEstimate("Exalted Orb", 0.3m, 0.5m, 0.4m, 0.6m, 30, 20, EstimateConfidence.High, DateTimeOffset.UtcNow, true, "t");
        var reward = new RewardEstimate(RewardKind.Disenchant, "rare", [], "Exalted Orb", 0.1m, true, null);
        Assert.AreEqual(SuggestedAction.Disenchant, ValueRecommender.Recommend(market, 1m, reward).Action);
    }

    [TestMethod]
    public void PicksBestOfSalvageAndDisenchant()
    {
        var market = new PriceEstimate("Exalted Orb", 0.3m, 0.5m, 0.4m, 0.6m, 30, 20, EstimateConfidence.High, DateTimeOffset.UtcNow, true, "t");
        var dis = new RewardEstimate(RewardKind.Disenchant, "rare", [], "Exalted Orb", 0.1m, true, null);
        var sal = new RewardEstimate(RewardKind.Salvage, "salvage", [], "Exalted Orb", 0.45m, true, null);
        Assert.AreEqual(SuggestedAction.Salvage, ValueRecommender.Recommend(market, 1m, dis, sal).Action);
    }

    [TestMethod]
    public void InsufficientDataWhenNoMarketOrLowConfidence()
    {
        var reward = new RewardEstimate(RewardKind.Disenchant, "rare", [], "Exalted Orb", 0.1m, true, null);
        Assert.AreEqual(SuggestedAction.InsufficientMarketData, ValueRecommender.Recommend(null, 1m, reward).Action);
        var low = new PriceEstimate("Exalted Orb", 5m, 5m, 5m, 5m, 1, 1, EstimateConfidence.Low, DateTimeOffset.UtcNow, true, "t");
        Assert.AreEqual(SuggestedAction.InsufficientMarketData, ValueRecommender.Recommend(low, 1m, reward).Action);
    }
}
