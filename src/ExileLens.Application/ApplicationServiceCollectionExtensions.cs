using ExileLens.Application.Capture;
using ExileLens.Application.Chat;
using ExileLens.Application.Games;
using ExileLens.Application.History;
using ExileLens.Application.Maps;
using ExileLens.Application.Market;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Trade;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddExileLensApplication(this IServiceCollection services, string? dataRoot = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppPaths>(_ => new AppPaths(dataRoot));
        services.AddMemoryCache(o => o.SizeLimit = 512);

        services.AddSingleton<IItemParser, ItemTextParser>();
        services.AddSingleton<IMapModifierAnalyzer, MapModifierAnalyzer>();
        services.AddSingleton<ITradeQueryBuilder, TradeQueryBuilder>();

        services.AddSingleton<IGameRegistry, GameRegistry>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IModifierProfileService, ModifierProfileService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<ILeagueService, LeagueService>();
        services.AddSingleton<ICurrencyConversionService, CurrencyConversionService>();
        services.AddSingleton<PriceCheckService>();
        services.AddSingleton<IPriceEstimationService>(sp => sp.GetRequiredService<PriceCheckService>());
        services.AddSingleton<IItemCaptureService, ItemCaptureService>();
        services.AddSingleton<IGameDetectionService, GameDetectionService>();
        services.AddSingleton<IChatCommandService, ChatCommandService>();
        services.AddSingleton<GameActionCoordinator>();
        return services;
    }
}
