using System.Globalization;
using System.Text;

namespace ExileLens.Core.Items;

/// <summary>
/// Allocation-light helpers that turn a modifier line into a normalized pattern ("# to maximum Life")
/// plus its numeric values. Normalized patterns are the stable matching key used for trade stats and
/// map-modifier identifiers, so they deliberately ignore leading '+' signs, advanced roll ranges and
/// whitespace differences.
/// </summary>
public static class ModifierText
{
    private static readonly (string Suffix, ModifierKind Kind)[] KindSuffixes =
    [
        (" (implicit)", ModifierKind.Implicit),
        (" (crafted)", ModifierKind.Crafted),
        (" (fractured)", ModifierKind.Fractured),
        (" (enchant)", ModifierKind.Enchant),
        (" (rune)", ModifierKind.Rune),
        (" (desecrated)", ModifierKind.Desecrated),
        (" (scourge)", ModifierKind.Scourge),
        (" (crucible)", ModifierKind.Crucible),
    ];

    /// <summary>Strips a trailing kind marker such as " (implicit)" and reports the kind.</summary>
    public static ReadOnlySpan<char> StripKindSuffix(ReadOnlySpan<char> line, out ModifierKind kind, ModifierKind fallback = ModifierKind.Explicit)
    {
        line = line.TrimEnd();
        foreach (var (suffix, k) in KindSuffixes)
        {
            if (line.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                kind = k;
                return line[..^suffix.Length].TrimEnd();
            }
        }

        kind = fallback;
        return line;
    }

    /// <summary>
    /// Produces the normalized pattern and the values. "+85(80-89) to maximum Life" → ("# to maximum Life", [85]).
    /// </summary>
    public static string Normalize(ReadOnlySpan<char> text, out double[] values)
    {
        var sb = new StringBuilder(text.Length);
        Span<double> buffer = stackalloc double[8];
        var count = 0;
        List<double>? overflow = null;
        var lastWasSpace = true;

        for (var i = 0; i < text.Length;)
        {
            var c = text[i];

            if (IsNumberStart(text, i))
            {
                var start = i;
                var negative = false;
                if (c is '+' or '-')
                {
                    negative = c == '-';
                    i++;
                }

                var numStart = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.' || (text[i] == ',' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))))
                {
                    i++;
                }

                var numberSpan = text[numStart..i];
                double value = ParseNumber(numberSpan);
                if (negative)
                {
                    value = -value;
                }

                if (count < buffer.Length)
                {
                    buffer[count] = value;
                }
                else
                {
                    overflow ??= [];
                    overflow.Add(value);
                }

                count++;

                // Advanced copy appends the roll range right after the value: "85(80-89)".
                if (i < text.Length && text[i] == '(')
                {
                    var close = text[i..].IndexOf(')');
                    if (close > 0 && IsRange(text.Slice(i + 1, close - 1)))
                    {
                        i += close + 1;
                    }
                }

                // A negative value keeps its sign in the pattern (e.g. "-#% maximum Player Resistances"),
                // positive '+' is dropped so "+#" and "#" match.
                if (negative && start == numStart - 1)
                {
                    sb.Append('-');
                }

                sb.Append('#');
                lastWasSpace = false;
                continue;
            }

            // Trade stat texts use "+#"; item text uses "+85". Both normalize to "#".
            if (c == '+' && i + 1 < text.Length && text[i + 1] == '#')
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                i++;
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
            i++;
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        if (count == 0)
        {
            values = [];
        }
        else
        {
            values = new double[count];
            for (var j = 0; j < Math.Min(count, buffer.Length); j++)
            {
                values[j] = buffer[j];
            }

            if (overflow is not null)
            {
                for (var j = 0; j < overflow.Count; j++)
                {
                    values[buffer.Length + j] = overflow[j];
                }
            }
        }

        return sb.ToString();
    }

    public static string Normalize(string text) => Normalize(text.AsSpan(), out _);

    /// <summary>Removes advanced-copy roll ranges ("+85(80-89) to ..." → "+85 to ...") for display.</summary>
    public static string StripRanges(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('(') < 0)
        {
            return text.ToString();
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(' && i > 0 && char.IsAsciiDigit(text[i - 1]))
            {
                var close = text[i..].IndexOf(')');
                if (close > 0 && IsRange(text.Slice(i + 1, close - 1)))
                {
                    i += close;
                    continue;
                }
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static bool IsNumberStart(ReadOnlySpan<char> text, int i)
    {
        var c = text[i];
        if (char.IsAsciiDigit(c))
        {
            // Avoid treating digits glued to letters (e.g. "T2") as values only if previous char is a letter.
            return i == 0 || !char.IsAsciiLetter(text[i - 1]);
        }

        if ((c is '+' or '-') && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
        {
            return i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1]);
        }

        return false;
    }

    private static bool IsRange(ReadOnlySpan<char> inner)
    {
        if (inner.IsEmpty)
        {
            return false;
        }

        var sawDigit = false;
        foreach (var ch in inner)
        {
            if (char.IsAsciiDigit(ch))
            {
                sawDigit = true;
            }
            else if (ch is not ('-' or '.' or '–' or '—' or ' ' or '+'))
            {
                return false;
            }
        }

        return sawDigit;
    }

    private static double ParseNumber(ReadOnlySpan<char> span)
    {
        if (span.Contains(','))
        {
            Span<char> tmp = stackalloc char[span.Length];
            var n = 0;
            foreach (var ch in span)
            {
                if (ch != ',')
                {
                    tmp[n++] = ch;
                }
            }

            return double.TryParse(tmp[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out var v1) ? v1 : 0;
        }

        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
