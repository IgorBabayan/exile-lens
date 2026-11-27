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
}

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
    string MapTierKey);

public interface ITradeQueryBuilder
{
    IReadOnlyList<SearchFilter> CreateDefaultFilters(ParsedItem item, StatIndex? stats, TradeQueryOptions options);
    JsonObject Build(ParsedItem item, IReadOnlyList<SearchFilter> filters, TradeQueryOptions options);
}

public sealed record TradeQueryOptions(
    TradeFilterLayout Layout,
    double ModifierMinPercent = 90,
    bool EnableModifiersByDefault = true,
    bool OnlineOnly = true);

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
            for (var i = 0; i < item.Modifiers.Count; i++)
            {
                var mod = item.Modifiers[i];
                if (!stats.TryResolve(mod, preferLocal, out var stat))
                {
                    continue;
                }

                double? rolled = mod.PrimaryValue is { } v ? (stat.Negated ? -v : v) : null;
                double? min = rolled is { } r ? RoundMin(r, options.ModifierMinPercent) : null;
                filters.Add(new SearchFilter
                {
                    Key = $"stat:{i}",
                    Kind = FilterKind.Stat,
                    Label = mod.Text,
                    StatId = stat.Id,
                    Negated = stat.Negated,
                    RolledValue = rolled,
                    Min = min,
                    ModifierKind = mod.Kind,
                    Enabled = !isUnique && options.EnableModifiersByDefault && mod.Kind != ModifierKind.Enchant,
                });
            }
        }

        if (item.ItemLevel is { } ilvl && item.Category is ItemCategory.Equipment or ItemCategory.Jewel && !isUnique)
        {
            filters.Add(new SearchFilter { Key = "ilvl", Kind = FilterKind.ItemLevel, Label = "Item level", Min = ilvl, Enabled = false });
        }

        if (item.Quality is { } q && q > 0)
        {
            filters.Add(new SearchFilter
            {
                Key = "quality", Kind = FilterKind.Quality, Label = "Quality", Min = q,
                Enabled = item.Category == ItemCategory.Gem,
            });
        }

        if (item.Sockets.Count > 0)
        {
            filters.Add(new SearchFilter { Key = "sockets", Kind = FilterKind.Sockets, Label = "Sockets", Min = item.Sockets.Count, Enabled = item.Sockets.Count >= 5 });
            if (options.Layout.LinksKey is not null && item.Sockets.LargestLinkGroup >= 5)
            {
                filters.Add(new SearchFilter { Key = "links", Kind = FilterKind.Links, Label = "Links", Min = item.Sockets.LargestLinkGroup, Enabled = true });
            }
        }

        if (item.GemLevel is { } gl)
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

    public JsonObject Build(ParsedItem item, IReadOnlyList<SearchFilter> filters, TradeQueryOptions options)
    {
        var layout = options.Layout;
        var query = new JsonObject
        {
            ["status"] = new JsonObject { ["option"] = options.OnlineOnly ? "online" : "any" },
        };

        if (item.Rarity == ItemRarity.Unique && item.IsIdentified && item.Name is not null)
        {
            query["name"] = item.Name;
            query["type"] = item.BaseType;
        }
        else if (item.Rarity is ItemRarity.Normal or ItemRarity.Rare || item.Category is not ItemCategory.Equipment)
        {
            query["type"] = item.BaseType;
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
                    var sf = new JsonObject { ["id"] = f.StatId };
                    var range = Range(f.Min, f.Max);
                    if (range is not null)
                    {
                        sf["value"] = range;
                    }

                    statFilters.Add((JsonNode)sf);
                    break;
                case FilterKind.ItemLevel:
                    Group(layout.ItemLevelGroup)["ilvl"] = Range(f.Min, f.Max);
                    break;
                case FilterKind.Quality:
                    Group(layout.QualityGroup)["quality"] = Range(f.Min, f.Max);
                    break;
                case FilterKind.GemLevel:
                    Group(layout.GemLevelGroup)["gem_level"] = Range(f.Min, f.Max);
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
