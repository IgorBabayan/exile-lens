using System.Collections.Frozen;
using ExileLens.Core.Items;

namespace ExileLens.Core.Rewards;

/// <summary>Base dust value of one PoE 1 unique (game data) and the influences it always carries (e.g. Starforge).</summary>
public sealed class UniqueDustEntry
{
    public double Dust { get; set; }
    public int Influences { get; set; }
}

/// <summary>Versioned unique → base dust table, shipped as embedded JSON and replaceable without code changes.</summary>
public sealed class UniqueDustDocument
{
    public int SchemaVersion { get; set; } = 1;
    public GameId Game { get; set; }
    public string RulesVersion { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = [];
    public string? Formula { get; set; }
    public Dictionary<string, UniqueDustEntry> Uniques { get; set; } = [];
}

/// <summary>Immutable, case-insensitive lookup built once per game (frozen for the hot price-check path).</summary>
public sealed class UniqueDustTable
{
    private readonly FrozenDictionary<string, UniqueDustEntry> _entries;

    public UniqueDustTable(UniqueDustDocument document)
    {
        RulesVersion = document.RulesVersion;
        _entries = document.Uniques.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public string RulesVersion { get; }

    public int Count => _entries.Count;

    public bool TryGet(string name, out UniqueDustEntry entry) => _entries.TryGetValue(name, out entry!);
}

/// <summary>Thaumaturgic Dust from disenchanting one unique at Rog (Kingsmarch / town disenchanting window).</summary>
public sealed record DustEstimate(
    long Amount,
    long AmountAtMaxQuality,
    int Quality,
    int EffectiveItemLevel,
    int Influences,
    int CorruptionImplicits,
    bool IsExact,
    string? Note)
{
    public const string CurrencyName = "Thaumaturgic Dust";

    /// <summary>Quality reachable with ordinary quality currency; the dust preview for "what if I quality it first".</summary>
    public const int MaxCommonQuality = 20;
}

public static class DustCalculator
{
    public const int MinEffectiveItemLevel = 65;
    public const int MaxEffectiveItemLevel = 84;

    private const string CorruptionImplicitHeader = "Corruption Implicit Modifier";

    /// <summary>
    /// base × 125 × (20 − (84 − ilvl)) × (1 + (2·quality + 50·influences + 50·corruption implicits) / 100), item level clamped to 65–84.
    /// </summary>
    public static long Compute(double baseDust, int itemLevel, int quality, int influences, int corruptionImplicits)
    {
        if (baseDust <= 0)
        {
            return 0;
        }

        var ilvl = Math.Clamp(itemLevel, MinEffectiveItemLevel, MaxEffectiveItemLevel);
        var increased = (Math.Max(quality, 0) * 2) + (Math.Max(influences, 0) * 50) + (Math.Max(corruptionImplicits, 0) * 50);
        var multiplier = 125d * (20 - (MaxEffectiveItemLevel - ilvl)) * ((increased + 100) / 100d);
        return (long)Math.Round(baseDust * multiplier, MidpointRounding.AwayFromZero);
    }

    /// <summary>Conqueror / Shaper / Elder influences; Eldritch implicits (Searing Exarch, Eater of Worlds) do not count.</summary>
    public static int CountInfluences(ParsedItem item)
    {
        var n = 0;
        foreach (var influence in item.Influences)
        {
            if (influence is not ("Searing Exarch" or "Eater of Worlds"))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Corruption implicits are only labelled in the advanced (Ctrl+Alt+C) copy.</summary>
    public static int CountCorruptionImplicits(ParsedItem item)
    {
        if (!item.IsCorrupted || !item.HasAdvancedAffixInfo)
        {
            return 0;
        }

        var text = item.RawText.AsSpan();
        var count = 0;
        int i;
        while ((i = text.IndexOf(CorruptionImplicitHeader, StringComparison.Ordinal)) >= 0)
        {
            count++;
            text = text[(i + CorruptionImplicitHeader.Length)..];
        }

        return count;
    }
}

/// <summary>PoE 1 unique disenchanting. Returns null for anything that is not a unique listed in the dust table.</summary>
public sealed class DustEstimator(UniqueDustTable table)
{
    public DustEstimate? Estimate(ParsedItem item)
    {
        if (item.Rarity != ItemRarity.Unique || !item.IsIdentified || item.TradeName is not { } name || !table.TryGet(name, out var entry))
        {
            return null;
        }

        var quality = item.Quality ?? 0;
        var influences = Math.Max(DustCalculator.CountInfluences(item), entry.Influences);
        var corruption = DustCalculator.CountCorruptionImplicits(item);
        var ilvl = item.ItemLevel ?? DustCalculator.MaxEffectiveItemLevel;
        var amount = DustCalculator.Compute(entry.Dust, ilvl, quality, influences, corruption);
        var atMax = quality >= DustEstimate.MaxCommonQuality
            ? amount
            : DustCalculator.Compute(entry.Dust, ilvl, DustEstimate.MaxCommonQuality, influences, corruption);

        string? note = null;
        var exact = true;
        if (item.ItemLevel is null)
        {
            note = $"Item level unknown — assumed {DustCalculator.MaxEffectiveItemLevel}.";
            exact = false;
        }
        else if (item.IsCorrupted && !item.HasAdvancedAffixInfo)
        {
            note = "Corruption implicits (+50% each) are only detected with advanced item copy.";
            exact = false;
        }

        return new DustEstimate(amount, atMax, quality, Math.Clamp(ilvl, DustCalculator.MinEffectiveItemLevel, DustCalculator.MaxEffectiveItemLevel),
            influences, corruption, exact, note);
    }
}
