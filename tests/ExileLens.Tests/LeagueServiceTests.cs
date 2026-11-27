using ExileLens.Application.Games;
using ExileLens.Application.Market;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;
using ExileLens.Core.Trade;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExileLens.Tests;

[TestClass]
public sealed class LeagueServiceTests
{
    [TestMethod]
    public async Task ConfiguredLeagueWinsOverDiscoveredOne()
    {
        using var paths = new TempPaths();
        var (service, settings, _) = await CreateAsync(paths);
        await service.GetLeaguesAsync(Games.Poe2, CancellationToken.None);

        Assert.AreEqual("Current", service.GetActiveLeague(GameId.Poe2), "automatic uses the first trade league");

        settings.Update(s => s.Poe2.League = "Hardcore Current");
        Assert.AreEqual("Hardcore Current", service.GetActiveLeague(GameId.Poe2));
        Assert.AreEqual("Standard", service.GetActiveLeague(GameId.Poe1), "leagues are kept per game");
    }

    [TestMethod]
    public async Task ConcurrentCallersShareOneRequestAndCacheIsReused()
    {
        using var paths = new TempPaths();
        var (service, _, trade) = await CreateAsync(paths);
        trade.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = service.GetLeaguesAsync(Games.Poe2, CancellationToken.None);
        var second = service.GetLeaguesAsync(Games.Poe2, CancellationToken.None);
        trade.Gate.SetResult();
        var lists = await Task.WhenAll(first, second);

        Assert.AreEqual(1, trade.Calls);
        Assert.AreSame(lists[0], lists[1]);
        Assert.AreSame(lists[0], await service.GetLeaguesAsync(Games.Poe2, CancellationToken.None));
        Assert.AreEqual(1, trade.Calls, "cached list is served without a request");
    }

    [TestMethod]
    public async Task FailedRefreshKeepsPreviousList()
    {
        using var paths = new TempPaths();
        var (service, _, trade) = await CreateAsync(paths);
        var initial = await service.GetLeaguesAsync(Games.Poe2, CancellationToken.None);

        trade.Fail = true;
        var refreshed = await service.GetLeaguesAsync(Games.Poe2, CancellationToken.None, forceRefresh: true);

        Assert.AreEqual(2, trade.Calls);
        Assert.AreSame(initial, refreshed);
        Assert.AreEqual("Current", service.GetActiveLeague(GameId.Poe2));
    }

    private static async Task<(LeagueService Service, SettingsService Settings, CountingTradeData Trade)> CreateAsync(TempPaths paths)
    {
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.LoadAsync();
        var trade = new CountingTradeData();
        return (new LeagueService(trade, settings, NullLogger<LeagueService>.Instance), settings, trade);
    }

    private sealed class CountingTradeData : ITradeDataProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource? Gate { get; set; }

        public bool Fail { get; set; }

        public async Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            return [new LeagueInfo("Current", "Current"), new LeagueInfo("Hardcore Current", "Hardcore Current"), new LeagueInfo("Standard", "Standard")];
        }

        public Task<StatIndex?> GetStatIndexAsync(IGameModule game, CancellationToken cancellationToken) => Task.FromResult<StatIndex?>(null);
    }
}
