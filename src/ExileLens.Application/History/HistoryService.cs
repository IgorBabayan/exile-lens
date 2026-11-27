using System.Text.Json;
using ExileLens.Application.Settings;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.History;

public sealed class PriceCheckHistoryEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public GameId Game { get; set; }
    public string? League { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string BaseType { get; set; } = string.Empty;
    public ItemRarity Rarity { get; set; }
    public int? ItemLevel { get; set; }
    public string? ValuationCurrency { get; set; }
    public decimal? MarketLow { get; set; }
    public decimal? MarketHigh { get; set; }
    public int MatchingListings { get; set; }
    public string? Confidence { get; set; }
    public decimal? DisenchantValue { get; set; }
    public string? DisenchantSummary { get; set; }
}

public sealed class MapCheckHistoryEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public GameId Game { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int? Tier { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public DangerLevel Overall { get; set; }
    public List<string> Modifiers { get; set; } = [];
}

public sealed class HistoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<PriceCheckHistoryEntry> PriceChecks { get; set; } = [];
    public List<MapCheckHistoryEntry> MapChecks { get; set; } = [];
}

public interface IHistoryService
{
    event EventHandler? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<PriceCheckHistoryEntry> PriceChecks { get; }
    IReadOnlyList<MapCheckHistoryEntry> MapChecks { get; }
    void Add(PriceCheckHistoryEntry entry);
    void Add(MapCheckHistoryEntry entry);
    void Clear();
    Task FlushAsync();
}

/// <summary>Newest-first bounded history, persisted with debounced atomic writes. No raw clipboard text is stored.</summary>
public sealed class HistoryService : IHistoryService, IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<HistoryService> _logger;
    private readonly string _path;
    private readonly DebouncedSaver _saver;
    private readonly Lock _gate = new();
    private List<PriceCheckHistoryEntry> _price = [];
    private List<MapCheckHistoryEntry> _maps = [];

    public HistoryService(ISettingsService settings, IAppPaths paths, ILogger<HistoryService> logger)
    {
        _settings = settings;
        _logger = logger;
        _path = Path.Combine(paths.DataDirectory, "history.json");
        _saver = new DebouncedSaver(SaveAsync, TimeSpan.FromSeconds(2));
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PriceCheckHistoryEntry> PriceChecks
    {
        get
        {
            lock (_gate)
            {
                return [.. _price];
            }
        }
    }

    public IReadOnlyList<MapCheckHistoryEntry> MapChecks
    {
        get
        {
            lock (_gate)
            {
                return [.. _maps];
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var doc = await AtomicFile.ReadJsonAsync(_path, AppJsonContext.Default.HistoryDocument, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _price = doc?.PriceChecks ?? [];
                _maps = doc?.MapChecks ?? [];
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "history.json unreadable; moved aside");
            AtomicFile.Quarantine(_path);
        }
    }

    public void Add(PriceCheckHistoryEntry entry)
    {
        lock (_gate)
        {
            _price.Insert(0, entry);
            Trim(_price, _settings.Current.History.MaxPriceChecks);
        }

        Commit();
    }

    public void Add(MapCheckHistoryEntry entry)
    {
        lock (_gate)
        {
            _maps.Insert(0, entry);
            Trim(_maps, _settings.Current.History.MaxMapChecks);
        }

        Commit();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _price.Clear();
            _maps.Clear();
        }

        Commit();
    }

    public Task FlushAsync() => _saver.FlushAsync();

    public ValueTask DisposeAsync() => _saver.DisposeAsync();

    private static void Trim<T>(List<T> list, int max)
    {
        max = Math.Max(0, max);
        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }
    }

    private void Commit()
    {
        _saver.Request();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task SaveAsync(CancellationToken cancellationToken)
    {
        HistoryDocument doc;
        lock (_gate)
        {
            doc = new HistoryDocument { PriceChecks = [.. _price], MapChecks = [.. _maps] };
        }

        return AtomicFile.WriteJsonAsync(_path, doc, AppJsonContext.Default.HistoryDocument, cancellationToken);
    }
}
