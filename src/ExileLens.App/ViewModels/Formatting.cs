using System.Diagnostics;
using System.Globalization;
using ExileLens.Core.Economy;

namespace ExileLens.App.ViewModels;

internal static class Formatting
{
    public static string Amount(decimal value) => CurrencyAmount.Format(value);

    public static string Range(decimal low, decimal high) =>
        low == high || Amount(low) == Amount(high) ? Amount(low) : $"{Amount(low)}–{Amount(high)}";

    /// <summary>"Exalted Orb" → "Exalted Orbs", "Orb of Fusing" → "Orbs of Fusing", "Regal Shard" → "Regal Shards".</summary>
    public static string Plural(string name, decimal quantity)
    {
        if (quantity == 1m)
        {
            return name;
        }

        if (name.StartsWith("Orb of ", StringComparison.Ordinal))
        {
            return "Orbs of " + name["Orb of ".Length..];
        }

        foreach (var suffix in (string[])["Orb", "Shard", "Scrap", "Whetstone", "Etcher", "Bauble", "Prism", "Fragment", "Splinter"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name + "s";
            }
        }

        return name;
    }

    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        if (at == DateTimeOffset.MinValue)
        {
            return "unknown";
        }

        var age = now - at;
        return age.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)age.TotalMinutes} min ago",
            < 86400 => $"{(int)age.TotalHours} h ago",
            _ => $"{(int)age.TotalDays} d ago",
        };
    }

    public static string Confidence(EstimateConfidence c) => c switch
    {
        EstimateConfidence.High => "High confidence",
        EstimateConfidence.Medium => "Medium confidence",
        EstimateConfidence.Low => "Low confidence",
        _ => "No estimate",
    };

    public static bool TryParseDouble(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            value = v;
            return true;
        }

        return false;
    }

    public static string? Format(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture);

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
