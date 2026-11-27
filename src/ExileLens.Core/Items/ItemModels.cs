namespace ExileLens.Core.Items;

public enum GameId
{
    Poe1 = 1,
    Poe2 = 2,
}

public enum ItemRarity
{
    Unknown = 0,
    Normal,
    Magic,
    Rare,
    Unique,
    Currency,
    Gem,
    DivinationCard,
    Quest,
}

public enum ModifierKind
{
    Explicit,
    Implicit,
    Crafted,
    Fractured,
    Enchant,
    Rune,
    Desecrated,
    Scourge,
    Crucible,

    /// <summary>PoE 1 Foulborn (mutated) unique modifier: "(mutated)" in the simple copy, "{ Foulborn Unique Modifier }" in the advanced one.</summary>
    Mutated,
}

public enum AffixSlot
{
    Unknown,
    Prefix,
    Suffix,
    Implicit,
    Unique,
    Other,
}

/// <summary>Coarse item category used for routing (pricing strategy, disenchant/salvage rules, overlay choice).</summary>
public enum ItemCategory
{
    Unknown,
    Equipment,
    Jewel,
    Flask,
    Charm,
    Currency,
    Fragment,
    Map,
    Waystone,
    Gem,
    DivinationCard,
    Essence,
    Scarab,
    Rune,
    Omen,
    Tablet,
    SoulCore,
    Relic,
    Other,
}

/// <summary>One modifier line as it appears on the item.</summary>
public sealed record ItemModifier(
    string Text,
    string Pattern,
    double[] Values,
    ModifierKind Kind,
    int AffixGroup)
{
    /// <summary>First numeric value or null. Convenience for threshold rules.</summary>
    public double? PrimaryValue => Values.Length > 0 ? Values[0] : null;
}

/// <summary>An affix (prefix/suffix/implicit/etc). A hybrid affix owns more than one <see cref="ItemModifier"/> line.</summary>
public sealed record ItemAffix(AffixSlot Slot, string? Name, int? Tier, ModifierKind Kind, IReadOnlyList<int> LineIndexes);

public sealed record SocketInfo(int Count, int LargestLinkGroup, string Raw)
{
    public static readonly SocketInfo None = new(0, 0, string.Empty);

    /// <summary>True when one link group contains at least one red, green and blue socket (PoE 1 chromatic recipe).</summary>
    public bool HasLinkedRgb { get; init; }
}

public sealed class ParsedItem
{
    public required GameId Game { get; init; }
    public required string RawText { get; init; }
    public string? ItemClass { get; init; }
    public ItemRarity Rarity { get; init; }
    public ItemCategory Category { get; init; }
    public string? Name { get; init; }
    public required string BaseType { get; init; }
    public int? ItemLevel { get; init; }
    public int? Quality { get; init; }
    public int? GemLevel { get; init; }
    public int? MapTier { get; init; }
    public int? StackSize { get; init; }
    public int? MaxStackSize { get; init; }
    public int? RequiredLevel { get; init; }
    public bool IsCorrupted { get; init; }
    public bool IsIdentified { get; init; } = true;
    public bool IsMirrored { get; init; }
    public bool IsFractured { get; init; }
    public bool IsSynthesised { get; init; }

    /// <summary>PoE 1 Foulborn (mutated) unique: the clipboard name carries a "Foulborn " prefix the trade API does not know.</summary>
    public bool IsFoulborn { get; init; }
    public SocketInfo Sockets { get; init; } = SocketInfo.None;
    public IReadOnlyList<string> Influences { get; init; } = [];
    public IReadOnlyList<ItemModifier> Modifiers { get; init; } = [];
    public IReadOnlyList<ItemAffix> Affixes { get; init; } = [];

    /// <summary>Key/value properties (e.g. "Item Quantity" → "+80% (augmented)") preserved for display and map analysis.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>True when the clipboard text came from the advanced (Ctrl+Alt+C) copy with affix headers.</summary>
    public bool HasAdvancedAffixInfo { get; init; }

    /// <summary>Lines the parser did not understand. Kept for diagnostics; never fatal.</summary>
    public IReadOnlyList<string> UnparsedLines { get; init; } = [];

    public string DisplayName => Name ?? BaseType;

    /// <summary>Unique name as the official trade API expects it (Foulborn prefix removed; the variant is a separate filter).</summary>
    public string? TradeName => IsFoulborn && Name is { } n && n.Length > FoulbornPrefix.Length && n.StartsWith(FoulbornPrefix, StringComparison.Ordinal)
        ? n[FoulbornPrefix.Length..]
        : Name;

    public const string FoulbornPrefix = "Foulborn ";

    /// <summary>
    /// Copy with another unique name, e.g. the unique the user picked for an unidentified item. Everything parsed from
    /// the clipboard is kept; the parsed collections are immutable after parsing and are shared, not copied.
    /// </summary>
    public ParsedItem WithName(string? name) => new()
    {
        Game = Game,
        RawText = RawText,
        ItemClass = ItemClass,
        Rarity = Rarity,
        Category = Category,
        Name = name,
        BaseType = BaseType,
        ItemLevel = ItemLevel,
        Quality = Quality,
        GemLevel = GemLevel,
        MapTier = MapTier,
        StackSize = StackSize,
        MaxStackSize = MaxStackSize,
        RequiredLevel = RequiredLevel,
        IsCorrupted = IsCorrupted,
        IsIdentified = IsIdentified,
        IsMirrored = IsMirrored,
        IsFractured = IsFractured,
        IsSynthesised = IsSynthesised,
        IsFoulborn = Rarity == ItemRarity.Unique && name is not null && name.StartsWith(FoulbornPrefix, StringComparison.Ordinal),
        Sockets = Sockets,
        Influences = Influences,
        Modifiers = Modifiers,
        Affixes = Affixes,
        Properties = Properties,
        HasAdvancedAffixInfo = HasAdvancedAffixInfo,
        UnparsedLines = UnparsedLines,
    };

    public IEnumerable<ItemModifier> ModifiersOf(ModifierKind kind)
    {
        foreach (var m in Modifiers)
        {
            if (m.Kind == kind)
            {
                yield return m;
            }
        }
    }

    /// <summary>
    /// Number of explicit affixes (prefixes + suffixes). Exact when advanced copy info is present; otherwise
    /// estimated from explicit-like lines (hybrid affixes may be over-counted) — see <see cref="ExplicitAffixCountIsExact"/>.
    /// </summary>
    public int ExplicitAffixCount
    {
        get
        {
            if (HasAdvancedAffixInfo)
            {
                var n = 0;
                foreach (var a in Affixes)
                {
                    if (a.Slot is AffixSlot.Prefix or AffixSlot.Suffix)
                    {
                        n++;
                    }
                }

                return n;
            }

            var lines = 0;
            foreach (var m in Modifiers)
            {
                if (m.Kind is ModifierKind.Explicit or ModifierKind.Crafted or ModifierKind.Fractured or ModifierKind.Desecrated)
                {
                    lines++;
                }
            }

            return lines;
        }
    }

    public bool ExplicitAffixCountIsExact => HasAdvancedAffixInfo;

    public bool TryGetProperty(string key, out string value) =>
        Properties.TryGetValue(key, out value!);
}
