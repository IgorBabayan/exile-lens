using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ExileLens.Platform.Windows;

/// <summary>Shared key-name ↔ virtual-key mapping.</summary>
internal static class VirtualKeys
{
    public static int? FromName(string key)
    {
        if (key.Length == 1)
        {
            var c = key[0];
            return char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) ? c : null;
        }

        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1;
        }

        return key switch
        {
            "Escape" => 0x1B,
            "Space" => 0x20,
            "Tab" => 0x09,
            "Enter" => 0x0D,
            "Backspace" => 0x08,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            "Home" => 0x24,
            "End" => 0x23,
            "Pageup" => 0x21,
            "Pagedown" => 0x22,
            _ => null,
        };
    }
}

/// <summary>
/// Low-level keyboard hook on a dedicated thread. A registered chord is swallowed only when a supported game
/// window is in the foreground; otherwise it passes through to the focused application untouched.
/// Injected events (including our own copy keystroke) are ignored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHotkeyService : IGlobalHotkeyService
{
    private static WindowsHotkeyService? _instance;
    private readonly WindowsGameWindowService _windows;
    private readonly ILogger<WindowsHotkeyService> _logger;
    private volatile Dictionary<(int Vk, HotkeyModifiers Mods), KeyChord> _bindings = [];
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;

    public WindowsHotkeyService(WindowsGameWindowService windows, ILogger<WindowsHotkeyService> logger)
    {
        _windows = windows;
        _logger = logger;
    }

    public bool IsSupported => true;

    public string? Limitation => null;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public IReadOnlyDictionary<KeyChord, string> Register(IReadOnlyCollection<KeyChord> chords)
    {
        var errors = new Dictionary<KeyChord, string>();
        var map = new Dictionary<(int, HotkeyModifiers), KeyChord>();
        foreach (var chord in chords)
        {
            if (VirtualKeys.FromName(chord.Key) is { } vk)
            {
                map[(vk, chord.Modifiers)] = chord;
            }
            else
            {
                errors[chord] = $"Key '{chord.Key}' is not supported.";
            }
        }

        _bindings = map;
        EnsureHook();
        return errors;
    }

    public void Unregister() => _bindings = [];

    private void EnsureHook()
    {
        if (_thread is not null)
        {
            return;
        }

        _instance = this;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandleW(null), 0);
            if (_hook == 0)
            {
                _logger.LogError("SetWindowsHookEx failed: {Error}", Marshal.GetLastPInvokeError());
            }

            ready.Set();
            while (Native.GetMessageW(out _, 0, 0, 0) > 0)
            {
            }

            if (_hook != 0)
            {
                Native.UnhookWindowsHookEx(_hook);
            }
        })
        {
            IsBackground = true,
            Name = "ExileLens keyboard hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nuint wParam, nint lParam)
    {
        var self = _instance;
        if (nCode >= 0 && self is not null && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var data = (Native.KBDLLHOOKSTRUCT*)lParam;
            if ((data->flags & Native.LLKHF_INJECTED) == 0 && self.TryHandle((int)data->vkCode))
            {
                return 1; // swallow
            }
        }

        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryHandle(int vk)
    {
        var bindings = _bindings;
        if (bindings.Count == 0)
        {
            return false;
        }

        var mods = HotkeyModifiers.None;
        if (Native.IsDown(Native.VK_CONTROL))
        {
            mods |= HotkeyModifiers.Control;
        }

        if (Native.IsDown(Native.VK_MENU))
        {
            mods |= HotkeyModifiers.Alt;
        }

        if (Native.IsDown(Native.VK_SHIFT))
        {
            mods |= HotkeyModifiers.Shift;
        }

        if (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
        {
            mods |= HotkeyModifiers.Meta;
        }

        if (!bindings.TryGetValue((vk, mods), out var chord))
        {
            return false;
        }

        // Escape-like chords must reach the overlay even when it has focus; capture chords need the game focused.
        if (_windows.GetActiveGameWindow() is null && !string.Equals(chord.Key, "Escape", StringComparison.Ordinal))
        {
            return false;
        }

        // Raise asynchronously: the hook must return within the system's LowLevelHooksTimeout.
        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Self.HotkeyPressed?.Invoke(s.Self, new HotkeyPressedEventArgs(s.Chord)), (Self: this, Chord: chord), preferLocal: false);
        return true;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        _instance = null;
    }
}

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsKeyboardInputService : IKeyboardInputService
{
    private const ushort VkC = 0x43;
    private const ushort VkA = 0x41;
    private const ushort VkV = 0x56;
    private const ushort VkReturn = 0x0D;

    public bool IsSupported => true;

    public string? Limitation => null;

    public Task SendCopyAsync(bool advanced, CancellationToken cancellationToken = default)
    {
        Span<Native.INPUT> inputs = stackalloc Native.INPUT[6];
        var n = 0;
        var ctrlHeld = Native.IsDown(Native.VK_CONTROL);
        var altHeld = Native.IsDown(Native.VK_MENU);

        if (!ctrlHeld)
        {
            inputs[n++] = Key(Native.VK_CONTROL, false);
        }

        if (advanced && !altHeld)
        {
            inputs[n++] = Key(Native.VK_MENU, false);
        }

        inputs[n++] = Key(VkC, false);
        inputs[n++] = Key(VkC, true);

        if (advanced && !altHeld)
        {
            inputs[n++] = Key(Native.VK_MENU, true);
        }

        if (!ctrlHeld)
        {
            inputs[n++] = Key(Native.VK_CONTROL, true);
        }

        fixed (Native.INPUT* p = inputs)
        {
            Native.SendInput((uint)n, p, sizeof(Native.INPUT));
        }

        return Task.CompletedTask;
    }

    public void SendChatKeystroke(ChatKeystroke keystroke)
    {
        // Up to 5 modifier releases + Ctrl down, key down, key up, Ctrl up.
        Span<Native.INPUT> inputs = stackalloc Native.INPUT[9];
        var n = 0;
        foreach (var vk in HeldModifiers)
        {
            if (Native.IsDown(vk))
            {
                inputs[n++] = Key(vk, true);
            }
        }

        var (key, ctrl) = keystroke switch
        {
            ChatKeystroke.SelectAll => (VkA, true),
            ChatKeystroke.Paste => (VkV, true),
            _ => (VkReturn, false),
        };

        if (ctrl)
        {
            inputs[n++] = Key(Native.VK_CONTROL, false);
        }

        inputs[n++] = Key(key, false);
        inputs[n++] = Key(key, true);
        if (ctrl)
        {
            inputs[n++] = Key(Native.VK_CONTROL, true);
        }

        fixed (Native.INPUT* p = inputs)
        {
            Native.SendInput((uint)n, p, sizeof(Native.INPUT));
        }
    }

    private static ReadOnlySpan<int> HeldModifiers => [Native.VK_CONTROL, Native.VK_MENU, Native.VK_SHIFT, Native.VK_LWIN, Native.VK_RWIN];

    private static Native.INPUT Key(int vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } },
    };
}

[SupportedOSPlatform("windows")]
public sealed class WindowsGameWindowService : IGameWindowService
{
    private sealed record CacheEntry(nint Hwnd, long At, GameWindowInfo? Info);

    private volatile CacheEntry? _cache;

    public GameWindowInfo? GetActiveGameWindow()
    {
        var hwnd = Native.GetForegroundWindow();
        return hwnd == 0 ? null : Describe(hwnd);
    }

    public GameWindowInfo? FindGameWindow()
    {
        var hwnd = Native.FindWindowW("POEWindowClass", null);
        return hwnd == 0 ? null : Describe(hwnd);
    }

    public PixelPoint? GetCursorPosition() => Native.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

    public void FocusWindow(GameWindowInfo window) => Native.SetForegroundWindow(window.Handle);

    private GameWindowInfo? Describe(nint hwnd)
    {
        // The hook calls this on every candidate key press; cache identity checks per window for 1 s.
        var now = Environment.TickCount64;
        var cache = _cache;
        if (cache is not null && cache.Hwnd == hwnd && now - cache.At < 1000)
        {
            return cache.Info is null ? null : cache.Info with { Bounds = Bounds(hwnd) };
        }

        var title = Native.GetWindowTitle(hwnd);
        var process = Native.GetProcessName(hwnd);
        var game = GameWindowMatcher.Match(title, process);
        var info = game is { } g ? new GameWindowInfo(g, title, process, Bounds(hwnd), hwnd) : null;
        _cache = new CacheEntry(hwnd, now, info);
        return info;
    }

    private static PixelRect Bounds(nint hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Native.RECT>()) != 0)
        {
            Native.GetWindowRect(hwnd, out r);
        }

        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ExileLens";

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(ValueName, startMinimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
