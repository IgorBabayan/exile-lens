using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ExileLens.App.Services;

/// <summary>
/// Minimal rolling file logger (warnings and above by default) written by a single background task.
/// Messages never contain clipboard text or credentials; callers only log identifiers and status codes.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 1024);
    private readonly Task _writer;
    private readonly string _path;

    public FileLoggerProvider()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "ExileLens", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "exile-lens.log");
        RollIfLarge();
        _writer = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            writer.WriteLine(line);
        }
    }

    private void RollIfLarge()
    {
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > 2 * 1024 * 1024)
        {
            File.Move(_path, _path + ".1", overwrite: true);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Wait(TimeSpan.FromSeconds(1));
        _queue.Dispose();
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            owner.Enqueue(line);
        }
    }
}
