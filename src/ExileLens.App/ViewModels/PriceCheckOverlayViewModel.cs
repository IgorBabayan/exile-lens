using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExileLens.App.Services;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
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

    /// <summary>Origin tag shown before the modifier text ("fractured ", "crafted ", ...); null for plain explicits.</summary>
    public string? KindTag => Filter.ModifierKind switch
    {
        ModifierKind.Implicit => "implicit ",
        ModifierKind.Crafted => "crafted ",
        ModifierKind.Fractured => "fractured ",
        ModifierKind.Enchant => "enchant ",
        ModifierKind.Rune => "rune ",
        ModifierKind.Desecrated => "desecrated ",
        _ => null,
    };

    /// <summary>Drives the in-game fractured color on the modifier line.</summary>
    public bool IsFractured => Filter.ModifierKind == ModifierKind.Fractured;

    /// <summary>Drives the in-game crafted color on the modifier line.</summary>
    public bool IsCrafted => Filter.ModifierKind == ModifierKind.Crafted;

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

/// <summary>One market listing. <see cref="CurrencyIcon"/> is set only when prices are shown with currency art.</summary>
public sealed partial class ListingItemViewModel(string amount, string currencyName, string price, string seller, bool online, string age, string? summary)
    : ObservableObject
{
    public string Amount { get; } = amount;

    /// <summary>Singular currency name ("Chaos Orb"): icon lookup key and the icon's tooltip.</summary>
    public string CurrencyName { get; } = currencyName;

    /// <summary>Amount with the currency name, shown while no icon is available.</summary>
    public string Price { get; } = price;

    public string Seller { get; } = seller;

    public bool Online { get; } = online;

    public string Age { get; } = age;

    public string? Summary { get; } = summary;

    [ObservableProperty]
    private Bitmap? _currencyIcon;
}

public sealed record KeyValueRow(string Label, string Value);

/// <summary>One unique in the "which unique is this?" picker. The art arrives after the list is shown.</summary>
public sealed partial class UniqueChoiceViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Art URL once known; passed on so the chosen unique's header shows the same art at once.</summary>
    public string? IconUrl { get; set; }

    [ObservableProperty]
    private Bitmap? _icon;
}

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
    private readonly CurrencyIconProvider _currencyIcons;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _iconCts;
    private CancellationTokenSource? _currencyIconCts;
    private CancellationTokenSource? _uniqueCts;
    private CancellationTokenSource? _uniqueIconCts;
    private string? _iconUrl;
    private PriceCheckSession? _session;
    private bool _historyRecorded;
    private bool _startRequested;

    public PriceCheckOverlayViewModel(
        IPriceEstimationService pricing,
        IHistoryService history,
        ISettingsService settings,
        IClipboardService clipboard,
        IOverlayService overlay,
        ItemIconLoader icons,
        CurrencyIconProvider currencyIcons,
        TimeProvider time)
    {
        _pricing = pricing;
        _history = history;
        _settings = settings;
        _clipboard = clipboard;
        _overlay = overlay;
        _icons = icons;
        _currencyIcons = currencyIcons;
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

    /// <summary>Art of the estimate's currency; null shows <see cref="PriceCurrency"/> as text.</summary>
    [ObservableProperty]
    private Bitmap? _priceCurrencyIcon;

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

    /// <summary>Caveat for the reward estimate (e.g. corruption implicits need advanced copy).</summary>
    [ObservableProperty]
    private string? _rewardNote;

    [ObservableProperty]
    private string? _recommendation;

    [ObservableProperty]
    private IBrush _recommendationBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _canOpenTrade;

    /// <summary>The item is an unidentified unique: the overlay asks which unique it is before searching.</summary>
    [ObservableProperty]
    private bool _isChoosingUnique;

    /// <summary>The unique list is still being fetched (first use only; it is disk cached afterwards).</summary>
    [ObservableProperty]
    private bool _isLoadingUniques;

    /// <summary>Uniques the unidentified item may be, with their art as it loads.</summary>
    [ObservableProperty]
    private IReadOnlyList<UniqueChoiceViewModel> _uniqueChoices = [];

    public IBrush UniqueBrush { get; } = DangerPalette.Rarity(ItemRarity.Unique);

    public ObservableCollection<FilterItemViewModel> StatFilters { get; } = [];

    public ObservableCollection<FilterItemViewModel> PropertyFilters { get; } = [];

    public ObservableCollection<ListingItemViewModel> Listings { get; } = [];

    public ObservableCollection<KeyValueRow> RewardRows { get; } = [];

    public bool HasStatFilters => StatFilters.Count > 0;

    public bool HasPropertyFilters => PropertyFilters.Count > 0;

    public bool HasListings => Listings.Count > 0;

    /// <summary>Seller filter of the current check; each new check starts on "All". Switching re-runs the search at once.</summary>
    public bool OnlineSellersOnly
    {
        get => _session?.OnlineSellersOnly ?? false;
        set => SetOnlineSellersOnly(value);
    }

    /// <summary>Second half of the "All / Online" segmented toggle.</summary>
    public bool AllSellers
    {
        get => !OnlineSellersOnly;
        set => SetOnlineSellersOnly(!value);
    }

    private void SetOnlineSellersOnly(bool value)
    {
        if (_session is { } session && value != session.OnlineSellersOnly)
        {
            session.OnlineSellersOnly = value;
            if (!session.UsesBulkPricing)
            {
                // Same path as an explicit refresh; a previously seen status is served from the query cache.
                _debounce.Stop();
                IsSearchPending = false;
                _ = RunAsync();
            }
        }

        // Posted, not raised inline: also re-checks a segment that was clicked while already checked (a ToggleButton
        // un-checks itself), and a notification raised inside the binding's own write would be ignored.
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(OnlineSellersOnly));
            OnPropertyChanged(nameof(AllSellers));
        });
    }

    public void Load(PriceCheckSession session)
    {
        CancelUniqueLookup();
        _startRequested = false;
        IsLoadingUniques = false;
        UniqueChoices = [];
        IsChoosingUnique = false;
        var lookupUniques = false;

        if (_pricing.NeedsUniqueChoice(session))
        {
            var candidates = _pricing.PeekUniqueCandidates(session);
            if (candidates is { Count: 1 })
            {
                // Only one unique on this base: nothing to ask.
                session = _pricing.ChooseUnique(session, candidates[0]);
            }
            else if (candidates is null)
            {
                // Unique list not loaded yet (first run or startup still warming it): ask once it arrives.
                IsChoosingUnique = true;
                IsLoadingUniques = true;
                lookupUniques = true;
            }
            else if (candidates.Count > 1)
            {
                IsChoosingUnique = true;
                ShowUniqueChoices(session, candidates);
            }

            // No known unique on this base: search any unique of the base.
        }

        LoadSession(session);
        if (lookupUniques)
        {
            // After LoadSession: the lookup compares its session with the current one when it completes.
            _uniqueCts = new CancellationTokenSource();
            _ = LookupUniquesAsync(session, _uniqueCts.Token);
        }
    }

    private void LoadSession(PriceCheckSession session)
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

        CancelCurrencyIcons();
        Listings.Clear();
        OnPropertyChanged(nameof(HasListings));
        HasPrice = false;
        PriceCurrencyIcon = null;
        Error = null;
        PriceNote = null;
        CanOpenTrade = !session.UsesBulkPricing;
        OnPropertyChanged(nameof(OnlineSellersOnly));
        OnPropertyChanged(nameof(AllSellers));
        ApplyRewards(_pricing.EstimateRewardsLocal(session, null), null);
    }

    public void Start()
    {
        _startRequested = true;
        if (_session is null)
        {
            return;
        }

        if (IsChoosingUnique)
        {
            PriceDetail = string.Empty; // the search starts once a unique is picked
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
        CancelUniqueLookup();
        CancelCurrencyIcons();
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

    private async Task LookupUniquesAsync(PriceCheckSession session, CancellationToken token)
    {
        try
        {
            var candidates = await _pricing.GetUniqueCandidatesAsync(session, token);
            if (token.IsCancellationRequested || !ReferenceEquals(_session, session))
            {
                return;
            }

            IsLoadingUniques = false;
            switch (candidates.Count)
            {
                case 0:
                    // Unknown base or no data: price it as any unique of the base.
                    IsChoosingUnique = false;
                    if (_startRequested)
                    {
                        Start();
                    }

                    break;
                case 1:
                    SelectUnique(candidates[0], userChoice: false);
                    break;
                default:
                    ShowUniqueChoices(session, candidates);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // another item was opened or the overlay closed
        }
    }

    [RelayCommand]
    private void ChooseUnique(UniqueChoiceViewModel? choice)
    {
        if (choice is not null && IsChoosingUnique && _session is not null)
        {
            SelectUnique(choice.Name, userChoice: true, choice.IconUrl);
        }
    }

    private void ShowUniqueChoices(PriceCheckSession session, IReadOnlyList<string> names)
    {
        var choices = new UniqueChoiceViewModel[names.Count];
        for (var i = 0; i < choices.Length; i++)
        {
            choices[i] = new UniqueChoiceViewModel(names[i]);
        }

        UniqueChoices = choices;
        CancelUniqueIcons();
        _uniqueIconCts = new CancellationTokenSource();
        _ = LoadUniqueIconsAsync(session, choices, _uniqueIconCts.Token);
    }

    /// <summary>
    /// Fills in the picker art: decoded bitmaps already in memory are applied synchronously (no flicker), the rest load
    /// in parallel through the shared icon cache. The list is usable meanwhile; rows without art keep an empty slot.
    /// </summary>
    private async Task LoadUniqueIconsAsync(PriceCheckSession session, UniqueChoiceViewModel[] choices, CancellationToken token)
    {
        IReadOnlyDictionary<string, string> urls;
        try
        {
            urls = await _pricing.GetUniqueIconUrlsAsync(session, token);
        }
        catch (OperationCanceledException)
        {
            return; // another item was opened, a unique was picked or the overlay closed
        }

        if (token.IsCancellationRequested || urls.Count == 0)
        {
            return;
        }

        foreach (var choice in choices)
        {
            if (!urls.TryGetValue(choice.Name, out var url))
            {
                continue;
            }

            choice.IconUrl = url;
            if (_icons.TryGetCached(url) is { } cached)
            {
                choice.Icon = cached;
            }
            else
            {
                _ = LoadUniqueIconAsync(choice, url, token);
            }
        }
    }

    private async Task LoadUniqueIconAsync(UniqueChoiceViewModel choice, string url, CancellationToken token)
    {
        try
        {
            var bitmap = await _icons.LoadAsync(url, token);
            if (!token.IsCancellationRequested && bitmap is not null)
            {
                choice.Icon = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // the picker was closed; the shared download still completes and lands in the cache
        }
    }

    /// <summary>Prices the unidentified item as any unique of its base.</summary>
    [RelayCommand]
    private void SkipUniqueChoice()
    {
        if (!IsChoosingUnique || _session is null)
        {
            return;
        }

        CancelUniqueLookup();
        IsLoadingUniques = false;
        UniqueChoices = [];
        IsChoosingUnique = false;
        _ = RunAsync();
    }

    private void SelectUnique(string name, bool userChoice, string? iconUrl = null)
    {
        CancelUniqueLookup();
        IsLoadingUniques = false;
        UniqueChoices = [];
        IsChoosingUnique = false;
        LoadSession(_pricing.ChooseUnique(_session!, name, iconUrl));

        // A pick is an explicit request to price the item; an automatic single match follows the auto-search setting.
        if (userChoice)
        {
            _ = RunAsync();
        }
        else if (_startRequested)
        {
            Start();
        }
    }

    private void CancelUniqueLookup()
    {
        _uniqueCts?.Cancel();
        _uniqueCts?.Dispose();
        _uniqueCts = null;
        CancelUniqueIcons();
    }

    private void CancelUniqueIcons()
    {
        _uniqueIconCts?.Cancel();
        _uniqueIconCts?.Dispose();
        _uniqueIconCts = null;
    }

    private void CancelCurrencyIcons()
    {
        _currencyIconCts?.Cancel();
        _currencyIconCts?.Dispose();
        _currencyIconCts = null;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (IsChoosingUnique)
        {
            return Task.CompletedTask;
        }

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
        var game = _session!.Game;
        var useIcons = _settings.Current.Trade.ShowCurrencyIcons;
        CancelCurrencyIcons();
        CanOpenTrade = !_session!.UsesBulkPricing;
        ShowIcon(result.IconUrl);
        Error = result.Error;

        if (e.HasValue)
        {
            var low = e.RangeLow ?? e.Median!.Value;
            var high = e.RangeHigh ?? e.Median!.Value;
            PriceMain = Formatting.Range(low, high);
            PriceCurrency = Formatting.Plural(e.ValuationCurrency, high);
            PriceCurrencyIcon = useIcons ? _currencyIcons.TryGet(game, e.ValuationCurrency) : null;
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
            PriceCurrencyIcon = null;
            PriceDetail = e.MatchingListings > 0
                ? $"{e.MatchingListings} listings, but none could be valued"
                : result.Error is null ? "No matching listings — not enough market data" : string.Empty;
        }

        PriceNote = e.IsListingPrice
            ? $"Asking prices, not completed sales · {(result.FromCache ? "cached" : "updated")} {Formatting.Age(e.DataTimestamp, _time.GetUtcNow())}"
            : $"Exchange rate data · updated {Formatting.Age(e.DataTimestamp, _time.GetUtcNow())}";

        Listings.Clear();
        var missingIcons = false;
        foreach (var l in result.Listings.Take(_settings.Current.Trade.MaxListings))
        {
            var amount = Formatting.Amount(l.ListedPrice.Amount);
            var currency = l.ListedPrice.Currency;
            var row = new ListingItemViewModel(
                amount,
                currency,
                $"{amount} {Formatting.Plural(currency, l.ListedPrice.Amount)}",
                l.AccountName ?? "Unknown seller",
                l.SellerOnline == true,
                l.IndexedAt is { } at ? Formatting.Age(at, _time.GetUtcNow()) : string.Empty,
                l.ItemSummary);
            if (useIcons)
            {
                row.CurrencyIcon = _currencyIcons.TryGet(game, currency);
                missingIcons |= row.CurrencyIcon is null;
            }

            Listings.Add(row);
        }

        OnPropertyChanged(nameof(HasListings));
        if (useIcons && (missingIcons || (HasPrice && PriceCurrencyIcon is null)))
        {
            _currencyIconCts = new CancellationTokenSource();
            _ = LoadCurrencyIconsAsync(game, e.ValuationCurrency, _currencyIconCts.Token);
        }

        ApplyRewards(result.Rewards, e);
        RecordHistory(result);
    }

    /// <summary>
    /// First use only: fetches the art still missing after <see cref="Apply"/> and fills the rows in place. Each distinct
    /// currency is loaded once; rows keep their text until the icon arrives (or for good, if there is no art).
    /// </summary>
    private async Task LoadCurrencyIconsAsync(IGameModule game, string valuationCurrency, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HasPrice && PriceCurrencyIcon is null)
        {
            names.Add(valuationCurrency);
        }

        foreach (var row in Listings)
        {
            if (row.CurrencyIcon is null)
            {
                names.Add(row.CurrencyName);
            }
        }

        try
        {
            foreach (var name in names)
            {
                var bitmap = await _currencyIcons.GetAsync(game, name, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (bitmap is null)
                {
                    continue;
                }

                if (HasPrice && PriceCurrencyIcon is null && string.Equals(name, valuationCurrency, StringComparison.OrdinalIgnoreCase))
                {
                    PriceCurrencyIcon = bitmap;
                }

                foreach (var row in Listings)
                {
                    if (row.CurrencyIcon is null && string.Equals(row.CurrencyName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        row.CurrencyIcon = bitmap;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // a newer result or another item replaced these rows
        }
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

        RewardNote = null;
        if (rewards.Dust is { } dust)
        {
            if (primary is null && rewards.Salvage is null)
            {
                RewardTitle = "Disenchantment";
            }

            AddDustRows(dust, market);
            RewardNote = dust.Note;
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

    private void AddDustRows(DustEstimate dust, PriceEstimate? market)
    {
        RewardRows.Add(new KeyValueRow(DustEstimate.CurrencyName, Formatting.Count(dust.Amount)));
        if (dust.AmountAtMaxQuality != dust.Amount)
        {
            RewardRows.Add(new KeyValueRow($"At {DustEstimate.MaxCommonQuality}% quality", Formatting.Count(dust.AmountAtMaxQuality)));
        }

        // Dust is not traded, so "dust per currency" is how disenchanting is compared with selling.
        if (_settings.Current.Rewards.ShowComparison && market is { HasValue: true, Median: { } median } && median > 0)
        {
            RewardRows.Add(new KeyValueRow($"Dust per {market.ValuationCurrency}", Formatting.Count((long)Math.Round(dust.Amount / median))));
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
            DisenchantSummary = d is not null
                ? string.Join(", ", d.Lines.Select(l => $"{Formatting.Amount(l.Quantity)} {Formatting.Plural(l.Currency, l.Quantity)}"))
                : result.Rewards.Dust is { } dust ? $"{Formatting.Count(dust.Amount)} {DustEstimate.CurrencyName}" : null,
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
