using ExileLens.Application.Market;
using ExileLens.Application.Updates;
using ExileLens.Infrastructure.Http;
using ExileLens.Infrastructure.Market;
using ExileLens.Infrastructure.Trade;
using ExileLens.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddExileLensInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(_ => HttpClientFactory.Create());
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<ITradeClient, OfficialTradeClient>();
        services.AddSingleton<ITradeDataProvider, TradeDataProvider>();
        services.AddSingleton<IMarketDataProvider, PoeNinjaMarketDataProvider>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        return services;
    }
}
