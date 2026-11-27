using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ExileLens.App.Services;
using ExileLens.App.ViewModels;
using ExileLens.App.Views;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.History;
using ExileLens.Application.Maps;
using ExileLens.Application.Market;
using ExileLens.Application.Platform;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExileLens.App;

public sealed partial class App : Avalonia.Application
{
    private TrayController? _tray;
    private ServiceProvider? _services;

    internal static bool StartMinimized { get; set; }
    internal static SingleInstance? Instance { get; set; }

    public static IServiceProvider Services => ((App)Current!)._services!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _services = ServiceConfiguration.Build();
        var settings = _services.GetRequiredService<ISettingsService>();

        // Settings are tiny and needed to style the first frame; everything else loads after the window shows.
        settings.LoadAsync().GetAwaiter().GetResult();
        ApplyAppearance(settings.Current);
        settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => ApplyAppearance(settings.Current));

        var mainVm = _services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = mainVm };
        desktop.MainWindow = window;
        _services.GetRequiredService<AvaloniaClipboardService>().Attach(window);
        if (!StartMinimized && !settings.Current.General.StartMinimized)
        {
            window.Show();
        }

        _tray = ActivatorUtilities.CreateInstance<TrayController>(_services, desktop);
        _tray.Install();

        // Flush pending writes synchronously on exit; FlushAsync never needs the UI thread.
        desktop.Exit += (_, _) => FlushAsync().GetAwaiter().GetResult();
        if (Instance is { } instance)
        {
            instance.MessageReceived += message => Dispatcher.UIThread.Post(() => OnInstanceMessage(message, window));
            instance.StartListening();
        }

        _ = StartBackgroundAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartBackgroundAsync()
    {
        var services = _services!;
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            await Task.WhenAll(
                services.GetRequiredService<IModifierProfileService>().LoadAsync(),
                services.GetRequiredService<IHistoryService>().LoadAsync()).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                services.GetRequiredService<GameActionCoordinator>().Start();
                services.GetRequiredService<MainWindowViewModel>().OnServicesReady();
                _tray?.Refresh();
            });

            if (services.GetRequiredService<ISettingsService>().Current.General.CheckForUpdates)
            {
                await Dispatcher.UIThread.InvokeAsync(() => services.GetRequiredService<SettingsViewModel>().CheckUpdatesCommand.Execute(null));
            }

            // Warm caches without blocking the UI: stat definitions and league lists.
            var pricing = services.GetRequiredService<PriceCheckService>();
            var leagues = services.GetRequiredService<ILeagueService>();
            var currencyIcons = services.GetRequiredService<CurrencyIconProvider>();
            var warmCurrencyIcons = services.GetRequiredService<ISettingsService>().Current.Trade.ShowCurrencyIcons;
            foreach (var module in services.GetRequiredService<IGameRegistry>().All)
            {
                _ = leagues.GetLeaguesAsync(module, CancellationToken.None);
                _ = pricing.PreloadAsync(module, CancellationToken.None);
                if (warmCurrencyIcons)
                {
                    _ = currencyIcons.WarmAsync(module);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background start-up failed");
        }
    }

    private void OnInstanceMessage(string message, Window window)
    {
        var coordinator = _services!.GetRequiredService<GameActionCoordinator>();
        switch (message)
        {
            case "price-check":
                coordinator.Trigger(HotkeyAction.PriceCheck);
                break;
            case "map-check":
                coordinator.Trigger(HotkeyAction.MapCheck);
                break;
            case "close":
                coordinator.Trigger(HotkeyAction.CloseOverlay);
                break;
            default:
                ShowMainWindow(window);
                break;
        }
    }

    internal static void ShowMainWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ApplyAppearance(AppSettings settings)
    {
        RequestedThemeVariant = settings.Appearance.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (Color.TryParse(settings.Appearance.AccentColor, out var accent) && Resources is ResourceDictionary root)
        {
            foreach (var provider in root.MergedDictionaries)
            {
                // Compiled ResourceInclude may be inlined as a dictionary or kept as an include wrapper.
                var dictionary = provider as ResourceDictionary ?? (provider as Avalonia.Markup.Xaml.Styling.ResourceInclude)?.Loaded as ResourceDictionary;
                if (dictionary is null)
                {
                    continue;
                }

                foreach (var (variant, themeDict) in dictionary.ThemeDictionaries)
                {
                    if (themeDict is not ResourceDictionary rd)
                    {
                        continue;
                    }

                    var tuned = variant == ThemeVariant.Dark ? Lighten(accent, 0.08) : accent;
                    rd["AccentColor"] = tuned;
                    rd["AccentBrush"] = new SolidColorBrush(tuned);
                    rd["AccentHoverBrush"] = new SolidColorBrush(variant == ThemeVariant.Dark ? Lighten(tuned, 0.08) : Lighten(tuned, -0.08));
                }
            }
        }

        Resources["FontSizeBody"] = settings.Appearance.FontSize;
    }

    internal static Color Lighten(Color c, double amount)
    {
        static byte Adjust(byte v, double a) => (byte)Math.Clamp(a >= 0 ? v + ((255 - v) * a) : v * (1 + a), 0, 255);
        return Color.FromArgb(c.A, Adjust(c.R, amount), Adjust(c.G, amount), Adjust(c.B, amount));
    }

    private async Task FlushAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(
                _services.GetRequiredService<ISettingsService>().FlushAsync(),
                _services.GetRequiredService<IModifierProfileService>().FlushAsync(),
                _services.GetRequiredService<IHistoryService>().FlushAsync()).ConfigureAwait(false);
        }
        finally
        {
            _tray?.Dispose();
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static WindowIcon? _icon;

    /// <summary>
    /// Shared window/tray icon, decoded once. On Windows the multi-size .ico is used so the shell picks the hand-sized
    /// 16/20/24/32px frames for the title bar, taskbar and tray instead of downscaling the 256px PNG (which looks jagged).
    /// </summary>
    internal static WindowIcon LoadIcon()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        var asset = OperatingSystem.IsWindows() ? "icon.ico" : "icon.png";
        using var stream = AssetLoader.Open(new Uri($"avares://ExileLens/Assets/{asset}"));
        return _icon = new WindowIcon(stream);
    }
}
