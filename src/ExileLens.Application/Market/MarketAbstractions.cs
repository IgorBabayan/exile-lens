using System.Text.Json.Nodes;
using ExileLens.Application.Games;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;

namespace ExileLens.Application.Market;

public sealed record LeagueInfo(string Id, string Name);

public sealed record TradeSearchResult(string QueryId, int Total, IReadOnlyList<string> ResultIds);

public sealed record TradeListing(
    string Id,
    decimal Amount,
    string Currency,
    string? AccountName,
    bool? Online,
    DateTimeOffset? Indexed,
    string? ItemSummary);

public sealed class TradeRateLimitedException(TimeSpan retryAfter, string policy)
    : Exception($"Trade API rate limit reached for '{policy}'. Retry in {retryAfter.TotalSeconds:0} s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
    public string Policy { get; } = policy;
}

public sealed class MarketUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface ITradeClient
{
    Task<TradeSearchResult> SearchAsync(IGameModule game, string league, JsonObject query, CancellationToken cancellationToken);
    Task<IReadOnlyList<TradeListing>> FetchAsync(IGameModule game, string queryId, IReadOnlyList<string> ids, CancellationToken cancellationToken);
    string BuildSearchUrl(IGameModule game, string league, string queryId);
    string BuildSearchUrl(IGameModule game, string league, JsonObject query);
}

public interface ITradeDataProvider
{
    Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken);
    Task<StatIndex?> GetStatIndexAsync(IGameModule game, CancellationToken cancellationToken);
}

public interface IMarketDataProvider
{
    string Name { get; }
    Task<CurrencyRates> GetCurrencyRatesAsync(IGameModule game, string league, CancellationToken cancellationToken);

    /// <summary>Aggregated price for stackable/bulk items (currency, fragments, essences...). Null when unknown.</summary>
    Task<PriceEstimate?> GetBulkPriceAsync(IGameModule game, string league, ParsedItem item, string valuationCurrency, CancellationToken cancellationToken);
}

public interface ICurrencyConversionService
{
    /// <summary>Cached league rates; never throws — returns empty rates when no data is available.</summary>
    Task<CurrencyRates> GetRatesAsync(IGameModule game, string league, CancellationToken cancellationToken);

    CurrencyRates? Peek(GameId game, string league);
}
