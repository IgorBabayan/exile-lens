using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using ExileLens.Application.Storage;
using Microsoft.Extensions.Logging;

namespace ExileLens.App.Services;

/// <summary>
/// Loads item art for the price-check header. Three cache levels keep it cheap:
/// decoded bitmaps in a small in-memory LRU (instant re-show, no UI-thread work), raw files on disk
/// (no network after the first view, survives restarts), and coalesced in-flight downloads.
/// Images are decoded off the UI thread straight to the display size so large art never sits in memory.
/// </summary>
public sealed class ItemIconLoader
{
    /// <summary>Decode width: the 52 px slot rendered at up to 2x scaling.</summary>
    private const int DecodeWidth = 104;
    private const int MemoryCapacity = 64;
    private const long MaxDownloadBytes = 512 * 1024;

    private static readonly string[] AllowedHosts = ["web.poecdn.com", "poecdn.com", "poe.ninja", "www.pathofexile.com", "pathofexile.com"];

    private readonly HttpClient _http;
    private readonly ILogger<ItemIconLoader> _logger;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<(string Url, Bitmap Bitmap)>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, Bitmap Bitmap)> _lru = new();
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

    public ItemIconLoader(HttpClient http, IAppPaths paths, ILogger<ItemIconLoader> logger)
    {
        _http = http;
        _logger = logger;
        _directory = Path.Combine(paths.CacheDirectory, "icons");
    }

    /// <summary>Decoded bitmap if it is already in memory; lets the view show it synchronously without flicker.</summary>
    public Bitmap? TryGetCached(string url)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(url, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        return null;
    }

    public async Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        if (TryGetCached(url) is { } hit)
        {
            return hit;
        }

        if (_failed.ContainsKey(url) || !IsAllowed(url))
        {
            return null;
        }

        // Shared work is not tied to the caller's token: a quick item switch should not throw away a nearly finished download.
        var lazy = _inflight.GetOrAdd(url, u => new Lazy<Task<Bitmap?>>(() => Task.Run(() => LoadCoreAsync(u))));
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Bitmap?> LoadCoreAsync(string url)
    {
        try
        {
            var file = Path.Combine(_directory, FileName(url));
            byte[]? bytes = null;
            if (File.Exists(file))
            {
                try
                {
                    bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    bytes = null;
                }
            }

            if (bytes is not { Length: > 0 })
            {
                bytes = await DownloadAsync(url).ConfigureAwait(false);
                if (bytes is null)
                {
                    _failed.TryAdd(url, 0);
                    return null;
                }

                await SaveAsync(file, bytes).ConfigureAwait(false);
            }

            Bitmap bitmap;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
            }

            Remember(url, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            // Network, disk or decode failure: remember it so the placeholder stays and nothing is retried this session.
            _logger.LogDebug(ex, "Item icon {Url} could not be loaded", url);
            _failed.TryAdd(url, 0);
            return null;
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    private async Task<byte[]?> DownloadAsync(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return data.Length is > 0 and <= (int)MaxDownloadBytes ? data : null;
    }

    private async Task SaveAsync(string file, byte[] bytes)
    {
        var temp = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk cache is best effort; the decoded bitmap is still used.
            _logger.LogDebug(ex, "Could not cache item icon on disk");
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Remember(string url, Bitmap bitmap)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(url, out var existing))
            {
                _lru.Remove(existing);
            }

            _memory[url] = _lru.AddFirst((url, bitmap));
            while (_memory.Count > MemoryCapacity && _lru.Last is { } last)
            {
                // Not disposed: the evicted bitmap may still be on screen; it is small and the GC reclaims it.
                _lru.RemoveLast();
                _memory.Remove(last.Value.Url);
            }
        }
    }

    private static bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        foreach (var host in AllowedHosts)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FileName(string url)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(url), hash);
        return Convert.ToHexStringLower(hash) + ".png";
    }
}
