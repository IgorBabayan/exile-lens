using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Settings;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Pricing;

public sealed class PriceCheckSession
{
    public required ParsedItem Item { get; init; }
    public required IGameModule Game { get; init; }
    public required string League { get; init; }
    public required IReadOnlyList<SearchFilter> Filters { get; init; }
    public required string ValuationCurrency { get; init; }
    public bool UsesBulkPricing { get; init; }
}

public sealed record RewardSummary(RewardEstimate? Disenchant, RewardEstimate? Salvage, RewardEstimate? Vendor, ValueComparison? Comparison);

public sealed record PriceCheckResult(
    PriceEstimate Estimate,
    IReadOnlyList<MarketListing> Listings,
    string? TradeUrl,
    RewardSummary Rewards,
    string? Error,
    bool FromCache,
    TimeSpan NetworkTime,
    string? IconUrl = null);

public interface IPriceEstimationService
{
    /// <summary>Creates a session with default filters. Uses cached stat data only; never blocks on the network.</summary>
    PriceCheckSession CreateSession(ParsedItem item, IGameModule game, string league);

    /// <summary>Computes local-only rewards (cached rates when available). Sub-millisecond; safe on UI thread.</summary>
    RewardSummary EstimateRewardsLocal(PriceCheckSession session, PriceEstimate? market);

    Task<PriceCheckResult> RunAsync(PriceCheckSession session, CancellationToken cancellationToken);

    /// <summary>Icon URL already known for this item (from earlier searches or cached exchange data). Never blocks.</summary>
    string? PeekIconUrl(PriceCheckSession session);

    string BuildTradeUrl(PriceCheckSession session);
}

public sealed class PriceCheckService : IPriceEstimationService
{
    private readonly ITradeClient _trade;
    private readonly ITradeDataProvider _tradeData;
    private readonly IMarketDataProvider _market;
    private readonly ICurrencyConversionService _rates;
    private readonly ITradeQueryBuilder _queryBuilder;
    private readonly ISettingsService _settings;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<PriceCheckService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<(PriceEstimate, IReadOnlyList<MarketListing>, string?)>>> _inflight = new();
    private readonly ConcurrentDictionary<GameId, StatIndex> _statIndexes = new();
    private readonly ConcurrentDictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxRememberedIcons = 4096;

    public PriceCheckService(
        ITradeClient trade,
        ITradeDataProvider tradeData,
        IMarketDataProvider market,
        ICurrencyConversionService rates,
        ITradeQueryBuilder queryBuilder,
        ISettingsService settings,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<PriceCheckService> logger)
    {
        _trade = trade;
        _tradeData = tradeData;
        _market = market;
        _rates = rates;
        _queryBuilder = queryBuilder;
        _settings = settings;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>Warm-up: loads the stat index so later sessions get modifier filters immediately.</summary>
    public async Task PreloadAsync(IGameModule game, CancellationToken cancellationToken)
    {
        var index = await _tradeData.GetStatIndexAsync(game, cancellationToken).ConfigureAwait(false);
        if (index is not null)
        {
            _statIndexes[game.Game] = index;
        }
    }

    public PriceCheckSession CreateSession(ParsedItem item, IGameModule game, string league)
    {
        var s = _settings.Current;
        var valuation = string.IsNullOrWhiteSpace(s.Rewards.ValuationCurrency) ? game.DefaultValuationCurrency : s.Rewards.ValuationCurrency!;
        var bulk = game.BulkPricedCategories.Contains(item.Category);
        var options = Options(game);
        var filters = bulk ? [] : _queryBuilder.CreateDefaultFilters(item, _statIndexes.GetValueOrDefault(game.Game), options);
        return new PriceCheckSession
        {
            Item = item,
            Game = game,
            League = league,
            Filters = filters,
            ValuationCurrency = valuation,
            UsesBulkPricing = bulk,
        };
    }

    public RewardSummary EstimateRewardsLocal(PriceCheckSession session, PriceEstimate? market)
    {
        var rates = _rates.Peek(session.Game.Game, session.League);
        return ComputeRewards(session, rates, market);
    }

    public string? PeekIconUrl(PriceCheckSession session)
    {
        var item = session.Item;
        if (_icons.TryGetValue(IconKey(session.Game.Game, item), out var url))
        {
            return url;
        }

        return session.UsesBulkPricing ? _market.GetCachedIconUrl(session.Game, session.League, item.BaseType) : null;
    }

    public string BuildTradeUrl(PriceCheckSession session) =>
        _trade.BuildSearchUrl(session.Game, session.League, _queryBuilder.Build(session.Item, session.Filters, Options(session.Game)));

    public async Task<PriceCheckResult> RunAsync(PriceCheckSession session, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        var rates = await _rates.GetRatesAsync(session.Game, session.League, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        try
        {
            PriceEstimate estimate;
            IReadOnlyList<MarketListing> listings;
            string? tradeUrl;
            bool fromCache;

            if (session.UsesBulkPricing)
            {
                estimate = await _market.GetBulkPriceAsync(session.Game, session.League, session.Item, session.ValuationCurrency, cancellationToken).ConfigureAwait(false)
                           ?? PriceEstimate.Unavailable(session.ValuationCurrency, _market.Name, now);
                listings = [];
                tradeUrl = null;
                fromCache = false;
            }
            else
            {
                var options = Options(session.Game);
                var query = _queryBuilder.Build(session.Item, session.Filters, options);
                var key = $"{session.Game.Game}|{session.League}|{session.ValuationCurrency}|{query.ToJsonString()}";
                if (_cache.TryGetValue(key, out (PriceEstimate, IReadOnlyList<MarketListing>, string?) cached))
                {
                    (estimate, listings, tradeUrl) = cached;
                    fromCache = true;
                }
                else
                {
                    var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<(PriceEstimate, IReadOnlyList<MarketListing>, string?)>>(
                        () => SearchAsync(session, query, rates, key)));
                    (estimate, listings, tradeUrl) = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                    fromCache = false;
                }
            }

            var rewards = ComputeRewards(session, rates, estimate);
            return new PriceCheckResult(estimate, listings, tradeUrl, rewards, null, fromCache, _time.GetElapsedTime(started), PeekIconUrl(session));
        }
        catch (TradeRateLimitedException ex)
        {
            return Failed(session, rates, now, ex.Message, started);
        }
        catch (Exception ex) when (ex is HttpRequestException or MarketUnavailableException or System.Text.Json.JsonException or TimeoutException)
        {
            _logger.LogWarning(ex, "Price check failed for {Item}", session.Item.DisplayName);
            return Failed(session, rates, now, "Market data is unavailable right now.", started);
        }
    }

    private PriceCheckResult Failed(PriceCheckSession session, CurrencyRates rates, DateTimeOffset now, string message, long started) =>
        new(PriceEstimate.Unavailable(session.ValuationCurrency, "Official trade", now), [], null,
            ComputeRewards(session, rates, null), message, false, _time.GetElapsedTime(started), PeekIconUrl(session));

    private static string IconKey(GameId game, ParsedItem item) =>
        item.Rarity == ItemRarity.Unique && item.Name is { Length: > 0 } name
            ? $"{game}|u|{name}"
            : $"{game}|b|{item.BaseType}";

    private void RememberIcon(GameId game, ParsedItem item, List<TradeListing> fetched)
    {
        var key = IconKey(game, item);
        if (item.BaseType.Length == 0 || _icons.ContainsKey(key))
        {
            return;
        }

        // Only trust art from a listing of the same base: with the base filter off, other bases can be returned.
        foreach (var l in fetched)
        {
            if (l.Icon is { Length: > 0 } icon && l.TypeLine is { } type
                && type.Contains(item.BaseType, StringComparison.OrdinalIgnoreCase))
            {
                if (_icons.Count >= MaxRememberedIcons)
                {
                    _icons.Clear();
                }

                _icons[key] = icon;
                return;
            }
        }
    }

    private async Task<(PriceEstimate, IReadOnlyList<MarketListing>, string?)> SearchAsync(PriceCheckSession session, JsonObject query, CurrencyRates rates, string key)
    {
        try
        {
            // Not tied to the caller's token: concurrent callers share this request, and a completed result is cached.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = timeout.Token;
            var search = await _trade.SearchAsync(session.Game, session.League, query, ct).ConfigureAwait(false);
            var max = Math.Clamp(_settings.Current.Trade.MaxListings, 1, 100);
            var ids = search.ResultIds.Take(max).ToList();
            var fetched = new List<TradeListing>(ids.Count);
            for (var i = 0; i < ids.Count; i += 10)
            {
                var chunk = ids.GetRange(i, Math.Min(10, ids.Count - i));
                fetched.AddRange(await _trade.FetchAsync(session.Game, search.QueryId, chunk, ct).ConfigureAwait(false));
            }

            RememberIcon(session.Game.Game, session.Item, fetched);
            var listings = new List<MarketListing>(fetched.Count);
            foreach (var l in fetched)
            {
                var name = session.Game.TradeCurrencyNames.GetValueOrDefault(l.Currency, l.Currency);
                decimal? value = rates.TryConvert(l.Amount, name, session.ValuationCurrency, out var v) ? v : null;
                listings.Add(new MarketListing(l.Id, new CurrencyAmount(l.Amount, name), value, l.AccountName, l.Online, l.Indexed, l.ItemSummary));
            }

            var estimate = ListingPriceEstimator.Estimate(listings, search.Total, session.ValuationCurrency, _time.GetUtcNow());
            var url = _trade.BuildSearchUrl(session.Game, session.League, search.QueryId);
            var result = (estimate, (IReadOnlyList<MarketListing>)listings, (string?)url);
            var minutes = Math.Clamp(_settings.Current.Trade.CacheMinutes, 0, 120);
            if (minutes > 0)
            {
                _cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(minutes), Size = 1 });
            }

            return result;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private RewardSummary ComputeRewards(PriceCheckSession session, CurrencyRates? rates, PriceEstimate? market)
    {
        var s = _settings.Current.Rewards;
        var game = session.Game;
        var item = session.Item;
        RewardEstimate? disenchant = null, salvage = null, vendor = null;

        if (item.Category is ItemCategory.Equipment or ItemCategory.Jewel or ItemCategory.Flask or ItemCategory.Charm or ItemCategory.Gem)
        {
            if (game.HasDisenchanting && s.DisenchantEnabled)
            {
                disenchant = new DisenchantmentEstimator(game.RewardRules).Estimate(item, rates, session.ValuationCurrency);
            }

            if (game.HasSalvage && s.SalvageEnabled)
            {
                salvage = new SalvageEstimator(game.RewardRules).Estimate(item, rates, session.ValuationCurrency);
            }

            if (game.HasVendorRecipes && s.DisenchantEnabled)
            {
                vendor = new VendorValueEstimator(game.RewardRules).Estimate(item, rates, session.ValuationCurrency);
            }
        }

        ValueComparison? comparison = null;
        if (disenchant is not null || salvage is not null || vendor is not null)
        {
            var minTrade = _settings.Current.Trade.MinimumTradeValue;
            comparison = ValueRecommender.Recommend(market, minTrade, disenchant, salvage, vendor);
        }

        return new RewardSummary(disenchant, salvage, vendor, comparison);
    }

    private TradeQueryOptions Options(IGameModule game)
    {
        var t = _settings.Current.Trade;
        return new TradeQueryOptions(
            game.FilterLayout,
            t.ModifierMinPercent,
            t.EnableModifiersByDefault,
            t.ListingStatus,
            TradePriceCurrencies.Find(t.PriceCurrency).ApiId(game.Game));
    }
}
