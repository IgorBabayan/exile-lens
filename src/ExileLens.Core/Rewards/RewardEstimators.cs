using ExileLens.Core.Economy;
using ExileLens.Core.Items;

namespace ExileLens.Core.Rewards;

public enum RewardKind
{
    Disenchant,
    Salvage,
    Vendor,
}

public sealed record RewardLine(string Currency, decimal Quantity, decimal? MinQuantity, decimal? MaxQuantity, decimal? Value);

public sealed record RewardEstimate(
    RewardKind Kind,
    string RuleId,
    IReadOnlyList<RewardLine> Lines,
    string? ValuationCurrency,
    decimal? TotalValue,
    bool IsExact,
    string? Note)
{
    public bool HasValue => TotalValue is not null;
}

public interface IDisenchantmentEstimator
{
    RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency);
}

public interface ISalvageEstimator
{
    RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency);
}

public interface IVendorValueEstimator
{
    RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency);
}

/// <summary>Values currency rewards, falling back to "full currency / stack size" for unpriced shards.</summary>
public static class RewardValuation
{
    public static decimal? UnitValue(string currency, RewardRuleSet rules, CurrencyRates? rates, string valuationCurrency)
    {
        if (rates is null || rates.IsEmpty)
        {
            return null;
        }

        if (rates.TryConvert(1m, currency, valuationCurrency, out var direct))
        {
            return direct;
        }

        foreach (var unit in rules.Units)
        {
            if (string.Equals(unit.Name, currency, StringComparison.OrdinalIgnoreCase) && unit.FullCurrency is { } full && unit.StackToFull > 0
                && rates.TryConvert(1m, full, valuationCurrency, out var fullValue))
            {
                return fullValue / unit.StackToFull;
            }
        }

        return null;
    }

    public static RewardEstimate Build(RewardKind kind, string ruleId, IEnumerable<(string Currency, decimal Qty, decimal? Min, decimal? Max)> rewards,
        RewardRuleSet rules, CurrencyRates? rates, string valuationCurrency, bool exact, string? note)
    {
        var lines = new List<RewardLine>();
        decimal? total = 0m;
        foreach (var (currency, qty, min, max) in rewards)
        {
            var unit = UnitValue(currency, rules, rates, valuationCurrency);
            decimal? value = unit is { } u ? u * qty : null;
            if (value is null)
            {
                total = null;
            }
            else if (total is not null)
            {
                total += value;
            }

            lines.Add(new RewardLine(currency, qty, min, max, value));
        }

        if (lines.Count == 0)
        {
            total = null;
        }

        return new RewardEstimate(kind, ruleId, lines, total is null ? null : valuationCurrency, total, exact, note);
    }
}

/// <summary>PoE 2 disenchanting (vendor "Disenchant" → currency shards). First matching rule wins.</summary>
public sealed class DisenchantmentEstimator(RewardRuleSet rules) : IDisenchantmentEstimator
{
    public RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency)
    {
        // A skipped rule whose affix clause relied on an estimate also makes the outcome uncertain.
        var estimated = false;
        foreach (var rule in rules.Disenchant)
        {
            var matched = rule.When.Matches(item, out var usedEstimate);
            estimated |= usedEstimate;
            if (!matched)
            {
                continue;
            }

            var note = estimated ? "Affix count estimated from modifier lines; use advanced item copy for an exact count." : null;
            if (!item.IsIdentified && (item.Rarity is ItemRarity.Rare or ItemRarity.Unique))
            {
                note = "Unidentified — affix-dependent bonuses cannot be determined.";
            }

            return RewardValuation.Build(RewardKind.Disenchant, rule.Id,
                rule.Rewards.Select(r => (r.Currency, r.Quantity, (decimal?)null, (decimal?)null)),
                rules, rates, valuationCurrency, !estimated, note);
        }

        return null;
    }
}

/// <summary>PoE 2 salvage bench: sockets → Artificer's Shards, quality → quality currency. Separate from disenchanting.</summary>
public sealed class SalvageEstimator(RewardRuleSet rules) : ISalvageEstimator
{
    public RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency)
    {
        var s = rules.Salvage;
        if (s is null || s.ExcludedCategories.Contains(item.Category)
            || (item.ItemClass is not null && s.ExcludedItemClasses.Contains(item.ItemClass, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        var rewards = new List<(string, decimal, decimal?, decimal?)>(2);
        var exact = true;

        if (item.Sockets.Count > 0 && !string.IsNullOrEmpty(s.SocketCurrency))
        {
            rewards.Add((s.SocketCurrency, item.Sockets.Count * s.PerSocket, null, null));
        }

        var quality = item.Quality ?? 0;
        if (quality > 0 && s.QualityStep > 0 && item.ItemClass is not null)
        {
            var rule = s.Quality.FirstOrDefault(q => q.ItemClasses.Contains(item.ItemClass, StringComparer.OrdinalIgnoreCase));
            if (rule is not null)
            {
                var guaranteed = quality / s.QualityStep;
                var remainder = quality % s.QualityStep;
                // Remainder grants a chance at one more unit; model expected value as remainder/step.
                var expected = guaranteed + ((decimal)remainder / s.QualityStep);
                var max = guaranteed + (remainder > 0 ? 1 : 0);
                if (max > 0)
                {
                    rewards.Add((rule.Currency, expected, guaranteed, max));
                    exact &= remainder == 0;
                }
            }
        }

        if (rewards.Count == 0)
        {
            return null;
        }

        return RewardValuation.Build(RewardKind.Salvage, "salvage", rewards, rules, rates, valuationCurrency, exact,
            exact ? null : "Quality between breakpoints gives a chance at one extra unit; shown as expected value.");
    }
}

/// <summary>PoE 1 single-item vendor recipes (e.g. 6 sockets → 7 Jeweller's Orbs). First matching rule wins.</summary>
public sealed class VendorValueEstimator(RewardRuleSet rules) : IVendorValueEstimator
{
    public RewardEstimate? Estimate(ParsedItem item, CurrencyRates? rates, string valuationCurrency)
    {
        foreach (var rule in rules.Vendor)
        {
            if (rule.When.Matches(item, out _))
            {
                return RewardValuation.Build(RewardKind.Vendor, rule.Id,
                    rule.Rewards.Select(r => (r.Currency, r.Quantity, (decimal?)null, (decimal?)null)),
                    rules, rates, valuationCurrency, true, rule.Description);
            }
        }

        return null;
    }
}
