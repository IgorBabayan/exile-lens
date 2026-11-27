using ExileLens.Core.Items;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;

namespace ExileLens.Tests;

[TestClass]
public sealed class DustTests
{
    private static DustEstimator Dust => new(Games.Poe1.UniqueDust!);

    [TestMethod]
    public void Poe1ShipsUniqueDustTableAndPoe2HasNone()
    {
        Assert.IsNotNull(Games.Poe1.UniqueDust);
        Assert.IsTrue(Games.Poe1.UniqueDust.Count > 1000);
        Assert.IsTrue(Games.Poe1.UniqueDust.TryGet("crown of thorns", out var entry), "lookup is case-insensitive");
        Assert.AreEqual(1.06, entry.Dust, 1e-9);
        Assert.IsNull(Games.Poe2.UniqueDust);
    }

    [TestMethod]
    public void FormulaMatchesPublishedValues()
    {
        // Published ilvl 84 / ilvl 84 + 20% quality values for Original Sin (base 1128.89).
        Assert.AreEqual(2_822_225L, DustCalculator.Compute(1128.89, 84, 0, 0, 0));
        Assert.AreEqual(3_951_115L, DustCalculator.Compute(1128.89, 84, 20, 0, 0));

        // Starforge always carries one influence (+50%).
        Assert.AreEqual(2_638_088L, DustCalculator.Compute(703.49, 84, 0, 1, 0));
    }

    [TestMethod]
    public void ItemLevelIsClampedTo65Through84()
    {
        Assert.AreEqual(DustCalculator.Compute(10, 84, 0, 0, 0), DustCalculator.Compute(10, 100, 0, 0, 0));
        Assert.AreEqual(DustCalculator.Compute(10, 65, 0, 0, 0), DustCalculator.Compute(10, 40, 0, 0, 0));
        Assert.AreEqual(1250L, DustCalculator.Compute(10, 65, 0, 0, 0));
        Assert.AreEqual(25_000L, DustCalculator.Compute(10, 84, 0, 0, 0));
    }

    [TestMethod]
    public void UniqueEstimateUsesItemLevelAndQualityPreview()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1); // Astramentis, ilvl 85
        var dust = Dust.Estimate(item)!;

        Assert.AreEqual(30_500L, dust.Amount, "12.2 × 2500 (ilvl capped at 84)");
        Assert.AreEqual(42_700L, dust.AmountAtMaxQuality, "+40% at 20% quality");
        Assert.AreEqual(84, dust.EffectiveItemLevel);
        Assert.IsTrue(dust.IsExact);
        Assert.IsNull(dust.Note);
    }

    [TestMethod]
    public void FoulbornUniqueIsLookedUpByItsTradeName()
    {
        var item = Fixture.Parse("poe1/foulborn-unique-armour.txt", GameId.Poe1);
        Assert.AreEqual(7_250L, Dust.Estimate(item)!.Amount);
    }

    [TestMethod]
    public void NonUniquesHaveNoDust()
    {
        var item = Fixture.Parse("poe1/rare-gloves.txt", GameId.Poe1);
        Assert.IsNull(Dust.Estimate(item));
    }

    [TestMethod]
    public void OnlineOnlyNarrowsEveryListingType()
    {
        Assert.AreEqual(TradeListingStatus.InPersonOnline, TradeListingStatuses.OnlineOnly(TradeListingStatus.InstantBuyout));
        Assert.AreEqual(TradeListingStatus.InPersonOnline, TradeListingStatuses.OnlineOnly(TradeListingStatus.InstantBuyoutAndInPerson));
        Assert.AreEqual(TradeListingStatus.InPersonOnline, TradeListingStatuses.OnlineOnly(TradeListingStatus.Any));
        Assert.AreEqual(TradeListingStatus.InPersonOnlineInLeague, TradeListingStatuses.OnlineOnly(TradeListingStatus.InPersonOnlineInLeague));
    }
}
