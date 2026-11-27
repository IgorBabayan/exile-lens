using System.IO.Pipes;
using System.Text;

namespace ExileLens.App.Services;

/// <summary>
/// Ensures one process owns the global shortcuts. The primary instance listens on a per-user named pipe
/// (a Unix domain socket on Linux/macOS) for "show" and "--action" requests from later launches.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = $"exile-lens-{Environment.UserName}";
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex? mutex, bool primary)
    {
        _mutex = mutex;
        IsPrimary = primary;
    }

    public bool IsPrimary { get; }

    public event Action<string>? MessageReceived;

    public static SingleInstance TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, $"Local\\{PipeName}", out var created);
        if (!created)
        {
            mutex.Dispose();
            return new SingleInstance(null, false);
        }

        return new SingleInstance(mutex, true);
    }

    public void StartListening()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        MessageReceived?.Invoke(message.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        });
    }

    public static void Send(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(message);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not reach the running Exile Lens instance: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }
    }
}
