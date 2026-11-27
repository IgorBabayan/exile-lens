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

    /// <summary>
    /// Returns the cached trade league list for <paramref name="game"/>. Concurrent callers share a single request;
    /// <paramref name="forceRefresh"/> re-downloads the list and keeps the previous one if the request fails.
    /// </summary>
    Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken, bool forceRefresh = false);
}

public sealed class LeagueService(ITradeDataProvider tradeData, ISettingsService settings, ILogger<LeagueService> logger) : ILeagueService
{
    private readonly ConcurrentDictionary<GameId, IReadOnlyList<LeagueInfo>> _leagues = new();
    private readonly ConcurrentDictionary<GameId, Task<IReadOnlyList<LeagueInfo>>> _completed = new();
    private readonly ConcurrentDictionary<GameId, Lazy<Task<IReadOnlyList<LeagueInfo>>>> _inflight = new();

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

    public Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        // Completed tasks are cached so repeated lookups (settings page activation, overview refresh) allocate nothing.
        if (!forceRefresh && _completed.TryGetValue(game.Game, out var cached))
        {
            return cached;
        }

        var fetch = _inflight.GetOrAdd(game.Game, _ => new Lazy<Task<IReadOnlyList<LeagueInfo>>>(() => FetchAsync(game))).Value;
        return cancellationToken.CanBeCanceled ? fetch.WaitAsync(cancellationToken) : fetch;
    }

    private async Task<IReadOnlyList<LeagueInfo>> FetchAsync(IGameModule game)
    {
        try
        {
            // The request is shared between callers, so one caller cancelling must not abort it for the others.
            var leagues = await tradeData.GetLeaguesAsync(game, CancellationToken.None).ConfigureAwait(false);
            if (leagues.Count > 0)
            {
                _leagues[game.Game] = leagues;
                _completed[game.Game] = Task.FromResult(leagues);
                return leagues;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or MarketUnavailableException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "League discovery failed for {Game}", game.Game);
        }
        finally
        {
            _inflight.TryRemove(game.Game, out _);
        }

        // Keep the last good list rather than wiping the selector after a failed refresh.
        return _leagues.TryGetValue(game.Game, out var previous) ? previous : [];
    }
}
