using ExileLens.Core.Items;

namespace ExileLens.Core.Maps;

public interface IMapModifierAnalyzer
{
    MapAnalysisResult Analyze(ParsedItem item, ModifierProfile profile, MapModifierCatalog catalog);
}

/// <summary>Pure, allocation-conscious map analysis. No I/O; safe to call on any thread.</summary>
public sealed class MapModifierAnalyzer : IMapModifierAnalyzer
{
    public static bool IsMapLike(ParsedItem item) => item.Category is ItemCategory.Map or ItemCategory.Waystone;

    public MapAnalysisResult Analyze(ParsedItem item, ModifierProfile profile, MapModifierCatalog catalog)
    {
        if (!IsMapLike(item))
        {
            return new MapAnalysisResult(item, profile.Name, DangerLevel.Unclassified, [], [], true,
                "This item is not a map or waystone.");
        }

        if (!item.IsIdentified)
        {
            return new MapAnalysisResult(item, profile.Name, DangerLevel.Unclassified, [], [], true,
                "Unidentified — modifiers are unknown.");
        }

        var analyzed = new List<AnalyzedMapModifier>(item.Modifiers.Count);
        var presentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mod in item.Modifiers)
        {
            if (mod.Kind is ModifierKind.Enchant or ModifierKind.Rune)
            {
                continue;
            }

            var known = catalog.ResolveId(mod.Pattern, out var id);
            presentIds.Add(id);
            var level = DangerLevel.Unclassified;
            string? note = null;
            if (profile.Rules.TryGetValue(id, out var rule))
            {
                level = rule.Evaluate(mod.PrimaryValue);
                note = rule.Note;
            }

            analyzed.Add(new AnalyzedMapModifier(mod, id, known, level, note));
        }

        // Stable sort by display rank; keeps original order inside a rank.
        var sorted = analyzed
            .Select((m, i) => (m, i))
            .OrderBy(x => x.m.Level.SortRank())
            .ThenBy(x => x.i)
            .Select(x => x.m)
            .ToList();

        var combos = new List<TriggeredCombination>();
        foreach (var c in profile.Combinations)
        {
            if (c.ModifierIds.Count >= 2 && c.ModifierIds.TrueForAll(presentIds.Contains))
            {
                combos.Add(new TriggeredCombination(c.Name, c.Level, c.ModifierIds));
            }
        }

        var warnings = 0;
        var hasDanger = false;
        var hasUnclassified = false;
        foreach (var m in sorted)
        {
            switch (m.Level)
            {
                case DangerLevel.Dangerous: hasDanger = true; break;
                case DangerLevel.Warning: warnings++; break;
                case DangerLevel.Unclassified: hasUnclassified = true; break;
            }
        }

        if (profile.EscalateWarningsAt is { } limit && limit > 0 && warnings >= limit)
        {
            combos.Add(new TriggeredCombination($"{warnings} warning modifiers together", DangerLevel.Dangerous, []));
        }

        foreach (var c in combos)
        {
            if (c.Level == DangerLevel.Dangerous)
            {
                hasDanger = true;
            }
            else if (c.Level == DangerLevel.Warning)
            {
                warnings++;
            }
        }

        DangerLevel overall;
        if (hasDanger)
        {
            overall = DangerLevel.Dangerous;
        }
        else if (warnings > 0)
        {
            overall = DangerLevel.Warning;
        }
        else if (hasUnclassified)
        {
            overall = DangerLevel.Unclassified;
        }
        else
        {
            overall = DangerLevel.Safe;
        }

        return new MapAnalysisResult(item, profile.Name, overall, sorted, combos, false, null);
    }
}
