using System.Collections.Frozen;
using ExileLens.Core.Items;

namespace ExileLens.Core.Trade;

/// <summary>A trade stat definition as published by the official trade data endpoint.</summary>
public sealed record TradeStat(string Id, string Text, string Type);

public readonly record struct ResolvedStat(string Id, bool Negated);

/// <summary>
/// Maps normalized modifier patterns to official trade stat ids, per modifier type
/// ("explicit", "implicit", "crafted", "fractured", "enchant", "rune", "desecrated", "pseudo").
/// Built once per dataset refresh; lookups are O(1) on frozen dictionaries.
/// </summary>
public sealed class StatIndex
{
    private const string LocalSuffix = " (local)";

    private readonly FrozenDictionary<string, string> _global;
    private readonly FrozenDictionary<string, string> _local;

    public StatIndex(IEnumerable<TradeStat> stats)
    {
        var global = new Dictionary<string, string>(StringComparer.Ordinal);
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var s in stats)
        {
            count++;
            var text = s.Text;
            var isLocal = text.EndsWith(LocalSuffix, StringComparison.OrdinalIgnoreCase);
            if (isLocal)
            {
                text = text[..^LocalSuffix.Length];
            }

            // Multi-line stats are published with '\n'; item lines are matched one at a time, so index the first line too.
            var key = Key(s.Type, ModifierText.Normalize(text));
            (isLocal ? local : global).TryAdd(key, s.Id);
        }

        _global = global.ToFrozenDictionary(StringComparer.Ordinal);
        _local = local.ToFrozenDictionary(StringComparer.Ordinal);
        Count = count;
    }

    public int Count { get; }

    public static string TypeFor(ModifierKind kind) => kind switch
    {
        ModifierKind.Implicit => "implicit",
        ModifierKind.Crafted => "crafted",
        ModifierKind.Fractured => "fractured",
        ModifierKind.Enchant => "enchant",
        ModifierKind.Rune => "rune",
        ModifierKind.Desecrated => "desecrated",
        ModifierKind.Scourge => "scourge",
        ModifierKind.Crucible => "crucible",
        _ => "explicit",
    };

    public bool TryResolve(ItemModifier modifier, bool preferLocal, out ResolvedStat stat)
    {
        var type = TypeFor(modifier.Kind);
        if (TryResolve(type, modifier.Pattern, preferLocal, out stat))
        {
            return true;
        }

        // Desecrated/fractured lines are often only listed as explicit in older datasets.
        return type != "explicit" && TryResolve("explicit", modifier.Pattern, preferLocal, out stat);
    }

    public bool TryResolve(string type, string pattern, bool preferLocal, out ResolvedStat stat)
    {
        if (Lookup(Key(type, pattern), preferLocal, out var id))
        {
            stat = new ResolvedStat(id, false);
            return true;
        }

        // "10% reduced X" is listed as "#% increased X" with a negative value.
        foreach (var (from, to) in Inversions)
        {
            if (pattern.Contains(from, StringComparison.Ordinal))
            {
                var swapped = pattern.Replace(from, to, StringComparison.Ordinal);
                if (Lookup(Key(type, swapped), preferLocal, out id))
                {
                    stat = new ResolvedStat(id, true);
                    return true;
                }
            }
        }

        stat = default;
        return false;
    }

    private static readonly (string From, string To)[] Inversions =
    [
        (" reduced ", " increased "),
        (" less ", " more "),
        (" increased ", " reduced "),
        (" more ", " less "),
    ];

    private bool Lookup(string key, bool preferLocal, out string id)
    {
        if (preferLocal && _local.TryGetValue(key, out id!))
        {
            return true;
        }

        if (_global.TryGetValue(key, out id!))
        {
            return true;
        }

        return _local.TryGetValue(key, out id!);
    }

    private static string Key(string type, string pattern) => string.Concat(type, "|", pattern.ToLowerInvariant());
}
