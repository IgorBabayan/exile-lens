using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.Logging;

namespace ExileLens.Platform.Linux;

/// <summary>Describes the Linux display session so the UI can explain limitations honestly.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxSession
{
    public static bool HasX11 => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static bool IsWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                                    || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public static string Desktop => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown";

    public static string? Limitation => (HasX11, IsWayland) switch
    {
        (false, _) => "No X11/XWayland display is available, so global shortcuts and game window detection are disabled. "
                      + "Bind `exile-lens --action price-check` / `--action map-check` to keys in your compositor instead.",
        (true, true) => "Wayland session: shortcuts work while the game runs through XWayland (the default for Steam/Proton). "
                        + "If the game uses a native Wayland driver, bind `exile-lens --action price-check` in your compositor.",
        _ => null,
    };
}

/// <summary>
/// X11 (and XWayland) global shortcuts. Keys are grabbed on the root window only while a supported game window
/// is active (tracked event-driven through _NET_ACTIVE_WINDOW PropertyNotify, no polling), so they never steal
/// keys from other applications. A dedicated thread owns the X connection; a self-pipe wakes it for changes.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class X11HotkeyService : IGlobalHotkeyService
{
    private readonly ILogger<X11HotkeyService> _logger;
    private readonly ConcurrentQueue<IReadOnlyCollection<KeyChord>> _pending = new();
    private readonly Dictionary<(int Keycode, uint Mods), KeyChord> _bindings = [];
    private readonly HashSet<(int Keycode, uint Mods)> _grabbed = [];
    private Thread? _thread;
    private nint _display;
    private nuint _root;
    private nuint _netActiveWindow;
    private nuint _netWmName;
    private int _wakeRead = -1;
    private int _wakeWrite = -1;
    private volatile bool _running;
    private bool _gameActive;

    public X11HotkeyService(ILogger<X11HotkeyService> logger)
    {
        _logger = logger;
    }

    public bool IsSupported => LinuxSession.HasX11;

    public string? Limitation => LinuxSession.Limitation;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public IReadOnlyDictionary<KeyChord, string> Register(IReadOnlyCollection<KeyChord> chords)
    {
        var errors = new Dictionary<KeyChord, string>();
        if (!IsSupported)
        {
            foreach (var c in chords)
            {
                errors[c] = LinuxSession.Limitation ?? "Global shortcuts are unavailable.";
            }

            return errors;
        }

        foreach (var c in chords)
        {
            if (X11.KeysymName(c.Key) is null)
            {
                errors[c] = $"Key '{c.Key}' is not supported.";
            }
        }

        EnsureThread();
        _pending.Enqueue(chords.ToList());
        Wake();
        return errors;
    }

    public void Unregister()
    {
        _pending.Enqueue([]);
        Wake();
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        int* fds = stackalloc int[2];
        if (X11.pipe(fds) == 0)
        {
            _wakeRead = fds[0];
            _wakeWrite = fds[1];
        }

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "ExileLens X11 hotkeys" };
        _thread.Start();
    }

    private void Wake()
    {
        if (_wakeWrite >= 0)
        {
            byte b = 1;
            X11.write(_wakeWrite, &b, 1);
        }
    }

    private void Run()
    {
        _display = X11.XOpenDisplay(0);
        if (_display == 0)
        {
            _logger.LogWarning("XOpenDisplay failed; global shortcuts disabled");
            return;
        }

        _root = X11.XDefaultRootWindow(_display);
        _netActiveWindow = X11.XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
        _netWmName = X11.XInternAtom(_display, "_NET_WM_NAME", 0);
        X11.XSelectInput(_display, _root, (nint)X11.PropertyChangeMask);
        X11.XFlush(_display);
        UpdateActive();

        var xfd = X11.XConnectionNumber(_display);
        X11.XEvent ev;
        var fds = stackalloc X11.PollFd[2];
        var drain = stackalloc byte[64];
        while (_running)
        {
            while (X11.XPending(_display) > 0)
            {
                X11.XNextEvent(_display, &ev);
                Handle(&ev);
            }

            while (_pending.TryDequeue(out var chords))
            {
                ApplyBindings(chords);
            }

            fds[0] = new X11.PollFd { Fd = xfd, Events = X11.POLLIN };
            fds[1] = new X11.PollFd { Fd = _wakeRead, Events = X11.POLLIN };
            X11.poll(fds, _wakeRead >= 0 ? 2u : 1u, -1);
            if (_wakeRead >= 0 && (fds[1].Revents & X11.POLLIN) != 0)
            {
                X11.read(_wakeRead, drain, 64);
            }
        }

        UngrabAll();
        X11.XCloseDisplay(_display);
    }

    private void Handle(X11.XEvent* ev)
    {
        if (ev->Type == X11.PropertyNotify)
        {
            var pe = (X11.XPropertyEvent*)ev;
            if (pe->Atom == _netActiveWindow)
            {
                UpdateActive();
            }
        }
        else if (ev->Type == X11.KeyPress)
        {
            var ke = (X11.XKeyEvent*)ev;
            var mods = ke->State & (X11.ShiftMask | X11.ControlMask | X11.Mod1Mask | X11.Mod4Mask);
            if (_bindings.TryGetValue(((int)ke->Keycode, mods), out var chord))
            {
                ThreadPool.UnsafeQueueUserWorkItem(static s => s.Self.HotkeyPressed?.Invoke(s.Self, new HotkeyPressedEventArgs(s.Chord)), (Self: this, Chord: chord), false);
            }
        }
    }

    private void UpdateActive()
    {
        using var trap = X11.TrapErrors(_display);
        var active = X11.GetWindowProperty(_display, _root, _netActiveWindow);
        var isGame = false;
        if (active != 0)
        {
            var (name, cls) = X11.GetClass(_display, active);
            var title = X11.GetStringProperty(_display, active, _netWmName);
            isGame = GameWindowMatcher.Match(title, name) is not null || GameWindowMatcher.Match(title, cls) is not null;
        }

        if (isGame != _gameActive)
        {
            _gameActive = isGame;
            RefreshGrabs();
        }
    }

    private void ApplyBindings(IReadOnlyCollection<KeyChord> chords)
    {
        _bindings.Clear();
        foreach (var chord in chords)
        {
            if (X11.KeysymName(chord.Key) is not { } sym)
            {
                continue;
            }

            var keycode = X11.XKeysymToKeycode(_display, X11.XStringToKeysym(sym));
            if (keycode == 0)
            {
                continue;
            }

            _bindings[(keycode, ToX11(chord.Modifiers))] = chord;
        }

        RefreshGrabs();
    }

    /// <summary>Grabs capture shortcuts only while the game is active; Escape (close) is grabbed whenever registered.</summary>
    private void RefreshGrabs()
    {
        var wanted = _bindings
            .Where(kv => _gameActive || kv.Value.Key == "Escape")
            .Select(kv => kv.Key)
            .ToHashSet();

        foreach (var g in _grabbed.Except(wanted).ToList())
        {
            foreach (var extra in LockVariants)
            {
                X11.XUngrabKey(_display, g.Keycode, g.Mods | extra, _root);
            }

            _grabbed.Remove(g);
        }

        foreach (var g in wanted.Except(_grabbed).ToList())
        {
            using (X11.TrapErrors(_display))
            {
                foreach (var extra in LockVariants)
                {
                    X11.XGrabKey(_display, g.Keycode, g.Mods | extra, _root, 0, X11.GrabModeAsync, X11.GrabModeAsync);
                }
            }

            if (X11.LastErrorCode == 10)
            {
                _logger.LogWarning("Shortcut {Chord} is already grabbed by another application", _bindings[g]);
            }

            _grabbed.Add(g);
        }

        X11.XFlush(_display);
    }

    private void UngrabAll()
    {
        foreach (var g in _grabbed)
        {
            foreach (var extra in LockVariants)
            {
                X11.XUngrabKey(_display, g.Keycode, g.Mods | extra, _root);
            }
        }

        _grabbed.Clear();
        X11.XFlush(_display);
    }

    private static readonly uint[] LockVariants = [0, X11.LockMask, X11.Mod2Mask, X11.LockMask | X11.Mod2Mask];

    internal static uint ToX11(HotkeyModifiers m)
    {
        uint r = 0;
        if (m.HasFlag(HotkeyModifiers.Shift))
        {
            r |= X11.ShiftMask;
        }

        if (m.HasFlag(HotkeyModifiers.Control))
        {
            r |= X11.ControlMask;
        }

        if (m.HasFlag(HotkeyModifiers.Alt))
        {
            r |= X11.Mod1Mask;
        }

        if (m.HasFlag(HotkeyModifiers.Meta))
        {
            r |= X11.Mod4Mask;
        }

        return r;
    }

    public void Dispose()
    {
        _running = false;
        Wake();
        _thread?.Join(TimeSpan.FromSeconds(1));
        if (_wakeRead >= 0)
        {
            X11.close(_wakeRead);
            X11.close(_wakeWrite);
        }
    }
}

/// <summary>Window queries and key injection on a separate X connection guarded by a lock.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class X11DisplayConnection : IDisposable
{
    private readonly Lock _gate = new();
    private nint _display;

    public T Use<T>(Func<nint, T> action, T fallback)
    {
        lock (_gate)
        {
            if (_display == 0)
            {
                if (!LinuxSession.HasX11)
                {
                    return fallback;
                }

                _display = X11.XOpenDisplay(0);
                if (_display == 0)
                {
                    return fallback;
                }
            }

            using var trap = X11.TrapErrors(_display);
            return action(_display);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_display != 0)
            {
                X11.XCloseDisplay(_display);
                _display = 0;
            }
        }
    }
}

[SupportedOSPlatform("linux")]
public sealed class X11GameWindowService(X11DisplayConnection connection) : IGameWindowService
{
    public GameWindowInfo? GetActiveGameWindow() => connection.Use(d =>
    {
        var root = X11.XDefaultRootWindow(d);
        var active = X11.GetWindowProperty(d, root, X11.XInternAtom(d, "_NET_ACTIVE_WINDOW", 0));
        return active == 0 ? null : Describe(d, root, active);
    }, null);

    public GameWindowInfo? FindGameWindow() => connection.Use(d =>
    {
        var root = X11.XDefaultRootWindow(d);
        var list = X11.XInternAtom(d, "_NET_CLIENT_LIST", 0);
        if (X11.XGetWindowProperty(d, root, list, 0, 4096, 0, 0, out _, out var format, out var n, out _, out var prop) != X11.Success || prop == 0)
        {
            return null;
        }

        try
        {
            if (format != 32)
            {
                return null;
            }

            unsafe
            {
                var windows = (nuint*)prop;
                for (nuint i = 0; i < n; i++)
                {
                    if (Describe(d, root, windows[i]) is { } info)
                    {
                        return info;
                    }
                }
            }

            return null;
        }
        finally
        {
            X11.XFree(prop);
        }
    }, null);

    public PixelPoint? GetCursorPosition() => connection.Use<PixelPoint?>(d =>
        X11.XQueryPointer(d, X11.XDefaultRootWindow(d), out _, out _, out var x, out var y, out _, out _, out _) != 0 ? new PixelPoint(x, y) : null, null);

    public void FocusWindow(GameWindowInfo window) => connection.Use(d =>
    {
        X11.XRaiseWindow(d, (nuint)window.Handle);
        X11.XSetInputFocus(d, (nuint)window.Handle, 2, 0);
        X11.XFlush(d);
        return true;
    }, false);

    private static GameWindowInfo? Describe(nint d, nuint root, nuint window)
    {
        var (name, cls) = X11.GetClass(d, window);
        var title = X11.GetStringProperty(d, window, X11.XInternAtom(d, "_NET_WM_NAME", 0));
        var game = GameWindowMatcher.Match(title, name) ?? GameWindowMatcher.Match(title, cls);
        if (game is null)
        {
            return null;
        }

        X11.XGetGeometry(d, window, out _, out _, out _, out var w, out var h, out _, out _);
        X11.XTranslateCoordinates(d, window, root, 0, 0, out var x, out var y, out _);
        return new GameWindowInfo(game.Value, title ?? string.Empty, cls ?? name, new PixelRect(x, y, (int)w, (int)h), (nint)window);
    }
}

[SupportedOSPlatform("linux")]
public sealed class X11KeyboardInputService(X11DisplayConnection connection) : IKeyboardInputService
{
    public bool IsSupported => LinuxSession.HasX11;

    public string? Limitation => LinuxSession.HasX11 ? null : LinuxSession.Limitation;

    public Task SendCopyAsync(bool advanced, CancellationToken cancellationToken = default)
    {
        connection.Use(d =>
        {
            X11.XQueryPointer(d, X11.XDefaultRootWindow(d), out _, out _, out _, out _, out _, out _, out var mask);
            var ctrl = X11.XKeysymToKeycode(d, X11.XStringToKeysym("Control_L"));
            var alt = X11.XKeysymToKeycode(d, X11.XStringToKeysym("Alt_L"));
            var c = X11.XKeysymToKeycode(d, X11.XStringToKeysym("c"));
            var ctrlHeld = (mask & X11.ControlMask) != 0;
            var altHeld = (mask & X11.Mod1Mask) != 0;

            if (!ctrlHeld)
            {
                X11.XTestFakeKeyEvent(d, ctrl, 1, 0);
            }

            if (advanced && !altHeld)
            {
                X11.XTestFakeKeyEvent(d, alt, 1, 0);
            }

            X11.XTestFakeKeyEvent(d, c, 1, 0);
            X11.XTestFakeKeyEvent(d, c, 0, 0);
            if (advanced && !altHeld)
            {
                X11.XTestFakeKeyEvent(d, alt, 0, 0);
            }

            if (!ctrlHeld)
            {
                X11.XTestFakeKeyEvent(d, ctrl, 0, 0);
            }

            X11.XFlush(d);
            return true;
        }, false);
        return Task.CompletedTask;
    }

    public void SendChatKeystroke(ChatKeystroke keystroke)
    {
        connection.Use(d =>
        {
            // Release modifiers still held from the shortcut so Enter is not read as Ctrl+Enter etc.
            X11.XQueryPointer(d, X11.XDefaultRootWindow(d), out _, out _, out _, out _, out _, out _, out var mask);
            Release(d, mask, X11.ControlMask, "Control_L", "Control_R");
            Release(d, mask, X11.ShiftMask, "Shift_L", "Shift_R");
            Release(d, mask, X11.Mod1Mask, "Alt_L", "Alt_R");
            Release(d, mask, X11.Mod4Mask, "Super_L", "Super_R");

            var (sym, ctrl) = keystroke switch
            {
                ChatKeystroke.SelectAll => ("a", true),
                ChatKeystroke.Paste => ("v", true),
                _ => ("Return", false),
            };

            var key = X11.XKeysymToKeycode(d, X11.XStringToKeysym(sym));
            var control = ctrl ? X11.XKeysymToKeycode(d, X11.XStringToKeysym("Control_L")) : (byte)0;
            if (ctrl)
            {
                X11.XTestFakeKeyEvent(d, control, 1, 0);
            }

            X11.XTestFakeKeyEvent(d, key, 1, 0);
            X11.XTestFakeKeyEvent(d, key, 0, 0);
            if (ctrl)
            {
                X11.XTestFakeKeyEvent(d, control, 0, 0);
            }

            X11.XFlush(d);
            return true;
        }, false);
    }

    private static void Release(nint d, uint mask, uint bit, string left, string right)
    {
        if ((mask & bit) == 0)
        {
            return;
        }

        X11.XTestFakeKeyEvent(d, X11.XKeysymToKeycode(d, X11.XStringToKeysym(left)), 0, 0);
        X11.XTestFakeKeyEvent(d, X11.XKeysymToKeycode(d, X11.XStringToKeysym(right)), 0, 0);
    }
}

/// <summary>XDG autostart entry (~/.config/autostart/exile-lens.desktop).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxAutoStartService : IAutoStartService
{
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "exile-lens.desktop");

    public bool IsSupported => true;

    public bool IsEnabled() => File.Exists(FilePath);

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        if (!enabled)
        {
            File.Delete(FilePath);
            return;
        }

        // AppImages report their own path via $APPIMAGE.
        var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath ?? "exile-lens";
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $"""
            [Desktop Entry]
            Type=Application
            Name=Exile Lens
            Exec="{exe}"{(startMinimized ? " --minimized" : string.Empty)}
            X-GNOME-Autostart-enabled=true
            Terminal=false
            """);
    }
}
