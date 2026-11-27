using System.Collections.Frozen;
using ExileLens.Application.Games;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;

namespace ExileLens.Game.Poe1;

public sealed class Poe1GameModule(IAppPaths paths) : IGameModule
{
    private readonly Lazy<RewardRuleSet> _rewards = new(() => EmbeddedGameData.LoadRewards(
        typeof(Poe1GameModule).Assembly, "ExileLens.Game.Poe1.Data.rewards.json", Path.Combine(paths.DataDirectory, "rules", "poe1", "rewards.json")));

    private readonly Lazy<IReadOnlyList<MapModifierDefinition>> _mapMods = new(() => EmbeddedGameData.LoadMapModifiers(
        typeof(Poe1GameModule).Assembly, "ExileLens.Game.Poe1.Data.map-modifiers.json", Path.Combine(paths.DataDirectory, "rules", "poe1", "map-modifiers.json")));

    public GameId Game => GameId.Poe1;
    public string DisplayName => "Path of Exile";
    public string ShortName => "PoE 1";
    public string TradeApiSegment => "trade";
    public string? TradeRealmSegment => null;
    public string TradeSitePath => "trade";
    public string NinjaSegment => "poe1";

    public TradeFilterLayout FilterLayout { get; } = new(
        ItemLevelGroup: "misc_filters",
        QualityGroup: "misc_filters",
        CorruptedGroup: "misc_filters",
        GemLevelGroup: "misc_filters",
        SocketGroup: "socket_filters",
        SocketKey: "sockets",
        LinksKey: "links",
        MapGroup: "map_filters",
        MapTierKey: "map_tier",
        FoulbornGroup: "misc_filters");

    public string DefaultValuationCurrency => "Chaos Orb";

    public IReadOnlyDictionary<string, string> TradeCurrencyNames { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chaos"] = "Chaos Orb",
        ["divine"] = "Divine Orb",
        ["exalted"] = "Exalted Orb",
        ["alch"] = "Orb of Alchemy",
        ["alt"] = "Orb of Alteration",
        ["fusing"] = "Orb of Fusing",
        ["jewellers"] = "Jeweller's Orb",
        ["chrome"] = "Chromatic Orb",
        ["chance"] = "Orb of Chance",
        ["regal"] = "Regal Orb",
        ["vaal"] = "Vaal Orb",
        ["gcp"] = "Gemcutter's Prism",
        ["mirror"] = "Mirror of Kalandra",
        ["annul"] = "Orb of Annulment",
        ["scour"] = "Orb of Scouring",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> NinjaExchangeTypes { get; } = ["Currency", "Fragment", "Scarab", "Essence", "Fossil", "Resonator", "Oil", "DivinationCard", "DeliriumOrb", "Omen", "Tattoo"];

    public IReadOnlySet<ItemCategory> BulkPricedCategories { get; } = new HashSet<ItemCategory>
    {
        ItemCategory.Currency, ItemCategory.Fragment, ItemCategory.Essence, ItemCategory.Scarab, ItemCategory.DivinationCard, ItemCategory.Omen,
    }.ToFrozenSet();

    public IReadOnlySet<ItemCategory> SupportedCategories { get; } = new HashSet<ItemCategory>
    {
        ItemCategory.Equipment, ItemCategory.Jewel, ItemCategory.Flask, ItemCategory.Currency, ItemCategory.Fragment, ItemCategory.Map,
        ItemCategory.Gem, ItemCategory.DivinationCard, ItemCategory.Essence, ItemCategory.Scarab, ItemCategory.Omen, ItemCategory.Relic,
    }.ToFrozenSet();

    public RewardRuleSet RewardRules => _rewards.Value;
    public IReadOnlyList<MapModifierDefinition> MapModifiers => _mapMods.Value;
    public bool HasDisenchanting => false;
    public bool HasSalvage => false;
    public bool HasVendorRecipes => true;
}
