using System.Collections.Concurrent;
using ExileLens.Application.Games;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Market;

/// <summary>
/// Caches league currency rates and deduplicates concurrent refreshes. Rates are refreshed lazily on use
/// (no background polling) once older than <see cref="RefreshAfter"/>; stale rates are served while refreshing fails.
/// </summary>
public sealed class CurrencyConversionService(IMarketDataProvider provider, TimeProvider time, ILogger<CurrencyConversionService> logger)
    : ICurrencyConversionService
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<(GameId, string), CurrencyRates> _cache = new();
    private readonly ConcurrentDictionary<(GameId, string), Lazy<Task<CurrencyRates>>> _inflight = new();

    public CurrencyRates? Peek(GameId game, string league) => _cache.GetValueOrDefault((game, league));

    public async Task<CurrencyRates> GetRatesAsync(IGameModule game, string league, CancellationToken cancellationToken)
    {
        var key = (game.Game, league);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < RefreshAfter)
        {
            return cached;
        }

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<CurrencyRates>>(() => RefreshAsync(game, league)));
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CurrencyRates> RefreshAsync(IGameModule game, string league)
    {
        var key = (game.Game, league);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var rates = await provider.GetCurrencyRatesAsync(game, league, cts.Token).ConfigureAwait(false);
            if (!rates.IsEmpty)
            {
                _cache[key] = rates;
            }

            return rates.IsEmpty && _cache.TryGetValue(key, out var old) ? old : rates;
        }
        catch (Exception ex) when (ex is HttpRequestException or MarketUnavailableException or OperationCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Currency rates unavailable for {Game} {League}", game.Game, league);
            return _cache.TryGetValue(key, out var stale) ? stale : CurrencyRates.Empty(game.DefaultValuationCurrency);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }
}
