using System.Runtime.Versioning;
using ExileLens.Application.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.Platform.MacOS;

[SupportedOSPlatform("macos")]
public static class MacPlatformRegistration
{
    public static IServiceCollection AddMacPlatform(this IServiceCollection services)
    {
        services.AddSingleton<MacGameWindowService>();
        services.AddSingleton<IGameWindowService>(sp => sp.GetRequiredService<MacGameWindowService>());
        services.AddSingleton<IGlobalHotkeyService, MacHotkeyService>();
        services.AddSingleton<IKeyboardInputService, MacKeyboardInputService>();
        services.AddSingleton<IAutoStartService, MacAutoStartService>();
        return services;
    }
}
