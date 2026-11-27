using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.Logging;

namespace ExileLens.Platform.MacOS;

/// <summary>
/// Global shortcuts through a Quartz event tap. Requires the Accessibility permission, which is requested
/// (system prompt) only when shortcuts are first registered. Chords are consumed only while the game is the
/// frontmost application.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacHotkeyService : IGlobalHotkeyService
{
    private static MacHotkeyService? _instance;
    private readonly MacGameWindowService _windows;
    private readonly ILogger<MacHotkeyService> _logger;
    private volatile Dictionary<(ushort Code, HotkeyModifiers Mods), KeyChord> _bindings = [];
    private Thread? _thread;
    private nint _tap;
    private nint _runLoop;
    private bool _trusted;

    public MacHotkeyService(MacGameWindowService windows, ILogger<MacHotkeyService> logger)
    {
        _windows = windows;
        _logger = logger;
    }

    public bool IsSupported => _thread is null || _trusted;

    public string? Limitation => _thread is not null && !_trusted
        ? "Grant Exile Lens access in System Settings → Privacy & Security → Accessibility, then restart the app."
        : null;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public IReadOnlyDictionary<KeyChord, string> Register(IReadOnlyCollection<KeyChord> chords)
    {
        var errors = new Dictionary<KeyChord, string>();
        var map = new Dictionary<(ushort, HotkeyModifiers), KeyChord>();
        foreach (var c in chords)
        {
            if (MacNative.KeyCode(c.Key) is { } code)
            {
                map[(code, c.Modifiers)] = c;
            }
            else
            {
                errors[c] = $"Key '{c.Key}' is not supported.";
            }
        }

        _bindings = map;
        if (map.Count > 0)
        {
            EnsureTap();
        }

        if (!_trusted)
        {
            foreach (var c in chords)
            {
                errors.TryAdd(c, Limitation ?? "Accessibility permission is required.");
            }
        }

        return errors;
    }

    public void Unregister() => _bindings = [];

    private void EnsureTap()
    {
        if (_thread is not null)
        {
            return;
        }

        _instance = this;
        _trusted = MacNative.RequestAccessibility();
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _tap = MacNative.CGEventTapCreate(1, 0, 0, 1UL << (int)MacNative.KeyDown, &TapCallback, 0);
            if (_tap == 0)
            {
                _trusted = false;
                _logger.LogWarning("CGEventTapCreate failed (Accessibility permission missing?)");
                ready.Set();
                return;
            }

            _trusted = true;
            var source = MacNative.CFMachPortCreateRunLoopSource(0, _tap, 0);
            _runLoop = MacNative.CFRunLoopGetCurrent();
            MacNative.CFRunLoopAddSource(_runLoop, source, MacNative.RunLoopCommonModes);
            MacNative.CGEventTapEnable(_tap, true);
            ready.Set();
            MacNative.CFRunLoopRun();
            MacNative.CFRelease(source);
        })
        {
            IsBackground = true,
            Name = "ExileLens event tap",
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    [UnmanagedCallersOnly]
    private static nint TapCallback(nint proxy, uint type, nint evt, nint userInfo)
    {
        var self = _instance;
        if (self is null)
        {
            return evt;
        }

        if (type is MacNative.TapDisabledByTimeout or MacNative.TapDisabledByUserInput)
        {
            MacNative.CGEventTapEnable(self._tap, true);
            return evt;
        }

        if (type != MacNative.KeyDown)
        {
            return evt;
        }

        var code = (ushort)MacNative.CGEventGetIntegerValueField(evt, MacNative.KeyboardEventKeycode);
        var flags = MacNative.CGEventGetFlags(evt);
        var mods = HotkeyModifiers.None;
        if ((flags & MacNative.FlagControl) != 0)
        {
            mods |= HotkeyModifiers.Control;
        }

        if ((flags & MacNative.FlagAlternate) != 0)
        {
            mods |= HotkeyModifiers.Alt;
        }

        if ((flags & MacNative.FlagShift) != 0)
        {
            mods |= HotkeyModifiers.Shift;
        }

        if ((flags & MacNative.FlagCommand) != 0)
        {
            mods |= HotkeyModifiers.Meta;
        }

        if (!self._bindings.TryGetValue((code, mods), out var chord))
        {
            return evt;
        }

        if (chord.Key != "Escape" && self._windows.GetActiveGameWindow() is null)
        {
            return evt;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Self.HotkeyPressed?.Invoke(s.Self, new HotkeyPressedEventArgs(s.Chord)), (Self: self, Chord: chord), false);
        return 0; // consume
    }

    public void Dispose()
    {
        if (_runLoop != 0)
        {
            MacNative.CFRunLoopStop(_runLoop);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        if (_tap != 0)
        {
            MacNative.CFRelease(_tap);
        }

        _instance = null;
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacGameWindowService : IGameWindowService
{
    private sealed record CacheEntry(int Pid, long At, GameWindowInfo? Info);

    private volatile CacheEntry? _cache;

    public GameWindowInfo? GetActiveGameWindow()
    {
        var (name, pid) = MacNative.FrontmostApplication();
        if (pid == 0)
        {
            return null;
        }

        var now = Environment.TickCount64;
        var cache = _cache;
        if (cache is not null && cache.Pid == pid && now - cache.At < 1000)
        {
            return cache.Info;
        }

        string? processName = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            processName = p.ProcessName;
        }
        catch (ArgumentException)
        {
        }

        var game = GameWindowMatcher.Match(name, processName);
        // Window geometry needs Screen Recording permission on recent macOS; overlays position from the cursor instead.
        var info = game is { } g ? new GameWindowInfo(g, name ?? string.Empty, processName, default, pid) : null;
        _cache = new CacheEntry(pid, now, info);
        return info;
    }

    public GameWindowInfo? FindGameWindow()
    {
        foreach (var name in (string[])["PathOfExile", "PathOfExile2", "Path of Exile", "Path of Exile 2"])
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (GameWindowMatcher.Match(name, p.ProcessName) is { } g)
                    {
                        return new GameWindowInfo(g, name, p.ProcessName, default, p.Id);
                    }
                }
            }
        }

        return null;
    }

    public unsafe PixelPoint? GetCursorPosition()
    {
        var e = MacNative.CGEventCreate(0);
        if (e == 0)
        {
            return null;
        }

        try
        {
            // Quartz global coordinates are in points with a top-left origin; the overlay service scales by DPI.
            var p = MacNative.CGEventGetLocation(e);
            return new PixelPoint((int)p.X, (int)p.Y);
        }
        finally
        {
            MacNative.CFRelease(e);
        }
    }

    public void FocusWindow(GameWindowInfo window)
    {
        // Overlays never take activation from the game on macOS, so there is nothing to restore.
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacKeyboardInputService : IKeyboardInputService
{
    public bool IsSupported => MacNative.AXIsProcessTrusted();

    public string? Limitation => IsSupported ? null : "Accessibility permission is required to send the item copy keystroke.";

    public Task SendCopyAsync(bool advanced, CancellationToken cancellationToken = default)
    {
        // Path of Exile on macOS copies items with Command+C (Command+Option+C for advanced descriptions).
        var flags = MacNative.FlagCommand | (advanced ? MacNative.FlagAlternate : 0);
        Post(8, true, flags);
        Post(8, false, flags);
        return Task.CompletedTask;
    }

    private static void Post(ushort key, bool down, ulong flags)
    {
        var e = MacNative.CGEventCreateKeyboardEvent(0, key, down);
        if (e == 0)
        {
            return;
        }

        MacNative.CGEventSetFlags(e, flags);
        MacNative.CGEventPost(0, e);
        MacNative.CFRelease(e);
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacAutoStartService : IAutoStartService
{
    private static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "net.exilelens.app.plist");

    public bool IsSupported => true;

    public bool IsEnabled() => File.Exists(PlistPath);

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        if (!enabled)
        {
            File.Delete(PlistPath);
            return;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown.");
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        var args = startMinimized ? $"<string>{System.Security.SecurityElement.Escape(exe)}</string><string>--minimized</string>" : $"<string>{System.Security.SecurityElement.Escape(exe)}</string>";
        File.WriteAllText(PlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>net.exilelens.app</string>
              <key>ProgramArguments</key><array>{args}</array>
              <key>RunAtLoad</key><true/>
            </dict></plist>
            """);
    }
}
