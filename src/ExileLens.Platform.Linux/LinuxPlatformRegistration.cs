using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.Platform.Linux;

[SupportedOSPlatform("linux")]
public static class LinuxPlatformRegistration
{
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<X11DisplayConnection>();
        services.AddSingleton<IGameWindowService, X11GameWindowService>();
        services.AddSingleton<IGlobalHotkeyService, X11HotkeyService>();
        services.AddSingleton<IKeyboardInputService, X11KeyboardInputService>();
        services.AddSingleton<IAutoStartService, LinuxAutoStartService>();
        return services;
    }
}
