using System.Collections.Concurrent;
using System.Collections.Frozen;
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
    private static readonly TimeSpan CurrencyIconsMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan UniquesMaxAge = TimeSpan.FromHours(24);
    private static readonly IReadOnlyDictionary<string, string> NoIcons = FrozenDictionary<string, string>.Empty;
    private static readonly IReadOnlyDictionary<string, string[]> NoUniques = FrozenDictionary<string, string[]>.Empty;
    private const string ImageHost = "https://web.poecdn.com";
    private readonly ConcurrentDictionary<GameId, Lazy<Task<StatIndex?>>> _stats = new();
    private readonly ConcurrentDictionary<GameId, Lazy<Task<IReadOnlyDictionary<string, string>>>> _currencyIcons = new();
    private readonly ConcurrentDictionary<GameId, Lazy<Task<IReadOnlyDictionary<string, string[]>>>> _uniques = new();

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

    public Task<IReadOnlyDictionary<string, string>> GetCurrencyIconsAsync(IGameModule game, CancellationToken cancellationToken)
    {
        var lazy = _currencyIcons.GetOrAdd(game.Game, _ => new Lazy<Task<IReadOnlyDictionary<string, string>>>(() => LoadCurrencyIconsAsync(game)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Only the small id → icon map of the "Currency" group is kept on disk (the full static data is several hundred KB),
    /// so later starts read a few KB and the endpoint is hit at most once a week.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadCurrencyIconsAsync(IGameModule game)
    {
        var cachePath = Path.Combine(paths.CacheDirectory, $"trade-currency-icons-{game.NinjaSegment}.json");
        if (File.Exists(cachePath) && time.GetUtcNow() - File.GetLastWriteTimeUtc(cachePath) < CurrencyIconsMaxAge
            && await ReadIconCacheAsync(cachePath).ConfigureAwait(false) is { Count: > 0 } cached)
        {
            return cached;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var endpoint = $"{game.TradeApiSegment}:data";
            await limiter.AcquireAsync(endpoint, cts.Token).ConfigureAwait(false);
            using var response = await http.GetAsync($"{OfficialTradeClient.BaseUrl}/api/{game.TradeApiSegment}/data/static", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                var icons = ParseCurrencyIcons(doc.RootElement);
                if (icons.Count > 0)
                {
                    await WriteIconCacheAsync(cachePath, icons).ConfigureAwait(false);
                    return icons.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                logger.LogWarning("Trade static data returned {Status}; using cached currency icons if present", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TradeRateLimitedException or IOException or JsonException)
        {
            logger.LogWarning(ex, "Trade static data refresh failed; using cached currency icons if present");
        }

        // Stale cache beats no icons; an empty result allows a retry on the next request.
        var stale = File.Exists(cachePath) ? await ReadIconCacheAsync(cachePath).ConfigureAwait(false) : null;
        if (stale is { Count: > 0 })
        {
            return stale;
        }

        _currencyIcons.TryRemove(game.Game, out _);
        return NoIcons;
    }

    public Task<IReadOnlyDictionary<string, string[]>> GetUniquesByBaseAsync(IGameModule game, CancellationToken cancellationToken)
    {
        var lazy = _uniques.GetOrAdd(game.Game, _ => new Lazy<Task<IReadOnlyDictionary<string, string[]>>>(() => LoadUniquesAsync(game)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The trade item list is large (every base and unique), so only the compact base → unique names map is kept on
    /// disk; later starts read a few dozen KB and the endpoint is hit at most once a day.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string[]>> LoadUniquesAsync(IGameModule game)
    {
        var cachePath = Path.Combine(paths.CacheDirectory, $"trade-uniques-{game.NinjaSegment}.json");
        if (File.Exists(cachePath) && time.GetUtcNow() - File.GetLastWriteTimeUtc(cachePath) < UniquesMaxAge
            && await ReadUniquesCacheAsync(cachePath).ConfigureAwait(false) is { Count: > 0 } cached)
        {
            return cached;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var endpoint = $"{game.TradeApiSegment}:data";
            await limiter.AcquireAsync(endpoint, cts.Token).ConfigureAwait(false);
            using var response = await http.GetAsync($"{OfficialTradeClient.BaseUrl}/api/{game.TradeApiSegment}/data/items", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                var uniques = ParseUniques(doc.RootElement);
                if (uniques.Count > 0)
                {
                    await WriteUniquesCacheAsync(cachePath, uniques).ConfigureAwait(false);
                    return uniques;
                }
            }
            else
            {
                logger.LogWarning("Trade item data returned {Status}; using cached uniques if present", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TradeRateLimitedException or IOException or JsonException)
        {
            logger.LogWarning(ex, "Trade item data refresh failed; using cached uniques if present");
        }

        var stale = File.Exists(cachePath) ? await ReadUniquesCacheAsync(cachePath).ConfigureAwait(false) : null;
        if (stale is { Count: > 0 })
        {
            return stale;
        }

        _uniques.TryRemove(game.Game, out _); // allow a later retry
        return NoUniques;
    }

    private async Task<IReadOnlyDictionary<string, string[]>?> ReadUniquesCacheAsync(string path)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var names = new string[p.Value.GetArrayLength()];
                var n = 0;
                foreach (var e in p.Value.EnumerateArray())
                {
                    if (e.GetString() is { Length: > 0 } name)
                    {
                        names[n++] = name;
                    }
                }

                if (n > 0)
                {
                    map[p.Name] = n == names.Length ? names : names[..n];
                }
            }

            return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Cached uniques unreadable");
            return null;
        }
    }

    private async Task WriteUniquesCacheAsync(string path, IReadOnlyDictionary<string, string[]> uniques)
    {
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temp))
            await using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var (baseType, names) in uniques)
                {
                    writer.WriteStartArray(baseType);
                    foreach (var name in names)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not cache uniques on disk");
        }
    }

    /// <summary>
    /// Reads the unique entries ({"name", "type", "flags": {"unique": true}}) of the trade item list. Variants of one
    /// unique (legacy versions, "disc" discriminators) share a name and are listed once.
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]> ParseUniques(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return NoUniques;
        }

        var sets = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Object
                    || !flags.TryGetProperty("unique", out var unique) || unique.ValueKind != JsonValueKind.True
                    || !e.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { Length: > 0 } name
                    || !e.TryGetProperty("type", out var typeEl) || typeEl.GetString() is not { Length: > 0 } type)
                {
                    continue;
                }

                if (!sets.TryGetValue(type, out var names))
                {
                    names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    sets[type] = names;
                }

                names.Add(name);
            }
        }

        var map = new Dictionary<string, string[]>(sets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, names) in sets)
        {
            map[type] = [.. names];
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadIconCacheAsync(string path)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.GetString() is { Length: > 0 } url)
                {
                    map[p.Name] = url;
                }
            }

            return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Cached currency icons unreadable");
            return null;
        }
    }

    private async Task WriteIconCacheAsync(string path, Dictionary<string, string> icons)
    {
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temp))
            await using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var (id, url) in icons)
                {
                    writer.WriteString(id, url);
                }

                writer.WriteEndObject();
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not cache currency icons on disk");
        }
    }

    internal static Dictionary<string, string> ParseCurrencyIcons(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("result", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("id", out var gid) || !string.Equals(gid.GetString(), "Currency", StringComparison.OrdinalIgnoreCase)
                || !group.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id
                    && e.TryGetProperty("image", out var imgEl) && imgEl.GetString() is { Length: > 0 } image)
                {
                    map[id] = image.StartsWith('/') ? ImageHost + image : image;
                }
            }
        }

        return map;
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

                // Multi-line stats stay whole: StatIndex matches them against consecutive item lines.
                type ??= id.Split('.')[0];
                yield return new TradeStat(id, text, type);
            }
        }
    }
}
