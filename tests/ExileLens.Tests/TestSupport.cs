using ExileLens.Application.Games;
using ExileLens.Application.Storage;
using ExileLens.Core.Items;
using ExileLens.Game.Poe1;
using ExileLens.Game.Poe2;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace ExileLens.Tests;

internal static class Fixture
{
    public static string Load(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    public static ParsedItem Parse(string relative, GameId game)
    {
        var ok = new ItemTextParser().TryParse(Load(relative), game, out var item, out var error);
        Assert.IsTrue(ok, error);
        return item!;
    }
}

/// <summary>Temporary data directory deleted on dispose.</summary>
internal sealed class TempPaths : IAppPaths, IDisposable
{
    public TempPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "exilelens-tests-" + Guid.NewGuid().ToString("N"));
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Deterministic clock for rate-limit and cache tests.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }
}

internal static class Games
{
    private static readonly TempPaths Paths = new();
    public static readonly IGameModule Poe1 = new Poe1GameModule(Paths);
    public static readonly IGameModule Poe2 = new Poe2GameModule(Paths);
    public static readonly GameRegistry Registry = new([Poe1, Poe2]);
}
