using ExileLens.Core.Items;
using ExileLens.Core.Trade;

namespace ExileLens.Application.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum OverlayPlacement
{
    NearCursor,
    LeftOfGame,
    RightOfGame,
    CenterOfGame,
}

public enum GameSelection
{
    Auto,
    Poe1,
    Poe2,
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public TradeSettings Trade { get; set; } = new();
    public MapSettings Maps { get; set; } = new();
    public RewardSettings Rewards { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    public GameSettings Poe1 { get; set; } = GameSettings.CreateDefault(GameId.Poe1);
    public GameSettings Poe2 { get; set; } = GameSettings.CreateDefault(GameId.Poe2);

    public GameSettings For(GameId game) => game == GameId.Poe1 ? Poe1 : Poe2;
}

public sealed class GeneralSettings
{
    public GameSelection Game { get; set; } = GameSelection.Auto;
    public GameId PreferredGame { get; set; } = GameId.Poe2;
    public bool StartWithSystem { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool PriceCheckEnabled { get; set; } = true;
    public bool MapCheckEnabled { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Use the advanced item copy (Ctrl+Alt+C) so affix headers are available for exact affix counts.</summary>
    public bool UseAdvancedCopy { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string AccentColor { get; set; } = "#C96442";
    public double FontSize { get; set; } = 13;
    public double OverlayScale { get; set; } = 1.0;
    public double OverlayOpacity { get; set; } = 0.98;
    public bool AnimationsEnabled { get; set; } = true;
    public OverlayPlacement OverlayPlacement { get; set; } = OverlayPlacement.NearCursor;
    public bool CloseOverlayOnClickOutside { get; set; } = true;
    public bool CloseOverlayOnGameFocusLoss { get; set; }
}

public sealed class TradeSettings
{
    public int MaxListings { get; set; } = 20;
    public int CacheMinutes { get; set; } = 5;
    public bool AutoSearch { get; set; } = true;
    public decimal MinimumTradeValue { get; set; } = 1m;
    public double ModifierMinPercent { get; set; } = 90;
    public bool EnableModifiersByDefault { get; set; } = true;
    public TradeListingStatus ListingStatus { get; set; } = TradeListingStatus.InstantBuyout;

    /// <summary>Only sellers that are online right now ("All" keeps <see cref="ListingStatus"/> as is).</summary>
    public bool OnlineSellersOnly { get; set; }

    /// <summary>Buyout currency of the trade search (<see cref="TradePriceCurrency.Key"/>); "any" leaves it unrestricted.</summary>
    public string PriceCurrency { get; set; } = TradePriceCurrencies.AnyKey;
    public string MarketDataProvider { get; set; } = "poe.ninja";

    /// <summary>Optional base URL of a self-hosted caching proxy for poe.ninja (recommended by poe.ninja for desktop clients).</summary>
    public string? NinjaBaseUrl { get; set; }
}

public sealed class MapSettings
{
    public string SafeColor { get; set; } = "#4CAF76";
    public string WarningColor { get; set; } = "#D8AE46";
    public string DangerousColor { get; set; } = "#E05A5A";
    public string UnclassifiedColor { get; set; } = "#8B8B8B";
    public bool SortByDanger { get; set; } = true;
}

public sealed class RewardSettings
{
    public bool DisenchantEnabled { get; set; } = true;
    public bool SalvageEnabled { get; set; } = true;
    public bool ShowComparison { get; set; } = true;
    public bool ShowSuggestedAction { get; set; } = true;

    /// <summary>Valuation currency name; empty uses the game's default (Exalted Orb / Chaos Orb).</summary>
    public string? ValuationCurrency { get; set; }
}

public sealed class HistorySettings
{
    public int MaxPriceChecks { get; set; } = 200;
    public int MaxMapChecks { get; set; } = 200;
}

public sealed class ChatSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shared by both games. The list is replaced (never mutated in place) when commands are added or removed
    /// so a background save can enumerate it safely.
    /// </summary>
    public List<ChatCommand> Commands { get; set; } = CreateDefaultCommands();

    public static List<ChatCommand> CreateDefaultCommands() =>
    [
        new() { Text = "/heist", Hotkey = "F8" },
        new() { Text = "/menagerie", Hotkey = "F10" },
        new() { Text = "/delve", Hotkey = "F9" },
        new() { Text = "/sanctum", Hotkey = "F11" },
        new() { Text = "/kingsmarch", Hotkey = "F6" },
        new() { Text = "/monastery", Hotkey = "F7" },
    ];
}

/// <summary>A text pasted into the game chat when its shortcut is pressed.</summary>
public sealed class ChatCommand
{
    public string Text { get; set; } = string.Empty;
    public string Hotkey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Press Enter after pasting; otherwise the chat stays open with the text for editing.</summary>
    public bool Send { get; set; } = true;
}

public sealed class GameSettings
{
    public string? League { get; set; }
    public string? ActiveProfileId { get; set; }
    public string PriceCheckHotkey { get; set; } = "Ctrl+D";
    public string MapCheckHotkey { get; set; } = "Alt+D";
    public string CloseOverlayHotkey { get; set; } = "Escape";

    public static GameSettings CreateDefault(GameId game) => new();
}
