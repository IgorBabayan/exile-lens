namespace ExileLens.Core.Economy;

public enum EstimateConfidence
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>A price in a named currency. Monetary math always uses decimal.</summary>
public readonly record struct CurrencyAmount(decimal Amount, string Currency)
{
    public override string ToString() => $"{Format(Amount)} {Currency}";

    public static string Format(decimal value) => value switch
    {
        >= 100m => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        >= 10m => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
        >= 1m => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>One trade listing, already converted into the valuation currency when a rate was available.</summary>
public sealed record MarketListing(
    string Id,
    CurrencyAmount ListedPrice,
    decimal? ValueInValuationCurrency,
    string? AccountName,
    bool? SellerOnline,
    DateTimeOffset? IndexedAt,
    string? ItemSummary);

public sealed record PriceEstimate(
    string ValuationCurrency,
    decimal? Lowest,
    decimal? Median,
    decimal? RangeLow,
    decimal? RangeHigh,
    int MatchingListings,
    int ConsideredListings,
    EstimateConfidence Confidence,
    DateTimeOffset DataTimestamp,
    bool IsListingPrice,
    string Source)
{
    public static PriceEstimate Unavailable(string currency, string source, DateTimeOffset at, int total = 0) =>
        new(currency, null, null, null, null, total, 0, EstimateConfidence.None, at, true, source);

    public bool HasValue => Median is not null;
}

/// <summary>
/// Turns raw listings into a robust estimate. Listing prices are asking prices, not sales, and the cheapest
/// listings are frequently bait or mistakes, so the estimate: (1) keeps one listing per seller, (2) discards
/// outliers far below the local median, and (3) reports an inter-quantile range rather than a single number.
/// </summary>
public static class ListingPriceEstimator
{
    /// <summary>Listings below this fraction of the median are treated as bait/mispriced.</summary>
    public const decimal OutlierFloorRatio = 0.35m;

    public static PriceEstimate Estimate(
        IReadOnlyList<MarketListing> listings,
        int totalMatching,
        string valuationCurrency,
        DateTimeOffset now,
        string source = "Official trade")
    {
        var values = new List<decimal>(listings.Count);
        var seenSellers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in listings)
        {
            if (l.ValueInValuationCurrency is not { } v || v <= 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(l.AccountName) && !seenSellers.Add(l.AccountName))
            {
                continue;
            }

            values.Add(v);
        }

        if (values.Count == 0)
        {
            return PriceEstimate.Unavailable(valuationCurrency, source, now, totalMatching);
        }

        values.Sort();
        var rawLowest = values[0];
        var median = Percentile(values, 0.5m);
        var floor = median * OutlierFloorRatio;
        var start = 0;
        while (start < values.Count - 1 && values[start] < floor)
        {
            start++;
        }

        var filtered = start == 0 ? values : values.GetRange(start, values.Count - start);
        var low = Percentile(filtered, 0.2m);
        var mid = Percentile(filtered, 0.5m);
        var high = Percentile(filtered, 0.6m);
        if (high < low)
        {
            high = low;
        }

        var confidence = Confidence(filtered, totalMatching, low, mid);
        return new PriceEstimate(valuationCurrency, rawLowest, mid, low, high, totalMatching, filtered.Count, confidence, now, true, source);
    }

    internal static EstimateConfidence Confidence(List<decimal> filtered, int totalMatching, decimal low, decimal mid)
    {
        var n = filtered.Count;
        var spread = low > 0 ? mid / low : decimal.MaxValue;
        if (n >= 10 && totalMatching >= 15 && spread <= 1.6m)
        {
            return EstimateConfidence.High;
        }

        if (n >= 5 && spread <= 3m)
        {
            return EstimateConfidence.Medium;
        }

        return EstimateConfidence.Low;
    }

    /// <summary>Linear-interpolated percentile on a sorted list.</summary>
    public static decimal Percentile(List<decimal> sorted, decimal p)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var pos = (sorted.Count - 1) * p;
        var lo = (int)decimal.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        var frac = pos - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }
}
