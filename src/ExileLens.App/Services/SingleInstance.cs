using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace ExileLens.App.Services;

/// <summary>
/// Ensures one process per user owns the global shortcuts. Ownership is an exclusive lock on a per-user lock file
/// (LockFileEx on Windows, flock on Linux/macOS), so it is independent of login/terminal sessions and is released by
/// the OS even if the process crashes. The primary instance listens on a per-user named pipe (a Unix domain socket on
/// Linux/macOS) for "show" and "--action" requests from later launches.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string PipeName = $"exile-lens-{Environment.UserName}";
    private readonly FileStream? _lock;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(FileStream? lockStream)
    {
        _lock = lockStream;
    }

    public bool IsPrimary => _lock is not null;

    public event Action<string>? MessageReceived;

    public static SingleInstance TryAcquire()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
                "ExileLens");
            Directory.CreateDirectory(dir);

            // FileShare.None: a second open fails with a sharing violation on Windows and an flock(LOCK_EX) conflict on Unix.
            // The file is intentionally never deleted: unlinking a locked file on Unix would let two processes lock different inodes.
            var stream = new FileStream(Path.Combine(dir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return new SingleInstance(stream);
        }
        catch (IOException)
        {
            return new SingleInstance(null);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstance(null);
        }
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
        if (OperatingSystem.IsWindows())
        {
            // The launching process owns the foreground right now; let the primary take it so its window actually comes
            // to the front instead of just flashing in the taskbar.
            _ = AllowSetForegroundWindow(AsfwAny);
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);

            // Generous timeout: the primary may still be starting up and not listening yet; Connect polls until the pipe appears.
            client.Connect(5000);
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
        _lock?.Dispose();
    }

    private const int AsfwAny = -1;

    // DllImport (not LibraryImport) keeps the App project free of AllowUnsafeBlocks; this is a one-shot cold call.
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
