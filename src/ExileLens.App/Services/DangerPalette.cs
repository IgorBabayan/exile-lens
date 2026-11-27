using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;

namespace ExileLens.App.Services;

/// <summary>
/// Resolves user-configured danger colors and rarity colors. In the light theme danger colors are darkened
/// so text stays readable on cream backgrounds (the defaults are tuned for dark surfaces).
/// </summary>
public sealed class DangerPalette(ISettingsService settings)
{
    private static bool IsDark => Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public IBrush Brush(DangerLevel level)
    {
        var m = settings.Current.Maps;
        var hex = level switch
        {
            DangerLevel.Safe => m.SafeColor,
            DangerLevel.Warning => m.WarningColor,
            DangerLevel.Dangerous => m.DangerousColor,
            _ => m.UnclassifiedColor,
        };

        var color = Color.TryParse(hex, out var c) ? c : Colors.Gray;
        if (!IsDark)
        {
            color = App.Lighten(color, -0.28);
        }

        return new SolidColorBrush(color);
    }

    public IBrush SoftBrush(DangerLevel level)
    {
        var solid = ((SolidColorBrush)Brush(level)).Color;
        return new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x2E : (byte)0x22, solid.R, solid.G, solid.B));
    }

    public static IBrush Rarity(ItemRarity rarity)
    {
        var key = rarity switch
        {
            ItemRarity.Magic => "RarityMagicBrush",
            ItemRarity.Rare => "RarityRareBrush",
            ItemRarity.Unique => "RarityUniqueBrush",
            ItemRarity.Gem => "RarityGemBrush",
            ItemRarity.Currency or ItemRarity.DivinationCard => "RarityCurrencyBrush",
            _ => "RarityNormalBrush",
        };
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
    }

    public static string Label(DangerLevel level) => level switch
    {
        DangerLevel.Safe => "Safe",
        DangerLevel.Warning => "Warning",
        DangerLevel.Dangerous => "Dangerous",
        _ => "Unclassified",
    };

    public static string IconKey(DangerLevel level) => level switch
    {
        DangerLevel.Safe => "IconCheck",
        DangerLevel.Warning => "IconWarning",
        DangerLevel.Dangerous => "IconShield",
        _ => "IconQuestion",
    };

    public static Geometry? Icon(DangerLevel level) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(IconKey(level), app.ActualThemeVariant, out var g) ? g as Geometry : null;
}
