using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ExileLens.Application.Storage;

public interface IAppPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string LogDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? overrideRoot = null)
    {
        var root = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "ExileLens");
        DataDirectory = root;
        CacheDirectory = Path.Combine(root, "cache");
        LogDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }
}

/// <summary>Atomic JSON persistence: write to a temp file in the same directory, flush, then rename over the target.</summary>
public static class AtomicFile
{
    public static async Task WriteJsonAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static async Task<T?> ReadJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an unreadable file aside so the app can start with defaults without losing user data.</summary>
    public static string? Quarantine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, target, overwrite: true);
        return target;
    }
}

/// <summary>Coalesces bursts of save requests (e.g. slider drags) into one write. Writes never overlap.</summary>
public sealed class DebouncedSaver : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _save;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private bool _dirty;

    public DebouncedSaver(Func<CancellationToken, Task> save, TimeSpan delay)
    {
        _save = save;
        _delay = delay;
    }

    public void Request()
    {
        CancellationToken token;
        lock (_gate)
        {
            _dirty = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = DelayThenSaveAsync(token);
    }

    private async Task DelayThenSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer request or a flush
        }

        await SaveIfDirtyAsync().ConfigureAwait(false);
    }

    private async Task SaveIfDirtyAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
            }

            await _save(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FlushAsync()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }

        await SaveIfDirtyAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
