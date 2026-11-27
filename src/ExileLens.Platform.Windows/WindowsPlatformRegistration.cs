using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPlatformRegistration
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<WindowsGameWindowService>();
        services.AddSingleton<IGameWindowService>(sp => sp.GetRequiredService<WindowsGameWindowService>());
        services.AddSingleton<IGlobalHotkeyService, WindowsHotkeyService>();
        services.AddSingleton<IKeyboardInputService, WindowsKeyboardInputService>();
        services.AddSingleton<IAutoStartService, WindowsAutoStartService>();
        return services;
    }
}
