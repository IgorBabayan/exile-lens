using System.Collections.Concurrent;
using System.Text.Json;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;
using ExileLens.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace ExileLens.Infrastructure.Trade;

/// <summary>
/// League list and trade stat definitions. Stats (~1 MB) are cached on disk per game and refreshed at most
/// once a day; the cached copy keeps price checks working offline or when the endpoint is rate limited.
/// </summary>
public sealed class TradeDataProvider(HttpClient http, RateLimiter limiter, IAppPaths paths, TimeProvider time, ILogger<TradeDataProvider> logger)
    : ITradeDataProvider
{
    private static readonly TimeSpan StatsMaxAge = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<GameId, Lazy<Task<StatIndex?>>> _stats = new();

    public async Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken)
    {
        var endpoint = $"{game.TradeApiSegment}:data";
        await limiter.AcquireAsync(endpoint, cancellationToken).ConfigureAwait(false);
        using var response = await http.GetAsync($"{OfficialTradeClient.BaseUrl}/api/{game.TradeApiSegment}/data/leagues", cancellationToken).ConfigureAwait(false);
        limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            throw new MarketUnavailableException($"League list unavailable ({(int)response.StatusCode}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLeagues(doc.RootElement, game.TradeRealmSegment ?? "pc");
    }

    public Task<StatIndex?> GetStatIndexAsync(IGameModule game, CancellationToken cancellationToken)
    {
        var lazy = _stats.GetOrAdd(game.Game, _ => new Lazy<Task<StatIndex?>>(() => LoadStatsAsync(game)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private async Task<StatIndex?> LoadStatsAsync(IGameModule game)
    {
        var cachePath = Path.Combine(paths.CacheDirectory, $"trade-stats-{game.NinjaSegment}.json");
        var fresh = File.Exists(cachePath) && time.GetUtcNow() - File.GetLastWriteTimeUtc(cachePath) < StatsMaxAge;
        if (!fresh)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var endpoint = $"{game.TradeApiSegment}:data";
                await limiter.AcquireAsync(endpoint, cts.Token).ConfigureAwait(false);
                using var response = await http.GetAsync($"{OfficialTradeClient.BaseUrl}/api/{game.TradeApiSegment}/data/stats", cts.Token).ConfigureAwait(false);
                limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    var temp = cachePath + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes, cts.Token).ConfigureAwait(false);
                    File.Move(temp, cachePath, overwrite: true);
                }
                else
                {
                    logger.LogWarning("Trade stats returned {Status}; using cached copy if present", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TradeRateLimitedException or IOException)
            {
                logger.LogWarning(ex, "Trade stats refresh failed; using cached copy if present");
            }
        }

        if (!File.Exists(cachePath))
        {
            _stats.TryRemove(game.Game, out _); // allow a later retry
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            return new StatIndex(ParseStats(doc.RootElement));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Cached trade stats are corrupt; deleting");
            File.Delete(cachePath);
            _stats.TryRemove(game.Game, out _);
            return null;
        }
    }

    internal static List<LeagueInfo> ParseLeagues(JsonElement root, string realm)
    {
        var list = new List<LeagueInfo>();
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var l in result.EnumerateArray())
        {
            var r = l.TryGetProperty("realm", out var re) ? re.GetString() : null;
            if (r is not null && !string.Equals(r, realm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = l.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var text = l.TryGetProperty("text", out var t) ? t.GetString() ?? id : id;
            list.Add(new LeagueInfo(id, text));
        }

        return list;
    }

    internal static IEnumerable<TradeStat> ParseStats(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in entries.EnumerateArray())
            {
                var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var text = e.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                var type = e.TryGetProperty("type", out var tyEl) ? tyEl.GetString() : null;
                if (id is null || text is null)
                {
                    continue;
                }

                type ??= id.Split('.')[0];
                foreach (var line in text.Split('\n'))
                {
                    yield return new TradeStat(id, line, type);
                }
            }
        }
    }
}
