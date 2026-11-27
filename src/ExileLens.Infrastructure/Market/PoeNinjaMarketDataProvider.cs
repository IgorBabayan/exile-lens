using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Settings;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using Microsoft.Extensions.Logging;

namespace ExileLens.Infrastructure.Market;

/// <summary>
/// poe.ninja economy data (documented at https://poe.ninja/docs/api). Responses are cached in memory and
/// revalidated with ETags; data is only requested when a price check needs it (no background polling).
/// The base URL is configurable so a self-hosted caching proxy can be used, as poe.ninja recommends for
/// widely distributed desktop clients.
/// </summary>
public sealed class PoeNinjaMarketDataProvider(HttpClient http, ISettingsService settings, TimeProvider time, ILogger<PoeNinjaMarketDataProvider> logger)
    : IMarketDataProvider
{
    public const string DefaultBaseUrl = "https://poe.ninja";
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, CachedOverview> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<ExchangeOverview?>>> _inflight = new(StringComparer.Ordinal);

    public string Name => "poe.ninja";

    private sealed record CachedOverview(ExchangeOverview Overview, string? ETag, DateTimeOffset FetchedAt);

    internal sealed record ExchangeLine(string Id, string Name, decimal PrimaryValue, decimal Volume, string? Icon = null);

    private const string ImageCdn = "https://web.poecdn.com";

    internal sealed record ExchangeOverview(string PrimaryId, string PrimaryName, IReadOnlyList<ExchangeLine> Lines, DateTimeOffset FetchedAt);

    public async Task<CurrencyRates> GetCurrencyRatesAsync(IGameModule game, string league, CancellationToken cancellationToken)
    {
        var overview = await GetOverviewAsync(game, league, "Currency", cancellationToken).ConfigureAwait(false);
        if (overview is null)
        {
            return CurrencyRates.Empty(game.DefaultValuationCurrency);
        }

        var values = new List<KeyValuePair<string, decimal>>(overview.Lines.Count * 2);
        foreach (var line in overview.Lines)
        {
            values.Add(new(line.Name, line.PrimaryValue));
            values.Add(new(line.Id, line.PrimaryValue));
        }

        // Also expose trade ids ("exalted") under their display names when poe.ninja ids differ.
        foreach (var (tradeId, name) in game.TradeCurrencyNames)
        {
            var match = overview.Lines.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                values.Add(new(tradeId, match.PrimaryValue));
            }
        }

        // Shards and other exchange categories are valued in the same primary currency.
        foreach (var type in game.NinjaExchangeTypes)
        {
            if (type == "Currency")
            {
                continue;
            }

            var other = _cache.GetValueOrDefault(Key(game, league, type));
            if (other is not null)
            {
                values.AddRange(other.Overview.Lines.Select(l => new KeyValuePair<string, decimal>(l.Name, l.PrimaryValue)));
            }
        }

        return new CurrencyRates(overview.PrimaryName, values, overview.FetchedAt, Name);
    }

    public async Task<PriceEstimate?> GetBulkPriceAsync(IGameModule game, string league, ParsedItem item, string valuationCurrency, CancellationToken cancellationToken)
    {
        var rates = await GetCurrencyRatesAsync(game, league, cancellationToken).ConfigureAwait(false);
        foreach (var type in OrderedTypes(game, item))
        {
            var overview = await GetOverviewAsync(game, league, type, cancellationToken).ConfigureAwait(false);
            var line = overview?.Lines.FirstOrDefault(l => string.Equals(l.Name, item.BaseType, StringComparison.OrdinalIgnoreCase));
            if (overview is null || line is null)
            {
                continue;
            }

            var currency = valuationCurrency;
            if (!rates.TryConvert(line.PrimaryValue, overview.PrimaryName, valuationCurrency, out var value))
            {
                value = line.PrimaryValue;
                currency = overview.PrimaryName;
            }

            var confidence = line.Volume switch
            {
                >= 1000m => EstimateConfidence.High,
                >= 50m => EstimateConfidence.Medium,
                > 0m => EstimateConfidence.Low,
                _ => EstimateConfidence.Low,
            };
            return new PriceEstimate(currency, value, value, value, value, 0, 0, confidence, overview.FetchedAt, false, $"{Name} exchange");
        }

        return null;
    }

    public string? GetCachedIconUrl(IGameModule game, string league, string name)
    {
        // Cache-only lookup across the exchange categories that were already downloaded for this league.
        foreach (var type in game.NinjaExchangeTypes)
        {
            if (!_cache.TryGetValue(Key(game, league, type), out var cached))
            {
                continue;
            }

            foreach (var line in cached.Overview.Lines)
            {
                if (line.Icon is not null && string.Equals(line.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Icon;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> OrderedTypes(IGameModule game, ParsedItem item)
    {
        // Try the most likely category first to keep requests minimal.
        var preferred = item.Category switch
        {
            ItemCategory.Essence => game.Game == GameId.Poe2 ? "Essences" : "Essence",
            ItemCategory.Fragment => game.Game == GameId.Poe2 ? "Fragments" : "Fragment",
            ItemCategory.Scarab => "Scarab",
            ItemCategory.DivinationCard => "DivinationCard",
            ItemCategory.Rune => "Runes",
            ItemCategory.SoulCore => "SoulCores",
            ItemCategory.Omen => game.Game == GameId.Poe2 ? "Ritual" : "Omen",
            _ => "Currency",
        };
        yield return preferred;
        foreach (var t in game.NinjaExchangeTypes)
        {
            if (t != preferred)
            {
                yield return t;
            }
        }
    }

    internal async Task<ExchangeOverview?> GetOverviewAsync(IGameModule game, string league, string type, CancellationToken cancellationToken)
    {
        var key = Key(game, league, type);
        if (_cache.TryGetValue(key, out var cached) && time.GetUtcNow() - cached.FetchedAt < FreshFor)
        {
            return cached.Overview;
        }

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<ExchangeOverview?>>(() => FetchAsync(game, league, type, key)));
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExchangeOverview?> FetchAsync(IGameModule game, string league, string type, string key)
    {
        _cache.TryGetValue(key, out var cached);
        try
        {
            var baseUrl = settings.Current.Trade.NinjaBaseUrl;
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            var url = $"{baseUrl}/{game.NinjaSegment}/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached?.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                var refreshed = cached with { FetchedAt = now, Overview = cached.Overview with { FetchedAt = now } };
                _cache[key] = refreshed;
                return refreshed.Overview;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("poe.ninja {Type} returned {Status}", type, (int)response.StatusCode);
                return cached?.Overview;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var overview = ParseOverview(doc.RootElement, game, now);
            if (overview is null)
            {
                return cached?.Overview;
            }

            _cache[key] = new CachedOverview(overview, response.Headers.ETag?.ToString(), now);
            return overview;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogWarning(ex, "poe.ninja {Type} request failed", type);
            return cached?.Overview;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    internal static ExchangeOverview? ParseOverview(JsonElement root, IGameModule game, DateTimeOffset now)
    {
        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? primaryId = null;
        if (root.TryGetProperty("core", out var core) && core.ValueKind == JsonValueKind.Object)
        {
            primaryId = core.TryGetProperty("primary", out var p) ? p.GetString() : null;
            if (core.TryGetProperty("items", out var items))
            {
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        if (it.TryGetProperty("id", out var iid) && it.TryGetProperty("name", out var nm) && iid.GetString() is { } i && nm.GetString() is { } n)
                        {
                            names[i] = n;
                            AddIcon(icons, i, it);
                        }
                    }
                }
                else if (items.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in items.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("name", out var nm) && nm.GetString() is { } n)
                        {
                            names[prop.Name] = n;
                            AddIcon(icons, prop.Name, prop.Value);
                        }
                    }
                }
            }
        }

        // Exchange endpoints may also embed items next to lines.
        if (root.TryGetProperty("items", out var rootItems) && rootItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in rootItems.EnumerateArray())
            {
                if (it.TryGetProperty("id", out var iid) && it.TryGetProperty("name", out var nm) && iid.GetString() is { } i && nm.GetString() is { } n)
                {
                    names.TryAdd(i, n);
                    AddIcon(icons, i, it);
                }
            }
        }

        var result = new List<ExchangeLine>(lines.GetArrayLength());
        foreach (var line in lines.EnumerateArray())
        {
            var id = line.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id is null || !line.TryGetProperty("primaryValue", out var pv) || !pv.TryGetDecimal(out var value))
            {
                continue;
            }

            var volume = line.TryGetProperty("volumePrimaryValue", out var vol) && vol.TryGetDecimal(out var v) ? v : 0m;
            var name = names.GetValueOrDefault(id) ?? game.TradeCurrencyNames.GetValueOrDefault(id) ?? id;
            result.Add(new ExchangeLine(id, name, value, volume, icons.GetValueOrDefault(id)));
        }

        primaryId ??= game.Game == GameId.Poe2 ? "exalted" : "chaos";
        var primaryName = names.GetValueOrDefault(primaryId) ?? game.TradeCurrencyNames.GetValueOrDefault(primaryId) ?? game.DefaultValuationCurrency;
        return new ExchangeOverview(primaryId, primaryName, result, now);
    }

    private static void AddIcon(Dictionary<string, string> icons, string id, JsonElement item)
    {
        if (icons.ContainsKey(id))
        {
            return;
        }

        var raw = item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String ? img.GetString()
            : item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        // poe.ninja serves CDN-relative paths ("/gen/image/..."); trade-style absolute URLs are kept as-is.
        icons[id] = raw.StartsWith('/') ? ImageCdn + raw : raw;
    }

    private static string Key(IGameModule game, string league, string type) => $"{game.Game}|{league}|{type}";
}
