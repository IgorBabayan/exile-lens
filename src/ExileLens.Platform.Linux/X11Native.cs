using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ExileLens.Platform.Linux;

[SupportedOSPlatform("linux")]
internal static unsafe partial class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    public const int KeyPress = 2;
    public const int PropertyNotify = 28;
    public const long PropertyChangeMask = 1L << 22;
    public const uint ShiftMask = 1;
    public const uint LockMask = 2;
    public const uint ControlMask = 4;
    public const uint Mod1Mask = 8;
    public const uint Mod2Mask = 16;
    public const uint Mod4Mask = 64;
    public const int GrabModeAsync = 1;
    public const int Success = 0;

    /// <summary>XEvent is a 192-byte union on LP64.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XEvent
    {
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint Root;
        public nuint Subwindow;
        public nuint Time;
        public int X;
        public int Y;
        public int XRoot;
        public int YRoot;
        public uint State;
        public uint Keycode;
        public int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XPropertyEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint Atom;
        public nuint Time;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XErrorEvent
    {
        public int Type;
        public nint Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClassHint
    {
        public nint ResName;
        public nint ResClass;
    }

    [LibraryImport(LibX11)]
    public static partial nint XOpenDisplay(nint name);

    [LibraryImport(LibX11)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    public static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11)]
    public static partial int XConnectionNumber(nint display);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    public static partial int XSelectInput(nint display, nuint window, nint mask);

    [LibraryImport(LibX11)]
    public static partial int XGrabKey(nint display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

    [LibraryImport(LibX11)]
    public static partial int XUngrabKey(nint display, int keycode, uint modifiers, nuint grabWindow);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XStringToKeysym(string name);

    [LibraryImport(LibX11)]
    public static partial byte XKeysymToKeycode(nint display, nuint keysym);

    [LibraryImport(LibX11)]
    public static partial int XPending(nint display);

    [LibraryImport(LibX11)]
    public static partial int XNextEvent(nint display, XEvent* ev);

    [LibraryImport(LibX11)]
    public static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    public static partial int XSync(nint display, int discard);

    [LibraryImport(LibX11)]
    public static partial int XFree(nint data);

    [LibraryImport(LibX11)]
    public static partial int XGetWindowProperty(nint display, nuint w, nuint property, nint longOffset, nint longLength, int delete, nuint reqType,
        out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out nint prop);

    [LibraryImport(LibX11)]
    public static partial int XGetClassHint(nint display, nuint w, out XClassHint hint);

    [LibraryImport(LibX11)]
    public static partial int XGetGeometry(nint display, nuint d, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [LibraryImport(LibX11)]
    public static partial int XTranslateCoordinates(nint display, nuint src, nuint dest, int srcX, int srcY, out int destX, out int destY, out nuint child);

    [LibraryImport(LibX11)]
    public static partial int XQueryPointer(nint display, nuint w, out nuint root, out nuint child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(LibX11)]
    public static partial nint XSetErrorHandler(delegate* unmanaged<nint, XErrorEvent*, int> handler);

    [LibraryImport(LibX11)]
    public static partial int XRaiseWindow(nint display, nuint w);

    [LibraryImport(LibX11)]
    public static partial int XSetInputFocus(nint display, nuint focus, int revertTo, nuint time);

    [LibraryImport(LibXtst)]
    public static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    public const short POLLIN = 1;

    [LibraryImport("libc", SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int pipe(int* fds);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint write(int fd, byte* buf, nuint count);

    [LibraryImport("libc")]
    public static partial int close(int fd);

    /// <summary>Last X protocol error code seen by the process-wide handler (BadAccess=10 when a grab conflicts).</summary>
    public static volatile int LastErrorCode;

    [UnmanagedCallersOnly]
    public static int OnError(nint display, XErrorEvent* ev)
    {
        LastErrorCode = ev->ErrorCode;
        return 0; // never terminate the process on async X errors
    }

    /// <summary>
    /// Temporarily installs a non-fatal error handler (the Xlib default exits the process on errors such as
    /// BadAccess for a conflicting grab or BadWindow for a window that just closed), then restores the
    /// previous handler (e.g. Avalonia's).
    /// </summary>
    public static ErrorTrap TrapErrors(nint display) => new(display);

    public readonly struct ErrorTrap : IDisposable
    {
        private readonly nint _display;
        private readonly nint _previous;

        public ErrorTrap(nint display)
        {
            _display = display;
            LastErrorCode = 0;
            _previous = XSetErrorHandler(&OnError);
        }

        public void Dispose()
        {
            XSync(_display, 0);
            XSetErrorHandler((delegate* unmanaged<nint, XErrorEvent*, int>)_previous);
        }
    }

    public static nuint GetWindowProperty(nint display, nuint window, nuint atom)
    {
        if (XGetWindowProperty(display, window, atom, 0, 1, 0, 0, out _, out var format, out var n, out _, out var prop) != Success || prop == 0)
        {
            return 0;
        }

        try
        {
            return n > 0 && format == 32 ? *(nuint*)prop : 0;
        }
        finally
        {
            XFree(prop);
        }
    }

    public static string? GetStringProperty(nint display, nuint window, nuint atom)
    {
        if (XGetWindowProperty(display, window, atom, 0, 1024, 0, 0, out _, out var format, out var n, out _, out var prop) != Success || prop == 0)
        {
            return null;
        }

        try
        {
            return n > 0 && format == 8 ? Marshal.PtrToStringUTF8(prop, (int)n) : null;
        }
        finally
        {
            XFree(prop);
        }
    }

    public static (string? Name, string? Class) GetClass(nint display, nuint window)
    {
        if (XGetClassHint(display, window, out var hint) == 0)
        {
            return (null, null);
        }

        try
        {
            return (Marshal.PtrToStringUTF8(hint.ResName), Marshal.PtrToStringUTF8(hint.ResClass));
        }
        finally
        {
            if (hint.ResName != 0)
            {
                XFree(hint.ResName);
            }

            if (hint.ResClass != 0)
            {
                XFree(hint.ResClass);
            }
        }
    }

    public static string? KeysymName(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetter(key[0]) => key.ToLowerInvariant(),
        { Length: 1 } => key,
        "Escape" => "Escape",
        "Space" => "space",
        "Tab" => "Tab",
        "Enter" => "Return",
        "Backspace" => "BackSpace",
        "Insert" => "Insert",
        "Delete" => "Delete",
        "Home" => "Home",
        "End" => "End",
        "Pageup" => "Prior",
        "Pagedown" => "Next",
        _ when key[0] == 'F' => key,
        _ => null,
    };
}
