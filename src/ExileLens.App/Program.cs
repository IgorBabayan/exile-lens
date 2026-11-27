using Avalonia;
using ExileLens.App.Services;

namespace ExileLens.App;

internal static class Program
{
    /// <summary>
    /// Command line:
    ///   --minimized                 start in the tray
    ///   --action price-check|map-check|close
    ///                               forward an action to the running instance (for compositor key bindings
    ///                               on Wayland, or scripts) and exit.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var action = ParseAction(args);
        using var instance = SingleInstance.TryAcquire();
        if (!instance.IsPrimary)
        {
            // Another instance owns the shortcuts: hand over the request and exit without registering anything.
            SingleInstance.Send(action ?? "show");
            return 0;
        }

        if (action is not null)
        {
            Console.Error.WriteLine("Exile Lens is not running; start it first, then bind --action to a key.");
            return 1;
        }

        App.StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        App.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { EnableIme = false })
            .LogToTrace();

    private static string? ParseAction(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--action", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
    }
}
