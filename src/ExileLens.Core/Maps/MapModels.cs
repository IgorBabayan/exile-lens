using ExileLens.Core.Items;

namespace ExileLens.Core.Maps;

/// <summary>User-assigned danger classification. Numeric order is NOT the display order; use <see cref="DangerLevelExtensions.SortRank"/>.</summary>
public enum DangerLevel
{
    Unclassified = 0,
    Safe = 1,
    Warning = 2,
    Dangerous = 3,
}

public static class DangerLevelExtensions
{
    /// <summary>Display order: Dangerous, Warning, Unclassified, Safe.</summary>
    public static int SortRank(this DangerLevel level) => level switch
    {
        DangerLevel.Dangerous => 0,
        DangerLevel.Warning => 1,
        DangerLevel.Unclassified => 2,
        _ => 3,
    };

    /// <summary>Next level for single-click cycling in the UI: Unclassified → Safe → Warning → Dangerous → Unclassified.</summary>
    public static DangerLevel Next(this DangerLevel level) => level switch
    {
        DangerLevel.Unclassified => DangerLevel.Safe,
        DangerLevel.Safe => DangerLevel.Warning,
        DangerLevel.Warning => DangerLevel.Dangerous,
        _ => DangerLevel.Unclassified,
    };
}

/// <summary>A known map/waystone modifier with a stable identifier.</summary>
public sealed record MapModifierDefinition(string Id, GameId Game, string Pattern, string Category)
{
    /// <summary>Human readable text — the pattern with '#' placeholders.</summary>
    public string Description => Pattern;
}

public sealed class ValueThreshold
{
    /// <summary>Inclusive lower bound on the modifier's absolute primary value; null = unbounded.</summary>
    public double? Min { get; set; }

    /// <summary>Inclusive upper bound on the modifier's absolute primary value; null = unbounded.</summary>
    public double? Max { get; set; }

    public DangerLevel Level { get; set; }

    public bool Matches(double value)
    {
        var v = Math.Abs(value);
        return (Min is null || v >= Min.Value) && (Max is null || v <= Max.Value);
    }
}

public sealed class ModifierRule
{
    public string ModifierId { get; set; } = string.Empty;
    public DangerLevel Level { get; set; }
    public List<ValueThreshold> Thresholds { get; set; } = [];
    public string? Note { get; set; }

    public DangerLevel Evaluate(double? value)
    {
        if (value is { } v)
        {
            foreach (var t in Thresholds)
            {
                if (t.Matches(v))
                {
                    return t.Level;
                }
            }
        }

        return Level;
    }
}

/// <summary>Escalates the overall map level when all listed modifiers appear together.</summary>
public sealed class CombinationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> ModifierIds { get; set; } = [];
    public DangerLevel Level { get; set; } = DangerLevel.Dangerous;
}

public sealed class ModifierProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public GameId Game { get; set; } = GameId.Poe2;
    public Dictionary<string, ModifierRule> Rules { get; set; } = new(StringComparer.Ordinal);
    public List<CombinationRule> Combinations { get; set; } = [];

    /// <summary>When set, this many (or more) warning modifiers on one map escalate it to dangerous.</summary>
    public int? EscalateWarningsAt { get; set; }

    public ModifierProfile Clone(string newName)
    {
        var copy = new ModifierProfile
        {
            Name = newName,
            Game = Game,
            EscalateWarningsAt = EscalateWarningsAt,
        };
        foreach (var (key, rule) in Rules)
        {
            copy.Rules[key] = new ModifierRule
            {
                ModifierId = rule.ModifierId,
                Level = rule.Level,
                Note = rule.Note,
                Thresholds = rule.Thresholds.Select(t => new ValueThreshold { Min = t.Min, Max = t.Max, Level = t.Level }).ToList(),
            };
        }

        foreach (var c in Combinations)
        {
            copy.Combinations.Add(new CombinationRule { Name = c.Name, Level = c.Level, ModifierIds = [.. c.ModifierIds] });
        }

        return copy;
    }
}

public sealed record AnalyzedMapModifier(ItemModifier Modifier, string ModifierId, bool IsKnown, DangerLevel Level, string? Note);

public sealed record TriggeredCombination(string Name, DangerLevel Level, IReadOnlyList<string> ModifierIds);

public sealed record MapAnalysisResult(
    ParsedItem Item,
    string ProfileName,
    DangerLevel Overall,
    IReadOnlyList<AnalyzedMapModifier> Modifiers,
    IReadOnlyList<TriggeredCombination> Combinations,
    bool ModifiersUnavailable,
    string? Note)
{
    public int SafeCount => Count(DangerLevel.Safe);
    public int WarningCount => Count(DangerLevel.Warning);
    public int DangerousCount => Count(DangerLevel.Dangerous);
    public int UnclassifiedCount => Count(DangerLevel.Unclassified);

    private int Count(DangerLevel level)
    {
        var n = 0;
        foreach (var m in Modifiers)
        {
            if (m.Level == level)
            {
                n++;
            }
        }

        return n;
    }
}
