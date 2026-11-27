using System.Collections.Frozen;
using ExileLens.Application.Games;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;

namespace ExileLens.Game.Poe2;

public sealed class Poe2GameModule(IAppPaths paths) : IGameModule
{
    private readonly Lazy<RewardRuleSet> _rewards = new(() => EmbeddedGameData.LoadRewards(
        typeof(Poe2GameModule).Assembly, "ExileLens.Game.Poe2.Data.rewards.json", Path.Combine(paths.DataDirectory, "rules", "poe2", "rewards.json")));

    private readonly Lazy<IReadOnlyList<MapModifierDefinition>> _mapMods = new(() => EmbeddedGameData.LoadMapModifiers(
        typeof(Poe2GameModule).Assembly, "ExileLens.Game.Poe2.Data.map-modifiers.json", Path.Combine(paths.DataDirectory, "rules", "poe2", "map-modifiers.json")));

    public GameId Game => GameId.Poe2;
    public string DisplayName => "Path of Exile 2";
    public string ShortName => "PoE 2";
    public string TradeApiSegment => "trade2";
    public string? TradeRealmSegment => "poe2";
    public string TradeSitePath => "trade2";
    public string NinjaSegment => "poe2";

    public TradeFilterLayout FilterLayout { get; } = new(
        ItemLevelGroup: "type_filters",
        QualityGroup: "type_filters",
        CorruptedGroup: "misc_filters",
        GemLevelGroup: "misc_filters",
        SocketGroup: "equipment_filters",
        SocketKey: "rune_sockets",
        LinksKey: null,
        MapGroup: "map_filters",
        MapTierKey: "map_tier");

    public string DefaultValuationCurrency => "Exalted Orb";

    public IReadOnlyDictionary<string, string> TradeCurrencyNames { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["exalted"] = "Exalted Orb",
        ["divine"] = "Divine Orb",
        ["chaos"] = "Chaos Orb",
        ["regal"] = "Regal Orb",
        ["alch"] = "Orb of Alchemy",
        ["annul"] = "Orb of Annulment",
        ["vaal"] = "Vaal Orb",
        ["mirror"] = "Mirror of Kalandra",
        ["transmute"] = "Orb of Transmutation",
        ["aug"] = "Orb of Augmentation",
        ["chance"] = "Orb of Chance",
        ["gcp"] = "Gemcutter's Prism",
        ["artificers"] = "Artificer's Orb",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> NinjaExchangeTypes { get; } = ["Currency", "Fragments", "Essences", "Runes", "SoulCores", "Ritual", "Abyss", "Expedition", "Delirium", "Breach", "UncutGems", "Idols"];

    public IReadOnlySet<ItemCategory> BulkPricedCategories { get; } = new HashSet<ItemCategory>
    {
        ItemCategory.Currency, ItemCategory.Fragment, ItemCategory.Essence, ItemCategory.Rune, ItemCategory.Omen, ItemCategory.SoulCore,
    }.ToFrozenSet();

    public IReadOnlySet<ItemCategory> SupportedCategories { get; } = new HashSet<ItemCategory>
    {
        ItemCategory.Equipment, ItemCategory.Jewel, ItemCategory.Flask, ItemCategory.Charm, ItemCategory.Currency, ItemCategory.Fragment,
        ItemCategory.Waystone, ItemCategory.Gem, ItemCategory.Essence, ItemCategory.Rune, ItemCategory.Omen, ItemCategory.Tablet,
        ItemCategory.SoulCore, ItemCategory.Relic,
    }.ToFrozenSet();

    public RewardRuleSet RewardRules => _rewards.Value;
    public IReadOnlyList<MapModifierDefinition> MapModifiers => _mapMods.Value;
    public bool HasDisenchanting => true;
    public bool HasSalvage => true;
    public bool HasVendorRecipes => false;
}
