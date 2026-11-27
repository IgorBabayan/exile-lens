using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ExileLens.Application.Games;
using ExileLens.Application.History;
using ExileLens.Application.Maps;
using ExileLens.Application.Market;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using ExileLens.Application.Storage;
using ExileLens.Application.Updates;
using ExileLens.Core.Economy;
using ExileLens.Core.Items;
using ExileLens.Core.Maps;
using ExileLens.Core.Trade;
using ExileLens.Infrastructure.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExileLens.Tests;

[TestClass]
public sealed class SettingsTests
{
    [TestMethod]
    public async Task SettingsRoundTripWithAtomicWrite()
    {
        using var paths = new TempPaths();
        var service = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await service.LoadAsync();
        service.Update(s =>
        {
            s.Appearance.Theme = ThemeMode.Dark;
            s.Poe2.League = "Rise of the Abyssal";
            s.Trade.MinimumTradeValue = 2.5m;
        });
        await service.FlushAsync();

        Assert.IsTrue(File.Exists(Path.Combine(paths.DataDirectory, "settings.json")));
        Assert.AreEqual(0, Directory.GetFiles(paths.DataDirectory, "*.tmp").Length, "no temp files left behind");

        var reloaded = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await reloaded.LoadAsync();
        Assert.AreEqual(ThemeMode.Dark, reloaded.Current.Appearance.Theme);
        Assert.AreEqual("Rise of the Abyssal", reloaded.Current.Poe2.League);
        Assert.AreEqual(2.5m, reloaded.Current.Trade.MinimumTradeValue);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, reloaded.Current.SchemaVersion);
    }

    [TestMethod]
    public void UnversionedSettingsAreMigrated()
    {
        var root = JsonNode.Parse("""{ "priceCheckHotkey": "Ctrl+Q", "appearance": { "theme": "Light" } }""")!.AsObject();
        var migrated = SettingsMigrator.Migrate(root, out var changed);

        Assert.IsTrue(changed);
        Assert.AreEqual(1, migrated["schemaVersion"]!.GetValue<int>());
        Assert.IsNull(migrated["priceCheckHotkey"]);
        Assert.AreEqual("Ctrl+Q", migrated["poe1"]!["priceCheckHotkey"]!.GetValue<string>());
        Assert.AreEqual("Ctrl+Q", migrated["poe2"]!["priceCheckHotkey"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CorruptSettingsAreQuarantinedAndDefaultsLoaded()
    {
        using var paths = new TempPaths();
        var file = Path.Combine(paths.DataDirectory, "settings.json");
        await File.WriteAllTextAsync(file, "{ not json");
        var service = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await service.LoadAsync();

        Assert.AreEqual(ThemeMode.System, service.Current.Appearance.Theme);
        Assert.IsFalse(File.Exists(file));
        Assert.AreEqual(1, Directory.GetFiles(paths.DataDirectory, "settings.json.corrupt-*").Length);
    }

    [TestMethod]
    public async Task DebouncedSaverCoalescesBursts()
    {
        var saves = 0;
        await using var saver = new DebouncedSaver(_ => { Interlocked.Increment(ref saves); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < 20; i++)
        {
            saver.Request();
        }

        await saver.FlushAsync();
        await Task.Delay(120);
        Assert.AreEqual(1, saves);
    }

    [TestMethod]
    [DataRow("Ctrl+D", true)]
    [DataRow("Alt + D", true)]
    [DataRow("ctrl+shift+f5", true)]
    [DataRow("Escape", true)]
    [DataRow("Ctrl+", false)]
    [DataRow("Ctrl+D+E", false)]
    [DataRow("Ctrl+F99", false)]
    public void KeyChordParsing(string text, bool valid) =>
        Assert.AreEqual(valid, Application.Platform.KeyChord.TryParse(text, out _));

    [TestMethod]
    public void ShortcutConflictsAreReported()
    {
        var g = new GameSettings { PriceCheckHotkey = "Ctrl+D", MapCheckHotkey = "ctrl + d", CloseOverlayHotkey = "Ctrl+C" };
        var errors = Application.Capture.GameActionCoordinator.Validate(g, out var chords);
        Assert.AreEqual(1, chords.Count);
        StringAssert.Contains(errors[Application.Platform.HotkeyAction.MapCheck], "already used");
        StringAssert.Contains(errors[Application.Platform.HotkeyAction.CloseOverlay], "copy");
    }
}

[TestClass]
public sealed class ProfileServiceTests
{
    private static async Task<(ModifierProfileService Service, SettingsService Settings, TempPaths Paths)> CreateAsync()
    {
        var paths = new TempPaths();
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.LoadAsync();
        var service = new ModifierProfileService(Games.Registry, settings, paths, NullLogger<ModifierProfileService>.Instance);
        await service.LoadAsync();
        return (service, settings, paths);
    }

    [TestMethod]
    public async Task ProfilesSupportFullLifecycleAndPersist()
    {
        var (service, settings, paths) = await CreateAsync();
        using (paths)
        {
            Assert.AreEqual(1, service.GetProfiles(GameId.Poe2).Count, "a default profile exists per game");

            var deadeye = service.Create(GameId.Poe2, "Lightning Arrow Deadeye");
            service.SetLevel(deadeye.Id, "poe2.recovery", DangerLevel.Dangerous);
            var copy = service.Duplicate(deadeye.Id, "Hardcore");
            service.SetLevel(copy.Id, "poe2.recovery", DangerLevel.Warning);
            service.Rename(copy.Id, "HC Deadeye");
            service.SetActiveProfile(GameId.Poe2, copy.Id);

            Assert.AreEqual("HC Deadeye", service.GetActiveProfile(GameId.Poe2).Name);
            Assert.AreEqual(DangerLevel.Dangerous, deadeye.Rules["poe2.recovery"].Level, "duplicate is independent");

            service.SetLevel(copy.Id, "poe2.recovery", DangerLevel.Unclassified);
            Assert.IsFalse(service.GetActiveProfile(GameId.Poe2).Rules.ContainsKey("poe2.recovery"), "reset removes rule");

            await service.FlushAsync();
            await settings.FlushAsync();

            var reloaded = new ModifierProfileService(Games.Registry, settings, paths, NullLogger<ModifierProfileService>.Instance);
            await reloaded.LoadAsync();
            Assert.AreEqual(3, reloaded.GetProfiles(GameId.Poe2).Count);
            Assert.AreEqual("HC Deadeye", reloaded.GetActiveProfile(GameId.Poe2).Name);

            reloaded.Delete(deadeye.Id);
            Assert.AreEqual(2, reloaded.GetProfiles(GameId.Poe2).Count);
        }
    }

    [TestMethod]
    public async Task ExportImportCreatesIndependentCopyWithUniqueName()
    {
        var (service, _, paths) = await CreateAsync();
        using (paths)
        {
            var p = service.Create(GameId.Poe1, "Melee");
            service.SetLevels(p.Id, ["poe1.no-regen", "poe1.max-res"], DangerLevel.Dangerous);
            var json = service.Export(p.Id);
            var imported = service.Import(json);

            Assert.AreNotEqual(p.Id, imported.Id);
            Assert.AreEqual("Melee (2)", imported.Name);
            Assert.AreEqual(2, imported.Rules.Count);
            Assert.AreEqual(GameId.Poe1, imported.Game);
        }
    }

    [TestMethod]
    public async Task LastProfileOfAGameCannotBeDeleted()
    {
        var (service, _, paths) = await CreateAsync();
        using (paths)
        {
            var only = service.GetProfiles(GameId.Poe1).Single();
            Assert.ThrowsExactly<InvalidOperationException>(() => service.Delete(only.Id));
        }
    }

    [TestMethod]
    public async Task DiscoveredModifiersBecomeClassifiable()
    {
        var (service, _, paths) = await CreateAsync();
        using (paths)
        {
            var mod = new ItemModifier("Monsters are Spooky", "Monsters are Spooky", [], ModifierKind.Explicit, 0);
            service.RecordDiscovered(GameId.Poe2, [mod]);
            var all = service.GetAllModifiers(GameId.Poe2);
            Assert.IsTrue(all.Any(m => m.Id == MapModifierCatalog.AutoId("Monsters are Spooky")));
            Assert.IsFalse(service.GetAllModifiers(GameId.Poe1).Any(m => m.Pattern == "Monsters are Spooky"), "games are separate");
        }
    }
}

[TestClass]
public sealed class HistoryTests
{
    [TestMethod]
    public async Task HistoryIsBoundedNewestFirstAndClearable()
    {
        using var paths = new TempPaths();
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.LoadAsync();
        settings.Update(s => s.History.MaxPriceChecks = 3);
        await using var history = new HistoryService(settings, paths, NullLogger<HistoryService>.Instance);

        for (var i = 0; i < 5; i++)
        {
            history.Add(new PriceCheckHistoryEntry { ItemName = $"Item {i}", Timestamp = DateTimeOffset.UtcNow });
        }

        Assert.AreEqual(3, history.PriceChecks.Count);
        Assert.AreEqual("Item 4", history.PriceChecks[0].ItemName);
        history.Clear();
        Assert.AreEqual(0, history.PriceChecks.Count);
    }
}

[TestClass]
public sealed class RateLimiterTests
{
    private static HttpResponseHeaders Headers(string rules, params (string Name, string Value)[] values)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("X-Rate-Limit-Policy", "trade-search-request-limit");
        response.Headers.Add("X-Rate-Limit-Rules", rules);
        foreach (var (n, v) in values)
        {
            response.Headers.Add(n, v);
        }

        return response.Headers;
    }

    [TestMethod]
    public async Task RespectsServerRulesWithHeadroom()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new RateLimiter(time);
        limiter.Observe("search", Headers("Ip", ("X-Rate-Limit-Ip", "3:10:60"), ("X-Rate-Limit-Ip-State", "0:10:0")), 200);

        await limiter.AcquireAsync("search", CancellationToken.None);
        await limiter.AcquireAsync("search", CancellationToken.None);

        // Third request would hit 3/3 (no headroom) → must wait ~10 s, which exceeds MaxWait → fail fast.
        var ex = await Assert.ThrowsExactlyAsync<TradeRateLimitedException>(() => limiter.AcquireAsync("search", CancellationToken.None));
        Assert.AreEqual("trade-search-request-limit", ex.Policy);

        time.Advance(TimeSpan.FromSeconds(11));
        await limiter.AcquireAsync("search", CancellationToken.None);
    }

    [TestMethod]
    public async Task TooManyRequestsBlocksUntilRetryAfter()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new RateLimiter(time);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        limiter.Observe("fetch", response.Headers, 429);

        Assert.AreEqual(TimeSpan.FromSeconds(60), limiter.RemainingBlock("fetch"));
        await Assert.ThrowsExactlyAsync<TradeRateLimitedException>(() => limiter.AcquireAsync("fetch", CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(61));
        await limiter.AcquireAsync("fetch", CancellationToken.None);
    }

    [TestMethod]
    public async Task ServerRestrictionStateIsHonoured()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var limiter = new RateLimiter(time);
        limiter.Observe("search", Headers("Ip", ("X-Rate-Limit-Ip", "8:10:60"), ("X-Rate-Limit-Ip-State", "9:10:60")), 200);
        await Assert.ThrowsExactlyAsync<TradeRateLimitedException>(() => limiter.AcquireAsync("search", CancellationToken.None));
    }
}

[TestClass]
public sealed class PriceCheckServiceTests
{
    private sealed class FakeTrade : ITradeClient
    {
        public int Searches;
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TradeSearchResult> SearchAsync(IGameModule game, string league, JsonObject query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Searches);
            await Gate.Task.WaitAsync(cancellationToken);
            return new TradeSearchResult("q1", 12, Enumerable.Range(0, 12).Select(i => $"id{i}").ToList());
        }

        public Task<IReadOnlyList<TradeListing>> FetchAsync(IGameModule game, string queryId, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TradeListing>>(ids.Select((id, i) => new TradeListing(id, 10 + i, "exalted", $"acc{id}", true, null, null)).ToList());

        public string BuildSearchUrl(IGameModule game, string league, string queryId) => $"url/{queryId}";
        public string BuildSearchUrl(IGameModule game, string league, JsonObject query) => "url?q";
    }

    private sealed class FakeTradeData : ITradeDataProvider
    {
        public Task<IReadOnlyList<LeagueInfo>> GetLeaguesAsync(IGameModule game, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LeagueInfo>>([new LeagueInfo("Test", "Test")]);

        public Task<StatIndex?> GetStatIndexAsync(IGameModule game, CancellationToken cancellationToken) =>
            Task.FromResult<StatIndex?>(new StatIndex([new TradeStat("explicit.stat_life", "+# to maximum Life", "explicit")]));

        public Task<IReadOnlyDictionary<string, string[]>> GetUniquesByBaseAsync(IGameModule game, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string[]>>(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Imperial Bow"] = ["Doomfletch", "Lioneye's Glare"],
            });
    }

    private sealed class FakeMarket : IMarketDataProvider
    {
        public string Name => "fake";

        public Task<CurrencyRates> GetCurrencyRatesAsync(IGameModule game, string league, CancellationToken cancellationToken) =>
            Task.FromResult(new CurrencyRates("Exalted Orb", [new("Regal Orb", 1m), new("exalted", 1m)], DateTimeOffset.UtcNow, "fake"));

        public Task<PriceEstimate?> GetBulkPriceAsync(IGameModule game, string league, ParsedItem item, string valuationCurrency, CancellationToken cancellationToken) =>
            Task.FromResult<PriceEstimate?>(new PriceEstimate(valuationCurrency, 1, 1, 1, 1, 0, 0, EstimateConfidence.High, DateTimeOffset.UtcNow, false, "fake"));
    }

    private static async Task<(PriceCheckService Service, FakeTrade Trade, TempPaths Paths)> CreateAsync()
    {
        var paths = new TempPaths();
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.LoadAsync();
        var trade = new FakeTrade();
        var market = new FakeMarket();
        var rates = new CurrencyConversionService(market, TimeProvider.System, NullLogger<CurrencyConversionService>.Instance);
        var service = new PriceCheckService(trade, new FakeTradeData(), market, rates, new TradeQueryBuilder(), settings,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }), TimeProvider.System, NullLogger<PriceCheckService>.Instance);
        await service.PreloadAsync(Games.Poe2, CancellationToken.None);
        return (service, trade, paths);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalRequestsShareOneSearchAndThenHitCache()
    {
        var (service, trade, paths) = await CreateAsync();
        using (paths)
        {
            var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
            var session = service.CreateSession(item, Games.Poe2, "Test");
            Assert.IsTrue(session.Filters.Any(f => f.StatId == "explicit.stat_life"), "stat index is used for default filters");

            var a = service.RunAsync(session, CancellationToken.None);
            var b = service.RunAsync(session, CancellationToken.None);
            trade.Gate.SetResult();
            var results = await Task.WhenAll(a, b);

            Assert.AreEqual(1, trade.Searches);
            Assert.AreEqual(12, results[0].Estimate.MatchingListings);
            Assert.IsNotNull(results[0].Rewards.Disenchant);
            Assert.AreEqual(Core.Rewards.SuggestedAction.SellOnTrade, results[0].Rewards.Comparison!.Action);

            var cached = await service.RunAsync(session, CancellationToken.None);
            Assert.IsTrue(cached.FromCache);
            Assert.AreEqual(1, trade.Searches);
        }
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotPoisonSharedRequest()
    {
        var (service, trade, paths) = await CreateAsync();
        using (paths)
        {
            var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);
            var session = service.CreateSession(item, Games.Poe2, "Test");
            using var cts = new CancellationTokenSource();
            var cancelled = service.RunAsync(session, cts.Token);
            var survivor = service.RunAsync(session, CancellationToken.None);
            await cts.CancelAsync();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);

            trade.Gate.SetResult();
            var result = await survivor;
            Assert.IsTrue(result.Estimate.HasValue);
        }
    }

    [TestMethod]
    public async Task UnidentifiedUniqueOffersCandidatesAndPricesTheChosenOne()
    {
        var (service, _, paths) = await CreateAsync();
        using (paths)
        {
            var item = Fixture.Parse("poe1/unidentified-unique-bow.txt", GameId.Poe1);
            var session = service.CreateSession(item, Games.Poe1, "Test");
            Assert.IsTrue(service.NeedsUniqueChoice(session));
            Assert.IsNull(service.PeekUniqueCandidates(session), "not loaded for this game yet: peeking never blocks");

            var candidates = await service.GetUniqueCandidatesAsync(session, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "Doomfletch", "Lioneye's Glare" }, candidates.ToArray());
            Assert.AreSame(candidates, service.PeekUniqueCandidates(session), "later checks reuse the loaded list");

            var chosen = service.ChooseUnique(session, "Lioneye's Glare");
            Assert.IsFalse(service.NeedsUniqueChoice(chosen));
            Assert.AreEqual("Lioneye's Glare", chosen.Item.Name);
            Assert.AreEqual("Test", chosen.League);
            StringAssert.Contains(service.BuildTradeUrl(chosen), "url");
        }
    }

    [TestMethod]
    public async Task BulkItemsUseAggregatedPricing()
    {
        var (service, trade, paths) = await CreateAsync();
        using (paths)
        {
            var item = Fixture.Parse("poe2/exalted-orb.txt", GameId.Poe2);
            var session = service.CreateSession(item, Games.Poe2, "Test");
            Assert.IsTrue(session.UsesBulkPricing);
            var result = await service.RunAsync(session, CancellationToken.None);
            Assert.AreEqual(0, trade.Searches);
            Assert.IsFalse(result.Estimate.IsListingPrice);
        }
    }
}

[TestClass]
public sealed class UpdateTests
{
    [TestMethod]
    [DataRow("1.2.3", "1.2.4", -1)]
    [DataRow("v1.10.0", "1.9.9", 1)]
    [DataRow("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [DataRow("1.0.0-rc.1", "1.0.0", -1)]
    [DataRow("2.0", "2.0.0", 0)]
    public void SemanticVersionPrecedence(string a, string b, int expected)
    {
        Assert.IsTrue(SemanticVersion.TryParse(a, out var va));
        Assert.IsTrue(SemanticVersion.TryParse(b, out var vb));
        Assert.AreEqual(expected, Math.Sign(va.CompareTo(vb)));
    }

    [TestMethod]
    public void PlatformAssetSelectionAndChecksums()
    {
        UpdateAsset[] assets =
        [
            new("ExileLens-1.2.0-win-x64.zip", "u1", 1),
            new("ExileLens-1.2.0-win-x64-setup.exe", "u2", 1),
            new("ExileLens-1.2.0-linux-x64.AppImage", "u3", 1),
        ];
        Assert.AreEqual("u2", PlatformAssetMatcher.Select(assets, "win-x64", ["-setup.exe", ".zip"])!.DownloadUrl);
        Assert.IsNull(PlatformAssetMatcher.Select(assets, "osx-arm64", [".dmg"]));

        const string sums = "abc123  ExileLens-1.2.0-win-x64-setup.exe\nDEF456 *ExileLens-1.2.0-linux-x64.AppImage\n";
        Assert.AreEqual("abc123", PlatformAssetMatcher.FindChecksum(sums, "ExileLens-1.2.0-win-x64-setup.exe"));
        Assert.AreEqual("def456", PlatformAssetMatcher.FindChecksum(sums, "ExileLens-1.2.0-linux-x64.AppImage"));
    }

    [TestMethod]
    public void GameWindowMatching()
    {
        Assert.AreEqual(GameId.Poe2, Application.Platform.GameWindowMatcher.Match(null, "PathOfExile2_x64Steam.exe"));
        Assert.AreEqual(GameId.Poe1, Application.Platform.GameWindowMatcher.Match(null, "PathOfExile_x64Steam.exe"));
        Assert.AreEqual(GameId.Poe2, Application.Platform.GameWindowMatcher.Match(null, "steam_app_2694490"));
        Assert.AreEqual(GameId.Poe1, Application.Platform.GameWindowMatcher.Match("Path of Exile", "wine64-preloader"));
        Assert.IsNull(Application.Platform.GameWindowMatcher.Match("Path of Exile 2 - Wiki - Firefox", "firefox"));
    }
}
