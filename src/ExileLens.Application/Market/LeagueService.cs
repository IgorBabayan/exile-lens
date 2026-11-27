using System.Collections.Concurrent;
using ExileLens.Application.Games;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Market;

public interface ILeagueService
{
    /// <summary>Configured league, or the first non-event trade league discovered, or "Standard".</summary>
    string GetActiveLeague(GameId game);

    Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken);
}

public sealed class LeagueService(ITradeDataProvider tradeData, ISettingsService settings, ILogger<LeagueService> logger) : ILeagueService
{
    private readonly ConcurrentDictionary<GameId, IReadOnlyList<LeagueInfo>> _leagues = new();

    public string GetActiveLeague(GameId game)
    {
        var configured = settings.Current.For(game).League;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (_leagues.TryGetValue(game, out var list) && list.Count > 0)
        {
            return list[0].Id;
        }

        return "Standard";
    }

    public async Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken)
    {
        if (_leagues.TryGetValue(game.Game, out var cached))
        {
            return cached;
        }

        try
        {
            var leagues = await tradeData.GetLeaguesAsync(game, cancellationToken).ConfigureAwait(false);
            if (leagues.Count > 0)
            {
                _leagues[game.Game] = leagues;
            }

            return leagues;
        }
        catch (Exception ex) when (ex is HttpRequestException or MarketUnavailableException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "League discovery failed for {Game}", game.Game);
            return [];
        }
    }
}
