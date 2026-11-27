using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.App.Services;
using ExileLens.Application.Capture;
using ExileLens.Application.History;
using ExileLens.Application.Platform;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;

namespace ExileLens.App.ViewModels;

public sealed partial class FilterItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public FilterItemViewModel(SearchFilter filter, Action changed)
    {
        Filter = filter;
        _changed = changed;
        _enabled = filter.Enabled;
        _minText = Formatting.Format(filter.Min);
        _maxText = Formatting.Format(filter.Max);
    }

    public SearchFilter Filter { get; }

    public string Label => Filter.Kind switch
    {
        FilterKind.ItemLevel => "Item level",
        FilterKind.Quality => "Quality",
        FilterKind.Sockets => "Sockets",
        FilterKind.Links => "Links",
        FilterKind.GemLevel => "Gem level",
        FilterKind.MapTier => "Tier",
        _ => Filter.Label,
    };

    public bool IsStat => Filter.Kind == FilterKind.Stat;

    public bool HasRange => Filter.Kind is not (FilterKind.Corrupted or FilterKind.Rarity or FilterKind.Name);

    public bool HasMax => Filter.Kind is FilterKind.MapTier or FilterKind.GemLevel or FilterKind.ItemLevel;

    public string? KindTag => Filter.ModifierKind switch
    {
        ModifierKind.Implicit => "implicit",
        ModifierKind.Crafted => "crafted",
        ModifierKind.Fractured => "fractured",
        ModifierKind.Enchant => "enchant",
        ModifierKind.Rune => "rune",
        ModifierKind.Desecrated => "desecrated",
        _ => null,
    };

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string? _minText;

    [ObservableProperty]
    private string? _maxText;

    partial void OnEnabledChanged(bool value)
    {
        Filter.Enabled = value;
        _changed();
    }

    partial void OnMinTextChanged(string? value)
    {
        if (Formatting.TryParseDouble(value, out var v))
        {
            Filter.Min = v;
            if (Filter.Enabled)
            {
                _changed();
            }
        }
    }

    partial void OnMaxTextChanged(string? value)
    {
        if (Formatting.TryParseDouble(value, out var v))
        {
            Filter.Max = v;
            if (Filter.Enabled)
            {
                _changed();
            }
        }
    }
}

public sealed record ListingItemViewModel(string Price, string Seller, bool Online, string Age, string? Summary);

public sealed record KeyValueRow(string Label, string Value);

public sealed partial class PriceCheckOverlayViewModel : ObservableObject, IOverlayContent
{
    /// <summary>Quiet period after the last filter edit before a search is sent (avoids a request per click/keystroke).</summary>
    private static readonly TimeSpan FilterSearchDelay = TimeSpan.FromMilliseconds(2500);

    private readonly IPriceEstimationService _pricing;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly IClipboardService _clipboard;
    private readonly IOverlayService _overlay;
    private readonly ItemIconLoader _icons;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _iconCts;
    private string? _iconUrl;
    private PriceCheckSession? _session;
    private bool _historyRecorded;

    public PriceCheckOverlayViewModel(
        IPriceEstimationService pricing,
        IHistoryService history,
        ISettingsService settings,
        IClipboardService clipboard,
        IOverlayService overlay,
        ItemIconLoader icons,
        TimeProvider time)
    {
        _pricing = pricing;
        _history = history;
        _settings = settings;
        _clipboard = clipboard;
        _overlay = overlay;
        _icons = icons;
        _time = time;
        _debounce = new DispatcherTimer(FilterSearchDelay, DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            IsSearchPending = false;
            _ = RunAsync();
        });
    }

    /// <summary>True when shown inside the main window rather than as an in-game overlay.</summary>
    public bool IsEmbedded { get; set; }

    public bool ShowWindowControls => !IsEmbedded;

    /// <summary>Only the market listings scroll; the overlay window must not add a page-level scrollbar.</summary>
    public bool ManagesOwnScrolling => true;

    [ObservableProperty]
    private string _gameLabel = string.Empty;

    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty]
    private string? _baseLine;

    [ObservableProperty]
    private string _metaLine = string.Empty;

    [ObservableProperty]
    private IBrush _nameBrush = Brushes.Gray;

    [ObservableProperty]
    private string _initial = "?";

    /// <summary>Item art; null shows the initial-letter placeholder.</summary>
    [ObservableProperty]
    private Bitmap? _icon;

    /// <summary>Filters changed and a search is scheduled once the user stops editing.</summary>
    [ObservableProperty]
    private bool _isSearchPending;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasPrice;

    [ObservableProperty]
    private string _priceMain = string.Empty;

    [ObservableProperty]
    private string _priceCurrency = string.Empty;

    [ObservableProperty]
    private string _priceDetail = string.Empty;

    [ObservableProperty]
    private string? _priceNote;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Market listings are expanded by default; a manual collapse is kept while this overlay is open.</summary>
    [ObservableProperty]
    private bool _showListings = true;

    [ObservableProperty]
    private bool _hasRewards;

    [ObservableProperty]
    private string _rewardTitle = "Disenchantment";

    [ObservableProperty]
    private string? _recommendation;

    [ObservableProperty]
    private IBrush _recommendationBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _canOpenTrade;

    public ObservableCollection<FilterItemViewModel> StatFilters { get; } = [];

    public ObservableCollection<FilterItemViewModel> PropertyFilters { get; } = [];

    public ObservableCollection<ListingItemViewModel> Listings { get; } = [];

    public ObservableCollection<KeyValueRow> RewardRows { get; } = [];

    public bool HasStatFilters => StatFilters.Count > 0;

    public bool HasPropertyFilters => PropertyFilters.Count > 0;

    public bool HasListings => Listings.Count > 0;

    public void Load(PriceCheckSession session)
    {
        _session = session;
        _historyRecorded = false;
        var item = session.Item;
        GameLabel = session.Game.ShortName;
        ItemName = item.DisplayName;
        BaseLine = item.Name is not null ? item.BaseType : null;
        NameBrush = DangerPalette.Rarity(item.Rarity);
        Initial = item.DisplayName.Length > 0 ? item.DisplayName[..1] : "?";
        MetaLine = BuildMeta(item);
        ResetIcon();
        ShowIcon(_pricing.PeekIconUrl(session));
        _debounce.Stop();
        IsSearchPending = false;

        StatFilters.Clear();
        PropertyFilters.Clear();
        foreach (var f in session.Filters)
        {
            var vm = new FilterItemViewModel(f, OnFilterChanged);
            (vm.IsStat ? StatFilters : PropertyFilters).Add(vm);
        }

        OnPropertyChanged(nameof(HasStatFilters));
        OnPropertyChanged(nameof(HasPropertyFilters));

        Listings.Clear();
        OnPropertyChanged(nameof(HasListings));
        HasPrice = false;
        Error = null;
        PriceNote = null;
        CanOpenTrade = !session.UsesBulkPricing;
        ApplyRewards(_pricing.EstimateRewardsLocal(session, null), null);
    }

    public void Start()
    {
        if (_session is null)
        {
            return;
        }

        if (_session.UsesBulkPricing || _settings.Current.Trade.AutoSearch)
        {
            _ = RunAsync();
        }
        else
        {
            PriceDetail = "Press refresh to search the market.";
        }
    }

    public void Detach()
    {
        _debounce.Stop();
        IsSearchPending = false;
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnFilterChanged()
    {
        // Restart the quiet period on every edit; the search runs only once nothing has changed for FilterSearchDelay.
        _debounce.Stop();
        _debounce.Start();
        IsSearchPending = true;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        // Explicit refresh wins over a pending filter search.
        _debounce.Stop();
        IsSearchPending = false;
        return RunAsync();
    }

    private void ResetIcon()
    {
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = null;
        _iconUrl = null;
        Icon = null;
    }

    private void ShowIcon(string? url)
    {
        if (url is null || url == _iconUrl)
        {
            return;
        }

        _iconUrl = url;
        if (_icons.TryGetCached(url) is { } cached)
        {
            Icon = cached; // already decoded: no async hop, no placeholder flash
            return;
        }

        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();
        _ = LoadIconAsync(url, _iconCts.Token);
    }

    private async Task LoadIconAsync(string url, CancellationToken token)
    {
        try
        {
            var bitmap = await _icons.LoadAsync(url, token);
            if (!token.IsCancellationRequested && bitmap is not null && url == _iconUrl)
            {
                Icon = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // another item was opened
        }
    }

    private async Task RunAsync()
    {
        if (_session is null)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsLoading = true;
        Error = null;
        try
        {
            var result = await _pricing.RunAsync(_session, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Apply(result);
        }
        catch (OperationCanceledException)
        {
            // superseded by newer filters or the overlay closed
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(PriceCheckResult result)
    {
        var e = result.Estimate;
        CanOpenTrade = !_session!.UsesBulkPricing;
        ShowIcon(result.IconUrl);
        Error = result.Error;

        if (e.HasValue)
        {
            var low = e.RangeLow ?? e.Median!.Value;
            var high = e.RangeHigh ?? e.Median!.Value;
            PriceMain = Formatting.Range(low, high);
            PriceCurrency = Formatting.Plural(e.ValuationCurrency, high);
            var stack = _session.Item.StackSize is > 1 && _session.UsesBulkPricing
                ? $" · stack of {_session.Item.StackSize}: {Formatting.Amount(e.Median!.Value * _session.Item.StackSize.Value)}"
                : string.Empty;
            PriceDetail = e.IsListingPrice
                ? $"{e.MatchingListings} matching listings · {Formatting.Confidence(e.Confidence)}"
                : $"{e.Source} · {Formatting.Confidence(e.Confidence)}{stack}";
            HasPrice = true;
        }
        else
        {
            HasPrice = false;
            PriceDetail = e.MatchingListings > 0
                ? $"{e.MatchingListings} listings, but none could be valued"
                : result.Error is null ? "No matching listings — not enough market data" : string.Empty;
        }

        PriceNote = e.IsListingPrice
            ? $"Asking prices, not completed sales · {(result.FromCache ? "cached" : "updated")} {Formatting.Age(e.DataTimestamp, _time.GetUtcNow())}"
            : $"Exchange rate data · updated {Formatting.Age(e.DataTimestamp, _time.GetUtcNow())}";

        Listings.Clear();
        foreach (var l in result.Listings.Take(_settings.Current.Trade.MaxListings))
        {
            Listings.Add(new ListingItemViewModel(
                $"{Formatting.Amount(l.ListedPrice.Amount)} {Formatting.Plural(l.ListedPrice.Currency, l.ListedPrice.Amount)}",
                l.AccountName ?? "Unknown seller",
                l.SellerOnline == true,
                l.IndexedAt is { } at ? Formatting.Age(at, _time.GetUtcNow()) : string.Empty,
                l.ItemSummary));
        }

        OnPropertyChanged(nameof(HasListings));
        ApplyRewards(result.Rewards, e);
        RecordHistory(result);
    }

    private void ApplyRewards(RewardSummary rewards, PriceEstimate? market)
    {
        RewardRows.Clear();
        var s = _settings.Current.Rewards;
        var primary = rewards.Disenchant ?? rewards.Vendor;
        if (primary is not null)
        {
            RewardTitle = primary.Kind == RewardKind.Vendor ? "Vendor recipe" : "Disenchantment";
            AddRewardRows(primary, "Expected reward", "Estimated value");
        }

        if (rewards.Salvage is { } salvage)
        {
            if (primary is null)
            {
                RewardTitle = "Salvage";
            }

            AddRewardRows(salvage, primary is null ? "Expected reward" : "Salvage reward", primary is null ? "Estimated value" : "Salvage value");
        }

        HasRewards = RewardRows.Count > 0;

        if (rewards.Comparison is { } c && s.ShowSuggestedAction && market is not null)
        {
            Recommendation = c.Action switch
            {
                SuggestedAction.SellOnTrade => "Selling on trade is more valuable",
                SuggestedAction.Disenchant => $"Disenchant — {c.Explanation}",
                SuggestedAction.Salvage => $"Salvage — {c.Explanation}",
                SuggestedAction.SellToVendor => $"Sell to vendor — {c.Explanation}",
                _ => c.Explanation,
            };
            RecommendationBrush = c.Action switch
            {
                SuggestedAction.SellOnTrade => Resource("SafeBrush"),
                SuggestedAction.InsufficientMarketData => Resource("TextSecondaryBrush"),
                _ => Resource("AccentBrush"),
            };
        }
        else
        {
            Recommendation = null;
        }
    }

    private void AddRewardRows(RewardEstimate r, string rewardLabel, string valueLabel)
    {
        var parts = r.Lines.Select(l =>
            l.MinQuantity is { } min && l.MaxQuantity is { } max && min != max
                ? $"{Formatting.Amount(min)}–{Formatting.Amount(max)} {Formatting.Plural(l.Currency, max)}"
                : $"{Formatting.Amount(l.Quantity)} {Formatting.Plural(l.Currency, l.Quantity)}");
        RewardRows.Add(new KeyValueRow(rewardLabel, string.Join(", ", parts)));
        if (_settings.Current.Rewards.ShowComparison)
        {
            RewardRows.Add(new KeyValueRow(valueLabel, r.TotalValue is { } v && r.ValuationCurrency is { } cur
                ? $"{(r.IsExact ? string.Empty : "≈ ")}{Formatting.Amount(v)} {Formatting.Plural(cur, v)}"
                : "No market rate"));
        }
    }

    private void RecordHistory(PriceCheckResult result)
    {
        if (_historyRecorded || _session is null)
        {
            return;
        }

        _historyRecorded = true;
        var item = _session.Item;
        var e = result.Estimate;
        var d = result.Rewards.Disenchant ?? result.Rewards.Vendor;
        _history.Add(new PriceCheckHistoryEntry
        {
            Timestamp = DateTimeOffset.Now,
            Game = item.Game,
            League = _session.League,
            ItemName = item.DisplayName,
            BaseType = item.BaseType,
            Rarity = item.Rarity,
            ItemLevel = item.ItemLevel,
            ValuationCurrency = e.ValuationCurrency,
            MarketLow = e.RangeLow,
            MarketHigh = e.RangeHigh,
            MatchingListings = e.MatchingListings,
            Confidence = e.Confidence.ToString(),
            DisenchantValue = d?.TotalValue,
            DisenchantSummary = d is null ? null : string.Join(", ", d.Lines.Select(l => $"{Formatting.Amount(l.Quantity)} {Formatting.Plural(l.Currency, l.Quantity)}")),
        });
    }

    [RelayCommand]
    private async Task CopyPriceAsync()
    {
        if (HasPrice)
        {
            await _clipboard.SetTextAsync($"{PriceMain} {PriceCurrency}");
        }
    }

    [RelayCommand]
    private void OpenTrade()
    {
        if (_session is null)
        {
            return;
        }

        // Always reflects the filters currently shown in the overlay.
        Formatting.OpenUrl(_pricing.BuildTradeUrl(_session));
    }

    [RelayCommand]
    private void Close() => _overlay.Close();

    private static string BuildMeta(ParsedItem item)
    {
        var parts = new List<string>(4);
        parts.Add(item.Rarity switch
        {
            ItemRarity.DivinationCard => "Divination card",
            ItemRarity.Unknown => item.ItemClass ?? "Item",
            _ => item.Rarity.ToString(),
        });
        if (item.MapTier is { } tier)
        {
            parts.Add($"Tier {tier}");
        }

        if (item.GemLevel is { } gl)
        {
            parts.Add($"Level {gl}");
        }

        if (item.ItemLevel is { } ilvl && item.Category is not (ItemCategory.Currency or ItemCategory.Gem))
        {
            parts.Add($"Item level {ilvl}");
        }

        if (item.Quality is > 0)
        {
            parts.Add($"{item.Quality}% quality");
        }

        if (item.StackSize is { } stack && item.MaxStackSize is { } max)
        {
            parts.Add($"Stack {stack}/{max}");
        }

        if (item.IsCorrupted)
        {
            parts.Add("Corrupted");
        }

        if (!item.IsIdentified)
        {
            parts.Add("Unidentified");
        }

        return string.Join(" · ", parts);
    }

    private static IBrush Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
}
