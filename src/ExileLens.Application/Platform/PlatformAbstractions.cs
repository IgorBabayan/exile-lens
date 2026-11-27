using ExileLens.Core.Items;

namespace ExileLens.Application.Platform;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>A key combination such as Ctrl+D. Keys are named with a small portable vocabulary (A-Z, 0-9, F1-F24, Escape, Space...).</summary>
public readonly record struct KeyChord(HotkeyModifiers Modifiers, string Key)
{
    public static readonly KeyChord None = new(HotkeyModifiers.None, string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "Not set";
        }

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Meta");
        }

        parts.Add(Key);
        return string.Join(" + ", parts);
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Control; break;
                case "ALT" or "OPTION": mods |= HotkeyModifiers.Alt; break;
                case "SHIFT": mods |= HotkeyModifiers.Shift; break;
                case "META" or "CMD" or "WIN" or "SUPER": mods |= HotkeyModifiers.Meta; break;
                default:
                    if (key is not null)
                    {
                        return false;
                    }

                    key = NormalizeKey(raw);
                    break;
            }
        }

        if (key is null || !IsSupportedKey(key))
        {
            return false;
        }

        chord = new KeyChord(mods, key);
        return true;
    }

    public static string NormalizeKey(string key) => key.Length == 1 ? key.ToUpperInvariant() : key switch
    {
        _ when key.Equals("esc", StringComparison.OrdinalIgnoreCase) => "Escape",
        _ => char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant(),
    };

    public static bool IsSupportedKey(string key)
    {
        if (key.Length == 1)
        {
            return char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]);
        }

        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f))
        {
            return f is >= 1 and <= 24;
        }

        return key is "Escape" or "Space" or "Tab" or "Enter" or "Backspace" or "Insert" or "Delete" or "Home" or "End" or "Pageup" or "Pagedown";
    }
}

public enum HotkeyAction
{
    PriceCheck,
    MapCheck,
    CloseOverlay,
}

public sealed class HotkeyPressedEventArgs(KeyChord chord) : EventArgs
{
    public KeyChord Chord { get; } = chord;
}

/// <summary>
/// Global shortcuts. Implementations must only consume (swallow) a key press when a supported game window
/// is in the foreground; otherwise the key passes through untouched.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }

    /// <summary>Human readable explanation when shortcuts are unavailable or limited on this platform/session.</summary>
    string? Limitation { get; }

    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Replaces all registrations. Returns an error per chord that could not be registered
    /// (e.g. already grabbed by another application).
    /// </summary>
    IReadOnlyDictionary<KeyChord, string> Register(IReadOnlyCollection<KeyChord> chords);

    void Unregister();
}

public interface IClipboardService
{
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);
    Task SetTextAsync(string? text, CancellationToken cancellationToken = default);
}

/// <summary>Keystrokes used to type into the game chat.</summary>
public enum ChatKeystroke
{
    /// <summary>Enter: opens the chat input, or sends its text when it is already open.</summary>
    Enter,

    /// <summary>Ctrl+A (Cmd+A on macOS): selects any text left in the chat input.</summary>
    SelectAll,

    /// <summary>Ctrl+V (Cmd+V on macOS): pastes the clipboard.</summary>
    Paste,
}

/// <summary>Sends exactly one copy keystroke to the focused game window. One user key press → one game action.</summary>
public interface IKeyboardInputService
{
    bool IsSupported { get; }
    string? Limitation { get; }
    Task SendCopyAsync(bool advanced, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one chat keystroke to the focused window. Modifiers still held from the triggering shortcut are
    /// released first so, for example, Enter is not read as Ctrl+Enter.
    /// </summary>
    void SendChatKeystroke(ChatKeystroke keystroke);
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(PixelPoint p) => p.X >= X && p.Y >= Y && p.X < X + Width && p.Y < Y + Height;
}

public readonly record struct PixelPoint(int X, int Y);

public sealed record GameWindowInfo(GameId Game, string Title, string? ProcessName, PixelRect Bounds, nint Handle);

public interface IGameWindowService
{
    /// <summary>Returns the game window if a supported game is the foreground window; otherwise null.</summary>
    GameWindowInfo? GetActiveGameWindow();

    /// <summary>Returns any running game window (foreground or not), used for the connection status.</summary>
    GameWindowInfo? FindGameWindow();

    PixelPoint? GetCursorPosition();

    /// <summary>Gives focus back to the game window (used after interactive overlays close).</summary>
    void FocusWindow(GameWindowInfo window);
}

public interface IAutoStartService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled, bool startMinimized);
}

/// <summary>Identifies a game window from its title/process name. Shared by all platform implementations.</summary>
public static class GameWindowMatcher
{
    public static GameId? Match(string? title, string? processName)
    {
        var p = processName ?? string.Empty;

        // Steam/Proton exposes the app id as the X11 WM_CLASS ("steam_app_<id>").
        if (p.Equals("steam_app_2694490", StringComparison.OrdinalIgnoreCase))
        {
            return GameId.Poe2;
        }

        if (p.Equals("steam_app_238960", StringComparison.OrdinalIgnoreCase))
        {
            return GameId.Poe1;
        }

        if (p.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
        {
            // PathOfExile2*.exe for PoE 2; PathOfExile*.exe (x64, Steam, KG, EGS) for PoE 1.
            return p.StartsWith("PathOfExile2", StringComparison.OrdinalIgnoreCase) || p.StartsWith("PathOfExile_2", StringComparison.OrdinalIgnoreCase)
                ? GameId.Poe2
                : GameId.Poe1;
        }

        var t = title?.Trim() ?? string.Empty;
        if (t.Equals("Path of Exile 2", StringComparison.Ordinal))
        {
            return GameId.Poe2;
        }

        if (t.Equals("Path of Exile", StringComparison.Ordinal))
        {
            return GameId.Poe1;
        }

        return null;
    }
}
