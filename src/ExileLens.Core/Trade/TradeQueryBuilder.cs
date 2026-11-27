using System.Text.Json.Nodes;
using ExileLens.Core.Items;

namespace ExileLens.Core.Trade;

public enum FilterKind
{
    Stat,
    ItemLevel,
    Quality,
    Sockets,
    Links,
    GemLevel,
    MapTier,
    Corrupted,
    Rarity,
    Name,

    /// <summary>PoE 1 "Imbued Gem" yes/no (misc filter), used when the exact built-in support cannot be resolved.</summary>
    Imbued,
}

/// <summary>A trade item that shares its base type with others and is told apart by a discriminator (transfigured gems).</summary>
public sealed record TradeTypeVariant(string Type, string Discriminator);

/// <summary>A user-toggleable search filter shown in the price-check overlay.</summary>
public sealed class SearchFilter
{
    public required string Key { get; init; }
    public required FilterKind Kind { get; init; }
    public required string Label { get; init; }
    public string? StatId { get; init; }
    public bool Negated { get; init; }
    public bool Enabled { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? RolledValue { get; init; }
    public bool? BoolValue { get; set; }
    public ModifierKind? ModifierKind { get; init; }

    /// <summary>Prefix/suffix of a stat filter; known only from the advanced copy (null otherwise).</summary>
    public AffixSlot? Slot { get; init; }

    /// <summary>Option-valued stat (e.g. an imbued gem's support): searched by option, not by a min/max range.</summary>
    public string? OptionId { get; init; }

    private List<SearchFilter>? _extra;

    /// <summary>
    /// Further lines of the same hybrid affix (e.g. "#% increased Evasion Rating" + "#% increased Stun and Block Recovery").
    /// They are one row in the overlay and are toggled with this filter, while each keeps its own minimum.
    /// </summary>
    public IReadOnlyList<SearchFilter> Extra => (IReadOnlyList<SearchFilter>?)_extra ?? [];

    internal void AddExtra(SearchFilter line) => (_extra ??= new List<SearchFilter>(2)).Add(line);
}

/// <summary>Per-game layout of the official trade query (filter group names differ between PoE 1 and PoE 2).</summary>
public sealed record TradeFilterLayout(
    string ItemLevelGroup,
    string QualityGroup,
    string CorruptedGroup,
    string GemLevelGroup,
    string SocketGroup,
    string SocketKey,
    string? LinksKey,
    string MapGroup,
    string MapTierKey,
    string? FoulbornGroup = null,
    string? SynthesisedGroup = null,
    string? ImbuedGroup = null);

public interface ITradeQueryBuilder
{
    IReadOnlyList<SearchFilter> CreateDefaultFilters(ParsedItem item, StatIndex? stats, TradeQueryOptions options);
    JsonObject Build(ParsedItem item, IReadOnlyList<SearchFilter> filters, TradeQueryOptions options);
}

public sealed record TradeQueryOptions(
    TradeFilterLayout Layout,
    double ModifierMinPercent = 90,
    bool EnableModifiersByDefault = true,
    TradeListingStatus Status = TradeListingStatus.InstantBuyout,
    string? PriceOption = null,
    TradeTypeVariant? TypeVariant = null);

public sealed class TradeQueryBuilder : ITradeQueryBuilder
{
    private static readonly HashSet<string> LocalPreferringClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Body Armours", "Helmets", "Gloves", "Boots", "Shields", "Bucklers", "Foci", "Quivers",
        "One Hand Swords", "Two Hand Swords", "One Hand Axes", "Two Hand Axes", "One Hand Maces", "Two Hand Maces",
        "Bows", "Crossbows", "Claws", "Daggers", "Wands", "Staves", "Warstaves", "Quarterstaves", "Sceptres", "Spears",
        "Flails", "Rune Daggers", "Thrusting One Hand Swords",
    };

    public IReadOnlyList<SearchFilter> CreateDefaultFilters(ParsedItem item, StatIndex? stats, TradeQueryOptions options)
    {
        var filters = new List<SearchFilter>(item.Modifiers.Count + 6);
        var isUnique = item.Rarity == ItemRarity.Unique;
        var preferLocal = item.ItemClass is not null && LocalPreferringClasses.Contains(item.ItemClass);

        if (stats is not null && item.IsIdentified && item.Category is ItemCategory.Equipment or ItemCategory.Jewel or ItemCategory.Flask or ItemCategory.Charm or ItemCategory.Tablet or ItemCategory.Relic)
        {
            var mods = item.Modifiers;
            var slots = item.HasAdvancedAffixInfo ? SlotsByLine(item) : null;

            // Advanced copy: lines under one affix header share an AffixGroup. A hybrid affix (two stats under one
            // header) becomes one row; the simple copy carries no grouping, so its lines stay separate.
            SearchFilter? affixHead = null;
            var affixHeadGroup = -1;
            for (var i = 0; i < mods.Count;)
            {
                var mod = mods[i];
                if (!stats.TryResolve(mods, i, preferLocal, out var stat, out var lineCount))
                {
                    i++;
                    continue;
                }

                // A multi-line modifier is one filter: joined label, value taken from whichever line carries it.
                var label = mod.Text;
                var primary = mod.PrimaryValue;
                if (lineCount > 1)
                {
                    label = JoinLines(mods, i, lineCount);
                    for (var j = i; primary is null && j < i + lineCount; j++)
                    {
                        primary = mods[j].PrimaryValue;
                    }
                }

                double? rolled = primary is { } v ? (stat.Negated ? -v : v) : null;

                // Unique rolls have narrow ranges and the price sits in the top of them: a percentage of the roll
                // (e.g. 202 for a perfect 225) matches far cheaper copies, so uniques search from the rolled value.
                double? min = rolled is { } r ? (isUnique ? r : RoundMin(r, options.ModifierMinPercent)) : null;
                var filter = new SearchFilter
                {
                    Key = $"stat:{i}",
                    Kind = FilterKind.Stat,
                    Label = label,
                    StatId = stat.Id,
                    Negated = stat.Negated,
                    RolledValue = rolled,
                    Min = min,
                    ModifierKind = mod.Kind,
                    Slot = slots is not null ? slots[i] : null,
                    Enabled = !isUnique && options.EnableModifiersByDefault && mod.Kind != ModifierKind.Enchant,
                };

                if (item.HasAdvancedAffixInfo && affixHead is not null && mod.AffixGroup == affixHeadGroup)
                {
                    affixHead.AddExtra(filter);
                }
                else
                {
                    filters.Add(filter);
                    affixHead = filter;
                    affixHeadGroup = mod.AffixGroup;
                }

                i += lineCount;
            }
        }

        if (item.ItemLevel is { } ilvl && item.Category is ItemCategory.Equipment or ItemCategory.Jewel && !isUnique)
        {
            filters.Add(new SearchFilter { Key = "ilvl", Kind = FilterKind.ItemLevel, Label = "Item level", Min = ilvl, Enabled = false });
        }
        else if (item.Category == ItemCategory.Corpse && item.ItemLevel is { } corpseLevel)
        {
            // A corpse's item level is its corpse level, which drives its value.
            filters.Add(new SearchFilter { Key = "ilvl", Kind = FilterKind.ItemLevel, Label = "Corpse level", Min = corpseLevel, Enabled = true });
        }

        var isGem = item.Category == ItemCategory.Gem;
        if (isGem)
        {
            // Gems always get both fields: level and quality drive the price far more than anything else on them.
            // A missing "Level:" line means level 1; a missing "Quality:" line means 0% (searched only when enabled).
            var gemLevel = item.GemLevel ?? 1;
            var gemQuality = item.Quality ?? 0;
            filters.Add(new SearchFilter { Key = "gem_level", Kind = FilterKind.GemLevel, Label = "Gem level", Min = gemLevel, Enabled = true });
            filters.Add(new SearchFilter { Key = "quality", Kind = FilterKind.Quality, Label = "Quality", Min = gemQuality, Enabled = gemQuality > 0 });
            AddImbuedFilter(filters, item, stats, options.Layout);
        }
        else if (item.Quality is { } q && q > 0)
        {
            filters.Add(new SearchFilter { Key = "quality", Kind = FilterKind.Quality, Label = "Quality", Min = q, Enabled = false });
        }

        if (item.Sockets.Count > 0)
        {
            filters.Add(new SearchFilter { Key = "sockets", Kind = FilterKind.Sockets, Label = "Sockets", Min = item.Sockets.Count, Enabled = item.Sockets.Count >= 5 });
            if (options.Layout.LinksKey is not null && item.Sockets.LargestLinkGroup >= 5)
            {
                filters.Add(new SearchFilter { Key = "links", Kind = FilterKind.Links, Label = "Links", Min = item.Sockets.LargestLinkGroup, Enabled = true });
            }
        }

        if (!isGem && item.GemLevel is { } gl)
        {
            filters.Add(new SearchFilter { Key = "gem_level", Kind = FilterKind.GemLevel, Label = "Gem level", Min = gl, Enabled = true });
        }

        if (item.MapTier is { } tier)
        {
            filters.Add(new SearchFilter { Key = "map_tier", Kind = FilterKind.MapTier, Label = "Tier", Min = tier, Max = tier, Enabled = true });
        }

        filters.Add(new SearchFilter
        {
            Key = "corrupted", Kind = FilterKind.Corrupted, Label = item.IsCorrupted ? "Corrupted" : "Not corrupted",
            BoolValue = item.IsCorrupted, Enabled = item.IsCorrupted || item.Category is ItemCategory.Gem or ItemCategory.Equipment && isUnique,
        });

        return filters;
    }

    /// <summary>
    /// PoE 1 imbued gems ("Supported by Level 1 Lifetap") are priced by their support: the exact support is searched
    /// when the trade stats know it, otherwise any imbued gem. A corrupted gem without one excludes imbued copies.
    /// </summary>
    private static void AddImbuedFilter(List<SearchFilter> filters, ParsedItem item, StatIndex? stats, TradeFilterLayout layout)
    {
        if (item.ImbuedSupport is { } support)
        {
            if (stats is not null && stats.TryResolveImbuedSupport(support, out var option))
            {
                filters.Add(new SearchFilter
                {
                    Key = "imbued_support", Kind = FilterKind.Stat, Label = $"Supported by {support}",
                    StatId = option.StatId, OptionId = option.OptionId, Enabled = true,
                });
            }
            else if (layout.ImbuedGroup is not null)
            {
                filters.Add(new SearchFilter { Key = "imbued", Kind = FilterKind.Imbued, Label = $"Imbued ({support})", BoolValue = true, Enabled = true });
            }
        }
        else if (item.IsCorrupted && layout.ImbuedGroup is not null)
        {
            filters.Add(new SearchFilter { Key = "imbued", Kind = FilterKind.Imbued, Label = "Not imbued", BoolValue = false, Enabled = true });
        }
    }

    /// <summary>Prefix/suffix of every modifier line, from the advanced-copy affix headers (one array, no per-line lookups).</summary>
    private static AffixSlot?[] SlotsByLine(ParsedItem item)
    {
        var slots = new AffixSlot?[item.Modifiers.Count];
        foreach (var affix in item.Affixes)
        {
            if (affix.Slot is not (AffixSlot.Prefix or AffixSlot.Suffix))
            {
                continue;
            }

            foreach (var line in affix.LineIndexes)
            {
                if ((uint)line < (uint)slots.Length)
                {
                    slots[line] = affix.Slot;
                }
            }
        }

        return slots;
    }

    public JsonObject Build(ParsedItem item, IReadOnlyList<SearchFilter> filters, TradeQueryOptions options)
    {
        var layout = options.Layout;
        var query = new JsonObject
        {
            ["status"] = new JsonObject { ["option"] = TradeListingStatuses.ApiId(options.Status) },
        };

        // An unidentified unique has no name in its copy; it carries one only once the user picked which unique it is.
        if (item.Rarity == ItemRarity.Unique && item.TradeName is { } tradeName)
        {
            query["name"] = tradeName;
            query["type"] = item.BaseType;
        }
        else if (item.Rarity is ItemRarity.Normal or ItemRarity.Rare or ItemRarity.Unique || item.Category is not ItemCategory.Equipment)
        {
            query["type"] = item.BaseType;
        }

        // Transfigured gems ("Arc of Oscillating") are unknown as a type: the trade API wants base + discriminator.
        if (item.Category == ItemCategory.Gem && options.TypeVariant is { } variant)
        {
            query["type"] = new JsonObject { ["option"] = variant.Type, ["discriminator"] = variant.Discriminator };
        }

        var groups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonObject Group(string name)
        {
            if (!groups.TryGetValue(name, out var g))
            {
                g = new JsonObject();
                groups[name] = g;
            }

            return g;
        }

        if (item.Category is ItemCategory.Equipment or ItemCategory.Jewel or ItemCategory.Flask or ItemCategory.Charm)
        {
            Group("type_filters")["rarity"] = new JsonObject { ["option"] = item.Rarity == ItemRarity.Unique ? "unique" : "nonunique" };
        }

        // Foulborn and regular copies of a unique share a name but not a price: always pin the variant.
        // The live PoE 1 trade data lists the Foulborn filter as misc_filters "mutated" (the old "foulborn_item" is ignored).
        if (layout.FoulbornGroup is { } foulbornGroup && item.Rarity == ItemRarity.Unique && item.Name is not null)
        {
            Group(foulbornGroup)["mutated"] = new JsonObject { ["option"] = item.IsFoulborn ? "true" : "false" };
        }

        if (layout.SynthesisedGroup is { } synthesisedGroup && item.IsSynthesised)
        {
            Group(synthesisedGroup)["synthesised_item"] = new JsonObject { ["option"] = "true" };
        }

        if (options.PriceOption is { } priceOption)
        {
            Group("trade_filters")["price"] = new JsonObject { ["option"] = priceOption };
        }

        var statFilters = new JsonArray();
        foreach (var f in filters)
        {
            if (!f.Enabled)
            {
                continue;
            }

            switch (f.Kind)
            {
                case FilterKind.Stat when f.StatId is not null:
                    // JsonNode-typed Add: the generic Add<T> is not trim/AOT safe.
                    statFilters.Add((JsonNode)StatNode(f));
                    foreach (var line in f.Extra)
                    {
                        if (line.StatId is not null)
                        {
                            statFilters.Add((JsonNode)StatNode(line));
                        }
                    }

                    break;
                case FilterKind.Imbued when layout.ImbuedGroup is { } imbuedGroup:
                    Group(imbuedGroup)["gem_imbued"] = new JsonObject { ["option"] = f.BoolValue == true ? "true" : "false" };
                    break;
                case FilterKind.ItemLevel:
                    Group(layout.ItemLevelGroup)["ilvl"] = Range(f.Min, f.Max);
                    break;
                case FilterKind.Quality when Range(f.Min, f.Max) is { } quality:
                    Group(layout.QualityGroup)["quality"] = quality;
                    break;
                case FilterKind.GemLevel when Range(f.Min, f.Max) is { } gemLevel:
                    Group(layout.GemLevelGroup)["gem_level"] = gemLevel;
                    break;
                case FilterKind.Sockets:
                    Group(layout.SocketGroup)[layout.SocketKey] = Range(f.Min, f.Max);
                    break;
                case FilterKind.Links when layout.LinksKey is not null:
                    Group(layout.SocketGroup)[layout.LinksKey] = Range(f.Min, f.Max);
                    break;
                case FilterKind.MapTier:
                    Group(layout.MapGroup)[layout.MapTierKey] = Range(f.Min, f.Max);
                    break;
                case FilterKind.Corrupted:
                    Group(layout.CorruptedGroup)["corrupted"] = new JsonObject { ["option"] = f.BoolValue == true ? "true" : "false" };
                    break;
            }
        }

        query["stats"] = new JsonArray(new JsonObject { ["type"] = "and", ["filters"] = statFilters });

        if (groups.Count > 0)
        {
            var filterRoot = new JsonObject();
            foreach (var (name, g) in groups)
            {
                filterRoot[name] = new JsonObject { ["filters"] = g };
            }

            query["filters"] = filterRoot;
        }

        return new JsonObject
        {
            ["query"] = query,
            ["sort"] = new JsonObject { ["price"] = "asc" },
        };
    }

    private static JsonObject StatNode(SearchFilter f)
    {
        var node = new JsonObject { ["id"] = f.StatId };
        if (f.OptionId is { } optionId)
        {
            node["value"] = new JsonObject
            {
                ["option"] = long.TryParse(optionId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                    ? JsonValue.Create(n)
                    : JsonValue.Create(optionId),
            };
        }
        else if (Range(f.Min, f.Max) is { } range)
        {
            node["value"] = range;
        }

        return node;
    }

    private static string JoinLines(IReadOnlyList<ItemModifier> mods, int start, int count)
    {
        var parts = new string[count];
        for (var j = 0; j < count; j++)
        {
            parts[j] = mods[start + j].Text;
        }

        return string.Join('\n', parts);
    }

    private static JsonObject? Range(double? min, double? max)
    {
        if (min is null && max is null)
        {
            return null;
        }

        var o = new JsonObject();
        if (min is { } a)
        {
            o["min"] = a;
        }

        if (max is { } b)
        {
            o["max"] = b;
        }

        return o;
    }

    internal static double RoundMin(double rolled, double percent)
    {
        var raw = rolled * percent / 100d;
        // Keep decimals for small fractional stats (e.g. 1.5% leech), otherwise floor to an integer.
        return Math.Abs(rolled) < 10 && rolled % 1 != 0
            ? Math.Round(raw, 1, MidpointRounding.ToZero)
            : Math.Floor(raw);
    }
}
