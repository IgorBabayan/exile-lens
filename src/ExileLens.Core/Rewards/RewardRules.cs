using ExileLens.Core.Items;

namespace ExileLens.Core.Rewards;

/// <summary>
/// Versioned, data-driven reward rules for one game. Loaded from JSON shipped with the game module and
/// replaceable without code changes when a patch alters item-destruction rewards.
/// </summary>
public sealed class RewardRuleSet
{
    public int SchemaVersion { get; set; } = 1;
    public GameId Game { get; set; }

    /// <summary>Game patch the rules were verified against, e.g. "0.4".</summary>
    public string RulesVersion { get; set; } = string.Empty;

    /// <summary>Where the rules were verified (wiki page, patch notes).</summary>
    public List<string> Sources { get; set; } = [];

    public List<RewardRule> Disenchant { get; set; } = [];
    public SalvageRules? Salvage { get; set; }
    public List<RewardRule> Vendor { get; set; } = [];
    public List<CurrencyUnit> Units { get; set; } = [];
}

public sealed class RewardRule
{
    public string Id { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RuleCondition When { get; set; } = new();
    public List<RewardItem> Rewards { get; set; } = [];
}

public sealed class RuleCondition
{
    public List<ItemRarity>? Rarities { get; set; }
    public List<ItemCategory>? Categories { get; set; }
    public List<string>? ItemClasses { get; set; }
    public bool? Identified { get; set; }
    public bool? Corrupted { get; set; }
    public int? MinExplicitAffixes { get; set; }
    public int? MaxExplicitAffixes { get; set; }
    public int? MinSockets { get; set; }
    public int? MinLinks { get; set; }
    public bool? LinkedRgb { get; set; }
    public int? MinQuality { get; set; }

    /// <summary>Evaluates the condition. <paramref name="usesEstimatedAffixes"/> is set when an affix-count clause relied on an estimate.</summary>
    public bool Matches(ParsedItem item, out bool usesEstimatedAffixes)
    {
        usesEstimatedAffixes = false;
        if (Rarities is { Count: > 0 } && !Rarities.Contains(item.Rarity))
        {
            return false;
        }

        if (Categories is { Count: > 0 } && !Categories.Contains(item.Category))
        {
            return false;
        }

        if (ItemClasses is { Count: > 0 } && (item.ItemClass is null || !ItemClasses.Contains(item.ItemClass, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Identified is { } id && item.IsIdentified != id)
        {
            return false;
        }

        if (Corrupted is { } c && item.IsCorrupted != c)
        {
            return false;
        }

        if (MinExplicitAffixes is not null || MaxExplicitAffixes is not null)
        {
            if (!item.IsIdentified)
            {
                return false; // affix count is unknowable for unidentified items
            }

            var count = item.ExplicitAffixCount;
            usesEstimatedAffixes = !item.ExplicitAffixCountIsExact;
            if (MinExplicitAffixes is { } min && count < min)
            {
                return false;
            }

            if (MaxExplicitAffixes is { } max && count > max)
            {
                return false;
            }
        }

        if (MinSockets is { } ms && item.Sockets.Count < ms)
        {
            return false;
        }

        if (MinLinks is { } ml && item.Sockets.LargestLinkGroup < ml)
        {
            return false;
        }

        if (LinkedRgb is true && !item.Sockets.HasLinkedRgb)
        {
            return false;
        }

        if (MinQuality is { } q && (item.Quality ?? 0) < q)
        {
            return false;
        }

        return true;
    }
}

public sealed class RewardItem
{
    public string Currency { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
}

public sealed class SalvageRules
{
    public string SocketCurrency { get; set; } = string.Empty;
    public decimal PerSocket { get; set; } = 1;

    /// <summary>Quality per guaranteed quality-currency unit (e.g. 5 → one unit per 5% quality).</summary>
    public int QualityStep { get; set; } = 5;

    public List<QualitySalvageRule> Quality { get; set; } = [];
    public List<ItemCategory> ExcludedCategories { get; set; } = [];
    public List<string> ExcludedItemClasses { get; set; } = [];
}

public sealed class QualitySalvageRule
{
    public List<string> ItemClasses { get; set; } = [];
    public string Currency { get; set; } = string.Empty;
}

/// <summary>Describes how a fractional currency (shard) relates to its whole currency.</summary>
public sealed class CurrencyUnit
{
    public string Name { get; set; } = string.Empty;
    public string? FullCurrency { get; set; }
    public int StackToFull { get; set; } = 1;
}
