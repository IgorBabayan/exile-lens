using System.Collections.Frozen;

namespace ExileLens.Core.Economy;

/// <summary>
/// Immutable snapshot of league currency values expressed in one primary currency
/// (e.g. Exalted Orb for PoE 2, Chaos Orb for PoE 1). Lookups are by display name, case-insensitive,
/// and by trade id when known.
/// </summary>
public sealed class CurrencyRates
{
    private readonly FrozenDictionary<string, decimal> _values;

    public CurrencyRates(string primaryCurrency, IEnumerable<KeyValuePair<string, decimal>> valuesInPrimary, DateTimeOffset fetchedAt, string source)
    {
        PrimaryCurrency = primaryCurrency;
        FetchedAt = fetchedAt;
        Source = source;
        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in valuesInPrimary)
        {
            if (v > 0)
            {
                dict[k] = v;
            }
        }

        dict[primaryCurrency] = 1m;
        _values = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static CurrencyRates Empty(string primary) => new(primary, [], DateTimeOffset.MinValue, "none");

    public string PrimaryCurrency { get; }

    public DateTimeOffset FetchedAt { get; }

    public string Source { get; }

    public int Count => _values.Count;

    public bool IsEmpty => _values.Count <= 1;

    public bool TryGetValue(string currency, out decimal valueInPrimary) =>
        _values.TryGetValue(currency, out valueInPrimary);

    /// <summary>Converts an amount between two currencies via the primary currency.</summary>
    public bool TryConvert(decimal amount, string from, string to, out decimal result)
    {
        result = 0;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            result = amount;
            return true;
        }

        if (!_values.TryGetValue(from, out var fromValue) || !_values.TryGetValue(to, out var toValue) || toValue == 0)
        {
            return false;
        }

        result = amount * fromValue / toValue;
        return true;
    }

    public TimeSpan Age(DateTimeOffset now) => now - FetchedAt;
}
