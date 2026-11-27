using ExileLens.App.ViewModels;
using ExileLens.Application;
using ExileLens.Application.Capture;
using ExileLens.Application.Games;
using ExileLens.Application.Platform;
using ExileLens.Game.Poe1;
using ExileLens.Game.Poe2;
using ExileLens.Infrastructure;
using ExileLens.Platform.Linux;
using ExileLens.Platform.MacOS;
using ExileLens.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExileLens.App.Services;

internal static class ServiceConfiguration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
#if DEBUG
            b.AddConsole();
#endif
            b.AddProvider(new FileLoggerProvider());
        });

        services.AddExileLensApplication();
        services.AddExileLensInfrastructure();
        services.AddSingleton<IGameModule, Poe1GameModule>();
        services.AddSingleton<IGameModule, Poe2GameModule>();

        if (OperatingSystem.IsWindows())
        {
            services.AddWindowsPlatform();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddMacPlatform();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddLinuxPlatform();
        }

        services.AddSingleton<AvaloniaClipboardService>();
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<AvaloniaClipboardService>());
        services.AddSingleton<DangerPalette>();
        services.AddSingleton<ItemIconLoader>();
        services.AddSingleton<OverlayService>();
        services.AddSingleton<IOverlayService>(sp => sp.GetRequiredService<OverlayService>());

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<PriceCheckPageViewModel>();
        services.AddSingleton<MapModifiersViewModel>();
        services.AddSingleton<DisenchantmentViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<PriceCheckOverlayViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
