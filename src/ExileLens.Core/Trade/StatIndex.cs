using System.Collections.Frozen;
using System.Text;
using ExileLens.Core.Items;

namespace ExileLens.Core.Trade;

/// <summary>
/// A trade stat definition as published by the official trade data endpoint. Multi-line stats keep their '\n'.
/// Option stats (e.g. imbued supports) are listed once per option: <see cref="OptionId"/> is the option's id and
/// <see cref="Text"/> the option's text.
/// </summary>
public sealed record TradeStat(string Id, string Text, string Type, string? OptionId = null);

public readonly record struct ResolvedStat(string Id, bool Negated);

/// <summary>An option-valued trade stat: searched as {"id": StatId, "value": {"option": OptionId}}.</summary>
public readonly record struct ResolvedOption(string StatId, string OptionId);

/// <summary>
/// Maps normalized modifier patterns to official trade stat ids, per modifier type
/// ("explicit", "implicit", "crafted", "fractured", "enchant", "rune", "desecrated", "pseudo").
/// Built once per dataset refresh; lookups are O(1) on frozen dictionaries.
/// </summary>
public sealed class StatIndex
{
    private const string LocalSuffix = " (local)";

    /// <summary>Longest multi-line stat considered when joining item lines (real stats span 2–3 lines).</summary>
    private const int MaxStatLines = 4;

    private readonly FrozenDictionary<string, string> _global;
    private readonly FrozenDictionary<string, string> _local;

    /// <summary>"type|first line" of every multi-line stat → its largest line count. Lets single-line mods skip joining.</summary>
    private readonly FrozenDictionary<string, int> _multiLineHeads;

    /// <summary>Imbued gem support name (case-insensitive) → option-valued trade stat.</summary>
    private readonly FrozenDictionary<string, ResolvedOption> _imbuedSupports;

    public const string ImbuedType = "imbued";

    public StatIndex(IEnumerable<TradeStat> stats)
    {
        var global = new Dictionary<string, string>(StringComparer.Ordinal);
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var heads = new Dictionary<string, int>(StringComparer.Ordinal);
        var imbued = new Dictionary<string, ResolvedOption>(StringComparer.OrdinalIgnoreCase);
        List<(string Key, string Id, bool IsLocal)>? lineFallbacks = null;
        var count = 0;
        foreach (var s in stats)
        {
            count++;
            if (s.OptionId is { } optionId)
            {
                if (s.Type == ImbuedType)
                {
                    imbued.TryAdd(SupportName(s.Text), new ResolvedOption(s.Id, optionId));
                }

                continue;
            }

            var text = s.Text;
            var isLocal = text.EndsWith(LocalSuffix, StringComparison.OrdinalIgnoreCase);
            if (isLocal)
            {
                text = text[..^LocalSuffix.Length];
            }

            var target = isLocal ? local : global;
            if (!text.Contains('\n'))
            {
                var pattern = ModifierText.Normalize(text);
                target.TryAdd(Key(s.Type, pattern), s.Id);

                // "... Supported by Level # X Support": index the bare gem name too, so both spellings resolve in O(1).
                if (StripSupportSuffix(pattern) is { } bare)
                {
                    (lineFallbacks ??= []).Add((Key(s.Type, bare), s.Id, isLocal));
                }

                continue;
            }

            // Multi-line stat (e.g. "Passives granting ... in Radius\nalso grant ..."): the item shows it as several
            // lines of one modifier, so it is indexed whole and matched against consecutive item lines.
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
            {
                continue;
            }

            var patterns = new string[lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                patterns[i] = ModifierText.Normalize(lines[i]);
            }

            if (lines.Length == 1)
            {
                target.TryAdd(Key(s.Type, patterns[0]), s.Id);
                continue;
            }

            target.TryAdd(Key(s.Type, string.Join('\n', patterns)), s.Id);
            if (lines.Length <= MaxStatLines)
            {
                var head = Key(s.Type, patterns[0]);
                heads[head] = Math.Max(heads.GetValueOrDefault(head), lines.Length);
            }

            lineFallbacks ??= [];
            foreach (var p in patterns)
            {
                lineFallbacks.Add((Key(s.Type, p), s.Id, isLocal));
            }
        }

        // Lone lines of multi-line stats are a last resort only: they must never shadow a real single-line stat.
        if (lineFallbacks is not null)
        {
            foreach (var (key, id, isLocal) in lineFallbacks)
            {
                (isLocal ? local : global).TryAdd(key, id);
            }
        }

        _global = global.ToFrozenDictionary(StringComparer.Ordinal);
        _local = local.ToFrozenDictionary(StringComparer.Ordinal);
        _multiLineHeads = heads.ToFrozenDictionary(StringComparer.Ordinal);
        _imbuedSupports = imbued.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Count = count;
    }

    public int Count { get; }

    /// <summary>Trade stat of an imbued gem's built-in support, by the support's name (e.g. "Lifetap").</summary>
    public bool TryResolveImbuedSupport(string supportName, out ResolvedOption option) =>
        _imbuedSupports.TryGetValue(supportName, out option);

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
        ModifierKind.Mutated => "mutated",
        _ => "explicit",
    };

    public bool TryResolve(ItemModifier modifier, bool preferLocal, out ResolvedStat stat)
    {
        var type = TypeFor(modifier.Kind);
        if (TryResolve(type, modifier.Pattern, preferLocal, out stat))
        {
            return true;
        }

        // Desecrated/fractured/mutated lines are often only listed as explicit in the datasets.
        return type != "explicit" && TryResolve("explicit", modifier.Pattern, preferLocal, out stat);
    }

    /// <summary>
    /// Resolves the modifier at <paramref name="start"/>, joining it with the following lines when together they form
    /// one multi-line trade stat. <paramref name="lineCount"/> is the number of item lines consumed (1 for normal stats).
    /// </summary>
    public bool TryResolve(IReadOnlyList<ItemModifier> modifiers, int start, bool preferLocal, out ResolvedStat stat, out int lineCount)
    {
        var first = modifiers[start];
        if (_multiLineHeads.Count > 0 && start + 1 < modifiers.Count)
        {
            var type = TypeFor(first.Kind);
            if (TryResolveMultiLine(modifiers, start, type, preferLocal, out stat, out lineCount)
                || (type != "explicit" && TryResolveMultiLine(modifiers, start, "explicit", preferLocal, out stat, out lineCount)))
            {
                return true;
            }
        }

        lineCount = 1;
        return TryResolve(first, preferLocal, out stat);
    }

    private bool TryResolveMultiLine(IReadOnlyList<ItemModifier> modifiers, int start, string type, bool preferLocal, out ResolvedStat stat, out int lineCount)
    {
        stat = default;
        lineCount = 0;
        var first = modifiers[start];
        if (!_multiLineHeads.TryGetValue(Key(type, first.Pattern), out var maxLines))
        {
            return false;
        }

        // Only lines of the same kind can belong to one modifier.
        var available = 1;
        while (available < maxLines && start + available < modifiers.Count && modifiers[start + available].Kind == first.Kind)
        {
            available++;
        }

        if (available < 2)
        {
            return false;
        }

        var sb = new StringBuilder(first.Pattern, 160);
        var joined = new string[available + 1];
        for (var n = 2; n <= available; n++)
        {
            sb.Append('\n').Append(modifiers[start + n - 1].Pattern);
            joined[n] = sb.ToString();
        }

        // Longest match wins so a 3-line stat is not cut short by a 2-line one with the same head.
        for (var n = available; n >= 2; n--)
        {
            if (TryResolve(type, joined[n], preferLocal, out stat))
            {
                lineCount = n;
                return true;
            }
        }

        return false;
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

        // The game's copy now names granted supports with their gem name ("... Level 20 Returning Projectiles Support"),
        // while trade stats keep the bare name ("... Level # Returning Projectiles"). Only tried on a miss.
        if (StripSupportSuffix(pattern) is { } bare && Lookup(Key(type, bare), preferLocal, out id))
        {
            stat = new ResolvedStat(id, false);
            return true;
        }

        stat = default;
        return false;
    }

    private const string SupportSuffix = " Support";

    /// <summary>"Socketed Gems are Supported by Level # X Support" → "... Level # X"; null for any other pattern.</summary>
    private static string? StripSupportSuffix(string pattern) =>
        pattern.EndsWith(SupportSuffix, StringComparison.OrdinalIgnoreCase)
        && pattern.Contains("Supported by Level #", StringComparison.OrdinalIgnoreCase)
            ? pattern[..^SupportSuffix.Length]
            : null;

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

    /// <summary>"Supported by Level 1 Lifetap" or "Lifetap" → "Lifetap".</summary>
    private static string SupportName(string text)
    {
        const string prefix = "Supported by Level ";
        var span = text.AsSpan().Trim();
        if (span.StartsWith(prefix, StringComparison.Ordinal))
        {
            span = span[prefix.Length..];
            var digits = 0;
            while (digits < span.Length && char.IsAsciiDigit(span[digits]))
            {
                digits++;
            }

            span = span[digits..];
        }

        return span.Trim().Trim('\u200B').ToString();
    }

    private static string Key(string type, string pattern) => string.Concat(type, "|", pattern.ToLowerInvariant());
}
