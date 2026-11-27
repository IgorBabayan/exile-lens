using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace ExileLens.Infrastructure.Trade;

/// <summary>
/// Client for the official trade search/fetch endpoints used by pathofexile.com/trade and /trade2.
/// These endpoints are not part of GGG's documented OAuth API; they are used read-only, at human-like
/// volume (one search per user action), with server-advertised rate limits enforced client-side.
/// </summary>
public sealed class OfficialTradeClient(HttpClient http, RateLimiter limiter, ILogger<OfficialTradeClient> logger) : ITradeClient
{
    public const string BaseUrl = "https://www.pathofexile.com";

    public async Task<TradeSearchResult> SearchAsync(IGameModule game, string league, JsonObject query, CancellationToken cancellationToken)
    {
        var endpoint = $"{game.TradeApiSegment}:search";
        await limiter.AcquireAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var url = $"{BaseUrl}/api/{game.TradeApiSegment}/search/{RealmPrefix(game)}{Uri.EscapeDataString(league)}";
        using var content = new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
        await EnsureSuccessAsync(response, endpoint, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseSearch(doc.RootElement);
    }

    public async Task<IReadOnlyList<TradeListing>> FetchAsync(IGameModule game, string queryId, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var endpoint = $"{game.TradeApiSegment}:fetch";
        await limiter.AcquireAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var url = $"{BaseUrl}/api/{game.TradeApiSegment}/fetch/{string.Join(',', ids)}?query={Uri.EscapeDataString(queryId)}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        limiter.Observe(endpoint, response.Headers, (int)response.StatusCode);
        await EnsureSuccessAsync(response, endpoint, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseFetch(doc.RootElement);
    }

    public string BuildSearchUrl(IGameModule game, string league, string queryId) =>
        $"{BaseUrl}/{game.TradeSitePath}/search/{RealmPrefix(game)}{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(queryId)}";

    public string BuildSearchUrl(IGameModule game, string league, JsonObject query) =>
        $"{BaseUrl}/{game.TradeSitePath}/search/{RealmPrefix(game)}{Uri.EscapeDataString(league)}?q={Uri.EscapeDataString(query.ToJsonString())}";

    private static string RealmPrefix(IGameModule game) => game.TradeRealmSegment is { } r ? r + "/" : string.Empty;

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string endpoint, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new TradeRateLimitedException(limiter.RemainingBlock(endpoint), endpoint);
        }

        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
            {
                message = msg.GetString();
            }
        }
        catch (JsonException)
        {
        }

        logger.LogWarning("Trade API {Endpoint} returned {Status}: {Message}", endpoint, (int)response.StatusCode, message);
        throw new MarketUnavailableException(message is null
            ? $"Trade service returned {(int)response.StatusCode}."
            : $"Trade service: {message}");
    }

    internal static TradeSearchResult ParseSearch(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var total = root.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var t) ? t : 0;
        var ids = new List<string>();
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in result.EnumerateArray())
            {
                if (r.GetString() is { } s)
                {
                    ids.Add(s);
                }
            }
        }

        return new TradeSearchResult(id, total, ids);
    }

    internal static List<TradeListing> ParseFetch(JsonElement root)
    {
        var list = new List<TradeListing>();
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in result.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("listing", out var listing))
            {
                continue;
            }

            if (!listing.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Object)
            {
                continue; // unpriced listing
            }

            var amount = price.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var dec) ? dec : 0m;
            var currency = price.TryGetProperty("currency", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            if (amount <= 0 || currency.Length == 0)
            {
                continue;
            }

            string? account = null;
            bool? online = null;
            if (listing.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
            {
                account = acc.TryGetProperty("name", out var n) ? n.GetString() : null;
                online = acc.TryGetProperty("online", out var on) ? on.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) : false;
            }

            DateTimeOffset? indexed = listing.TryGetProperty("indexed", out var ix)
                                      && DateTimeOffset.TryParse(ix.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : null;

            string? summary = null;
            string? icon = null;
            string? type = null;
            if (entry.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                var name = item.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                type = item.TryGetProperty("typeLine", out var tl) ? tl.GetString() : null;
                icon = item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
                var details = IsGem(item)
                    ? GemDetails(item)
                    : item.TryGetProperty("ilvl", out var il) && il.TryGetInt32(out var lvl) ? $" · ilvl {lvl}" : string.Empty;
                summary = string.IsNullOrEmpty(name) ? $"{type}{details}" : $"{name}, {type}{details}";
            }

            var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            list.Add(new TradeListing(id, amount, currency, account, online, indexed, summary, icon, type));
        }

        return list;
    }

    /// <summary>Trade API frame type of skill and support gems (their "ilvl" is always 0 and meaningless).</summary>
    private const int GemFrameType = 4;

    private static bool IsGem(JsonElement item) =>
        item.TryGetProperty("frameType", out var ft) && ft.TryGetInt32(out var frame) && frame == GemFrameType;

    /// <summary>" · lvl 20 · 20%" from the gem's "Level" / "Quality" properties (single pass, stops once both are found).</summary>
    internal static string GemDetails(JsonElement item)
    {
        if (!item.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string? level = null, quality = null;
        foreach (var prop in props.EnumerateArray())
        {
            if (!prop.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (level is null && n.ValueEquals("Level"))
            {
                level = FirstValue(prop);
            }
            else if (quality is null && n.ValueEquals("Quality"))
            {
                quality = FirstValue(prop);
            }

            if (level is not null && quality is not null)
            {
                break;
            }
        }

        level = LeadingNumber(level);
        quality = LeadingNumber(quality);
        return (level, quality) switch
        {
            (not null, not null) => $" · lvl {level} · {quality}%",
            (not null, null) => $" · lvl {level}",
            (null, not null) => $" · {quality}%",
            _ => string.Empty,
        };
    }

    /// <summary>First display value of a trade property: values = [["20 (Max)", 0]].</summary>
    private static string? FirstValue(JsonElement prop) =>
        prop.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
        && values[0].ValueKind == JsonValueKind.Array && values[0].GetArrayLength() > 0 && values[0][0].ValueKind == JsonValueKind.String
            ? values[0][0].GetString()
            : null;

    /// <summary>"+20%" → "20", "20 (Max)" → "20"; null when there is no number.</summary>
    private static string? LeadingNumber(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var span = value.AsSpan().TrimStart().TrimStart('+');
        var end = 0;
        while (end < span.Length && char.IsAsciiDigit(span[end]))
        {
            end++;
        }

        return end == 0 ? null : span[..end].ToString();
    }
}

public static class HttpClientFactory
{
    public const string UserAgent = "ExileLens/0.1 (+https://github.com/IgorBabayan/exile-lens)";

    /// <summary>One pooled, long-lived client for the whole app (avoids socket exhaustion and handshake cost).</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
