using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ExileLens.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public const uint KeyDown = 10;
    public const uint TapDisabledByTimeout = 0xFFFFFFFE;
    public const uint TapDisabledByUserInput = 0xFFFFFFFF;
    public const int KeyboardEventKeycode = 9;
    public const ulong FlagShift = 0x20000;
    public const ulong FlagControl = 0x40000;
    public const ulong FlagAlternate = 0x80000;
    public const ulong FlagCommand = 0x100000;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
    }

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest,
        delegate* unmanaged<nint, uint, nint, nint, nint> callback, nint userInfo);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(CoreGraphics)]
    public static partial long CGEventGetIntegerValueField(nint evt, int field);

    [LibraryImport(CoreGraphics)]
    public static partial ulong CGEventGetFlags(nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventCreateKeyboardEvent(nint source, ushort keycode, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventSetFlags(nint evt, ulong flags);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventPost(uint tap, nint evt);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphics)]
    public static partial CGPoint CGEventGetLocation(nint evt);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopAddSource(nint rl, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopRun();

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopStop(nint rl);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint obj);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AXIsProcessTrustedWithOptions(nint options);

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AXIsProcessTrusted();

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial int SendInt(nint receiver, nint selector);

    [LibraryImport(ObjC)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    private static nint Export(string library, string symbol) =>
        NativeLibrary.TryLoad(library, out var lib) && NativeLibrary.TryGetExport(lib, symbol, out var p) ? p : 0;

    /// <summary>Reads an exported CF constant (pointer-valued global).</summary>
    public static nint Constant(string library, string symbol)
    {
        var p = Export(library, symbol);
        return p == 0 ? 0 : *(nint*)p;
    }

    public static nint RunLoopCommonModes => Constant(CoreFoundation, "kCFRunLoopCommonModes");

    /// <summary>Asks for Accessibility permission (shows the system prompt once) and returns whether it is granted.</summary>
    public static bool RequestAccessibility()
    {
        var promptKey = Constant(ApplicationServices, "kAXTrustedCheckOptionPrompt");
        var trueValue = Constant(CoreFoundation, "kCFBooleanTrue");
        var keyCallbacks = Export(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = Export(CoreFoundation, "kCFTypeDictionaryValueCallBacks");
        if (promptKey == 0 || trueValue == 0)
        {
            return AXIsProcessTrusted();
        }

        var dict = CFDictionaryCreate(0, &promptKey, &trueValue, 1, keyCallbacks, valueCallbacks);
        try
        {
            return AXIsProcessTrustedWithOptions(dict);
        }
        finally
        {
            CFRelease(dict);
        }
    }

    /// <summary>Frontmost application name and process id via NSWorkspace.</summary>
    public static (string? Name, int Pid) FrontmostApplication()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ws = SendPtr(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            var app = ws == 0 ? 0 : SendPtr(ws, sel_registerName("frontmostApplication"));
            if (app == 0)
            {
                return (null, 0);
            }

            var pid = SendInt(app, sel_registerName("processIdentifier"));
            var nsName = SendPtr(app, sel_registerName("localizedName"));
            var utf8 = nsName == 0 ? 0 : SendPtr(nsName, sel_registerName("UTF8String"));
            return (utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8), pid);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public static ushort? KeyCode(string key) => key switch
    {
        "A" => 0, "S" => 1, "D" => 2, "F" => 3, "H" => 4, "G" => 5, "Z" => 6, "X" => 7, "C" => 8, "V" => 9,
        "B" => 11, "Q" => 12, "W" => 13, "E" => 14, "R" => 15, "Y" => 16, "T" => 17, "1" => 18, "2" => 19,
        "3" => 20, "4" => 21, "6" => 22, "5" => 23, "9" => 25, "7" => 26, "8" => 28, "0" => 29, "O" => 31,
        "U" => 32, "I" => 34, "P" => 35, "L" => 37, "J" => 38, "K" => 40, "N" => 45, "M" => 46,
        "Enter" => 36, "Tab" => 48, "Space" => 49, "Backspace" => 51, "Escape" => 53,
        "F1" => 122, "F2" => 120, "F3" => 99, "F4" => 118, "F5" => 96, "F6" => 97, "F7" => 98, "F8" => 100,
        "F9" => 101, "F10" => 109, "F11" => 103, "F12" => 111, "Home" => 115, "End" => 119, "Pageup" => 116,
        "Pagedown" => 121, "Delete" => 117, "Insert" => 114,
        _ => null,
    };
}
