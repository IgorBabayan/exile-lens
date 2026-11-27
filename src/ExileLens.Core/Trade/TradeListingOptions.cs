using ExileLens.Core.Items;

namespace ExileLens.Core.Trade;

/// <summary>Seller / sale-type filter of the official trade site ("status" in the query).</summary>
public enum TradeListingStatus
{
    InstantBuyoutAndInPerson,
    InstantBuyout,
    InPersonOnlineInLeague,
    InPersonOnline,
    Any,
}

public static class TradeListingStatuses
{
    public static string ApiId(TradeListingStatus status) => status switch
    {
        TradeListingStatus.InstantBuyoutAndInPerson => "available",
        TradeListingStatus.InstantBuyout => "securable",
        TradeListingStatus.InPersonOnlineInLeague => "onlineleague",
        TradeListingStatus.InPersonOnline => "online",
        _ => "any",
    };

    /// <summary>
    /// Narrows a listing type to sellers that are online right now. "Online in league" is already online-only;
    /// everything else (including Instant Buyout, whose merchants are usually offline) becomes "In Person (Online)".
    /// </summary>
    public static TradeListingStatus OnlineOnly(TradeListingStatus status) =>
        status == TradeListingStatus.InPersonOnlineInLeague ? status : TradeListingStatus.InPersonOnline;

    public static string Label(TradeListingStatus status) => status switch
    {
        TradeListingStatus.InstantBuyoutAndInPerson => "Instant Buyout and In Person",
        TradeListingStatus.InstantBuyout => "Instant Buyout",
        TradeListingStatus.InPersonOnlineInLeague => "In Person (Online in League)",
        TradeListingStatus.InPersonOnline => "In Person (Online)",
        _ => "Any",
    };
}

/// <summary>
/// A buyout-currency option of the trade site's price filter. <see cref="Poe1Id"/> / <see cref="Poe2Id"/> are the
/// trade API option ids; null means the game has no such option and the search is not restricted.
/// <see cref="IconIds"/> are trade static-data ids ("Currency" group) whose art is shown next to the label.
/// </summary>
public sealed record TradePriceCurrency(string Key, string Label, string? Poe1Id, string? Poe2Id, params string[] IconIds)
{
    public string? ApiId(GameId game) => game == GameId.Poe1 ? Poe1Id : Poe2Id;
}

public static class TradePriceCurrencies
{
    public const string AnyKey = "any";

    public static TradePriceCurrency Any { get; } = new(AnyKey, "Any", null, null);

    /// <summary>Order matches the trade site's currency drop-down.</summary>
    public static IReadOnlyList<TradePriceCurrency> All { get; } =
    [
        Any,
        // The trade site's default ordering already compares prices in chaos: no option is sent.
        new("chaos_equivalent", "Chaos Orb Equivalent", null, null, "chaos"),
        new("chaos_divine", "Chaos or Divine Orbs", "chaos_divine", null, "chaos", "divine"),
        new("chaos", "Chaos Orb", "chaos", "chaos", "chaos"),
        new("exalted", "Exalted Orb", "exalted", "exalted", "exalted"),
        new("divine", "Divine Orb", "divine", "divine", "divine"),
        new("mirror", "Mirror of Kalandra", "mirror", null, "mirror"),
        new("blessed", "Blessed Orb", "blessed", null, "blessed"),
        new("chrome", "Chromatic Orb", "chrome", null, "chrome"),
        new("gcp", "Gemcutter's Prism", "gcp", null, "gcp"),
        new("jewellers", "Jeweller's Orb", "jewellers", null, "jewellers"),
        new("scour", "Orb of Scouring", "scour", null, "scour"),
        new("regret", "Orb of Regret", "regret", null, "regret"),
        new("fusing", "Orb of Fusing", "fusing", null, "fusing"),
        new("chance", "Orb of Chance", "chance", null, "chance"),
        new("alt", "Orb of Alteration", "alt", null, "alt"),
        new("alch", "Orb of Alchemy", "alch", null, "alch"),
        new("regal", "Regal Orb", "regal", null, "regal"),
        new("vaal", "Vaal Orb", "vaal", null, "vaal"),
    ];

    private static readonly Dictionary<string, TradePriceCurrency> ByKey = All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    public static TradePriceCurrency Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var c) ? c : Any;
}
