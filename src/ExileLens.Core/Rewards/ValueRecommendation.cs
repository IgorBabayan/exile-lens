using ExileLens.Core.Economy;

namespace ExileLens.Core.Rewards;

public enum SuggestedAction
{
    InsufficientMarketData,
    SellOnTrade,
    Disenchant,
    Salvage,
    SellToVendor,
}

public sealed record ValueComparison(SuggestedAction Action, decimal? MarketValue, decimal? BestRewardValue, string Explanation);

/// <summary>
/// Compares a conservative market value against item-destruction rewards. Never acts on the item; the
/// result is advisory only.
/// </summary>
public static class ValueRecommender
{
    public static ValueComparison Recommend(PriceEstimate? market, decimal minimumTradeValue, params RewardEstimate?[] rewards)
    {
        RewardEstimate? best = null;
        foreach (var r in rewards)
        {
            if (r?.TotalValue is { } v && (best?.TotalValue is null || v > best.TotalValue))
            {
                best = r;
            }
        }

        var bestValue = best?.TotalValue;

        if (market is null || !market.HasValue || market.Confidence == EstimateConfidence.None)
        {
            return new ValueComparison(SuggestedAction.InsufficientMarketData, null, bestValue,
                "Not enough market data to compare.");
        }

        // Conservative: what the item realistically sells for is closer to the low end of the asking range.
        var marketValue = market.RangeLow ?? market.Median!.Value;

        if (market.Confidence == EstimateConfidence.Low && (bestValue is null || bestValue < marketValue))
        {
            return new ValueComparison(SuggestedAction.InsufficientMarketData, marketValue, bestValue,
                "Few comparable listings; the market estimate is unreliable.");
        }

        if (marketValue >= minimumTradeValue && (bestValue is null || marketValue > bestValue))
        {
            return new ValueComparison(SuggestedAction.SellOnTrade, marketValue, bestValue, "Selling on trade is more valuable.");
        }

        if (best is not null && bestValue > 0)
        {
            var action = best.Kind switch
            {
                RewardKind.Disenchant => SuggestedAction.Disenchant,
                RewardKind.Salvage => SuggestedAction.Salvage,
                _ => SuggestedAction.SellToVendor,
            };
            var why = marketValue < minimumTradeValue
                ? "Market value is below your minimum trade value."
                : "Rewards are worth more than the market estimate.";
            return new ValueComparison(action, marketValue, bestValue, why);
        }

        return marketValue >= minimumTradeValue
            ? new ValueComparison(SuggestedAction.SellOnTrade, marketValue, bestValue, "Selling on trade is more valuable.")
            : new ValueComparison(SuggestedAction.InsufficientMarketData, marketValue, bestValue,
                "Below your minimum trade value and no valued rewards are available.");
    }
}
