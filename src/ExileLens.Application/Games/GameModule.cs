using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Rewards;
using ExileLens.Core.Trade;

namespace ExileLens.Application.Games;

/// <summary>Everything game-specific, supplied by ExileLens.Game.Poe1 / ExileLens.Game.Poe2.</summary>
public interface IGameModule
{
    GameId Game { get; }
    string DisplayName { get; }
    string ShortName { get; }

    /// <summary>Official trade API path segment, e.g. "trade" (PoE 1) or "trade2" (PoE 2).</summary>
    string TradeApiSegment { get; }

    /// <summary>Realm path segment inserted before the league on PoE 2 ("poe2"), or null.</summary>
    string? TradeRealmSegment { get; }

    /// <summary>Path of the human trade website, e.g. "trade" or "trade2".</summary>
    string TradeSitePath { get; }

    /// <summary>poe.ninja path segment: "poe1" or "poe2".</summary>
    string NinjaSegment { get; }

    TradeFilterLayout FilterLayout { get; }

    /// <summary>Primary valuation currency, e.g. "Exalted Orb" (PoE 2) or "Chaos Orb" (PoE 1).</summary>
    string DefaultValuationCurrency { get; }

    /// <summary>Maps trade listing currency ids (e.g. "exalted") to display names (e.g. "Exalted Orb").</summary>
    IReadOnlyDictionary<string, string> TradeCurrencyNames { get; }

    /// <summary>poe.ninja exchange categories queried for currency rates and bulk pricing.</summary>
    IReadOnlyList<string> NinjaExchangeTypes { get; }

    /// <summary>Item categories that are priced via aggregated bulk data rather than item search.</summary>
    IReadOnlySet<ItemCategory> BulkPricedCategories { get; }

    /// <summary>Categories that exist in this game (used to hide irrelevant UI).</summary>
    IReadOnlySet<ItemCategory> SupportedCategories { get; }

    RewardRuleSet RewardRules { get; }

    IReadOnlyList<MapModifierDefinition> MapModifiers { get; }

    /// <summary>True when the "item-destruction" reward is disenchanting (PoE 2) rather than vendor recipes (PoE 1).</summary>
    bool HasDisenchanting { get; }

    bool HasSalvage { get; }

    bool HasVendorRecipes { get; }

    /// <summary>PoE 1 unique → Thaumaturgic Dust table (Kingsmarch disenchanting); null when the game has no dust.</summary>
    UniqueDustTable? UniqueDust { get; }
}

public interface IGameRegistry
{
    IReadOnlyList<IGameModule> All { get; }
    IGameModule Get(GameId game);
}

public sealed class GameRegistry(IEnumerable<IGameModule> modules) : IGameRegistry
{
    private readonly Dictionary<GameId, IGameModule> _modules = modules.ToDictionary(m => m.Game);

    public IReadOnlyList<IGameModule> All => [.. _modules.Values.OrderBy(m => m.Game)];

    public IGameModule Get(GameId game) =>
        _modules.TryGetValue(game, out var m) ? m : throw new InvalidOperationException($"Game module {game} is not registered.");
}
