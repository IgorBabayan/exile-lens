using System.Text.Json;
using System.Text.Json.Nodes;
using ExileLens.Application.Storage;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a mutation, raises <see cref="Changed"/> and schedules a debounced atomic save.</summary>
    void Update(Action<AppSettings> mutate);

    Task FlushAsync();
}

public sealed class SettingsService : ISettingsService, IAsyncDisposable
{
    private readonly string _path;
    private readonly ILogger<SettingsService> _logger;
    private readonly DebouncedSaver _saver;
    private readonly Lock _gate = new();

    public SettingsService(IAppPaths paths, ILogger<SettingsService> logger)
    {
        _path = Path.Combine(paths.DataDirectory, "settings.json");
        _logger = logger;
        _saver = new DebouncedSaver(SaveAsync, TimeSpan.FromMilliseconds(400));
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new JsonException("Settings root is not an object.");
            var migrated = SettingsMigrator.Migrate(node, out var changed);
            Current = migrated.Deserialize(AppJsonContext.Default.AppSettings) ?? new AppSettings();
            if (changed)
            {
                _logger.LogInformation("Settings migrated to schema {Version}", AppSettings.CurrentSchemaVersion);
                _saver.Request();
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var moved = AtomicFile.Quarantine(_path);
            _logger.LogWarning(ex, "Settings file was unreadable and was moved to {Path}; defaults loaded", moved);
            Current = new AppSettings();
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _saver.Request();
    }

    public Task FlushAsync() => _saver.FlushAsync();

    private Task SaveAsync(CancellationToken cancellationToken)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = Current;
        }

        return AtomicFile.WriteJsonAsync(_path, snapshot, AppJsonContext.Default.AppSettings, cancellationToken);
    }

    public ValueTask DisposeAsync() => _saver.DisposeAsync();
}

/// <summary>Forward-only, step-wise migration of the raw settings JSON before typed deserialization.</summary>
public static class SettingsMigrator
{
    private static readonly SortedDictionary<int, Action<JsonObject>> Steps = new()
    {
        // 0 → 1: unversioned pre-release files kept a single shared "priceCheckHotkey"/"mapCheckHotkey"
        // at the root; schema 1 keeps shortcuts per game.
        [0] = root =>
        {
            foreach (var (legacy, target) in new[] { ("priceCheckHotkey", "priceCheckHotkey"), ("mapCheckHotkey", "mapCheckHotkey") })
            {
                if (root[legacy] is JsonValue value)
                {
                    var text = value.GetValue<string>();
                    root.Remove(legacy);
                    foreach (var game in new[] { "poe1", "poe2" })
                    {
                        if (root[game] is not JsonObject g)
                        {
                            g = new JsonObject();
                            root[game] = g;
                        }

                        g[target] ??= text;
                    }
                }
            }
        },
    };

    public static JsonObject Migrate(JsonObject root, out bool changed)
    {
        changed = false;
        var version = root["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 0;
        if (version > AppSettings.CurrentSchemaVersion)
        {
            // Written by a newer build: keep it readable, unknown properties are ignored.
            return root;
        }

        while (version < AppSettings.CurrentSchemaVersion)
        {
            if (Steps.TryGetValue(version, out var step))
            {
                step(root);
            }

            version++;
            changed = true;
        }

        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        return root;
    }
}
