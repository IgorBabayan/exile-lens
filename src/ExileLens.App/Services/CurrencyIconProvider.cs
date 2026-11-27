using System.Collections.Concurrent;
using System.Collections.Frozen;
using Avalonia.Media.Imaging;
using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Core.Items;

namespace ExileLens.App.Services;

/// <summary>
/// Currency art by currency name ("Chaos Orb") for prices shown with icons. The id → URL map comes from the trade
/// static data (disk cached for a week) and bitmaps from <see cref="ItemIconLoader"/> (disk cached). Decoded icons are
/// also held here: there are only a handful, and keeping them avoids the item-art LRU evicting them, so after the
/// first use every lookup is a synchronous dictionary hit with no placeholder flash.
/// </summary>
public sealed class CurrencyIconProvider(ITradeDataProvider tradeData, ItemIconLoader icons)
{
    private readonly ConcurrentDictionary<GameId, IReadOnlyDictionary<string, string>> _urls = new();
    private readonly ConcurrentDictionary<GameId, FrozenDictionary<string, string>> _idsByName = new();
    private readonly ConcurrentDictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);

    /// <summary>Icon if it is already decoded; null otherwise. Never blocks.</summary>
    public Bitmap? TryGet(IGameModule game, string currencyName) =>
        Url(game, currencyName) is { } url && _bitmaps.TryGetValue(url, out var bitmap) ? bitmap : null;

    /// <summary>Icon for the currency; null when the game has no art for it or it cannot be loaded.</summary>
    public async Task<Bitmap?> GetAsync(IGameModule game, string currencyName, CancellationToken cancellationToken)
    {
        if (!_urls.ContainsKey(game.Game))
        {
            var urls = await tradeData.GetCurrencyIconsAsync(game, cancellationToken).ConfigureAwait(false);
            if (urls.Count == 0)
            {
                return null; // unavailable now; the provider allows a retry on the next request
            }

            _urls[game.Game] = urls;
        }

        if (Url(game, currencyName) is not { } url)
        {
            return null;
        }

        if (_bitmaps.TryGetValue(url, out var known))
        {
            return known;
        }

        var bitmap = await icons.LoadAsync(url, cancellationToken).ConfigureAwait(false);
        if (bitmap is not null)
        {
            _bitmaps[url] = bitmap;
        }

        return bitmap;
    }

    /// <summary>Background warm-up of the most common price currencies so the first overlay shows icons at once.</summary>
    public async Task WarmAsync(IGameModule game)
    {
        try
        {
            foreach (var name in (string[])[game.DefaultValuationCurrency, "Chaos Orb", "Divine Orb", "Exalted Orb"])
            {
                await GetAsync(game, name, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // best effort; icons load on demand later
        }
    }

    private string? Url(IGameModule game, string currencyName)
    {
        if (!_urls.TryGetValue(game.Game, out var urls))
        {
            return null;
        }

        // Listing prices carry names mapped from trade ids; an unmapped id is passed through as the name itself.
        var ids = _idsByName.GetOrAdd(game.Game, static (_, g) => g.TradeCurrencyNames
            .GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(grp => grp.Key, grp => grp.First().Key, StringComparer.OrdinalIgnoreCase), game);
        var id = ids.GetValueOrDefault(currencyName, currencyName);
        return urls.TryGetValue(id, out var url) ? url : null;
    }
}
