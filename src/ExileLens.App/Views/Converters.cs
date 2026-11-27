using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ExileLens.App.Views;

public static class Converters
{
    /// <summary>"#RRGGBB" → brush; invalid text renders transparent instead of throwing.</summary>
    public static readonly IValueConverter ColorToBrush =
        new FuncValueConverter<string?, IBrush>(hex => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);
}
