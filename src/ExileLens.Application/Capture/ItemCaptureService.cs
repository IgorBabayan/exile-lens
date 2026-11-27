using ExileLens.Application.Platform;
using Microsoft.Extensions.Logging;

namespace ExileLens.Application.Capture;

public interface IItemCaptureService
{
    /// <summary>
    /// Sends one copy keystroke to the game and returns the item text from the clipboard, or null on timeout.
    /// Restores the user's previous clipboard text afterwards when it is safe to do so.
    /// </summary>
    Task<string?> CaptureAsync(bool advanced, CancellationToken cancellationToken);
}

public sealed class ItemCaptureService(
    IClipboardService clipboard,
    IKeyboardInputService keyboard,
    TimeProvider time,
    ILogger<ItemCaptureService> logger) : IItemCaptureService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(600);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> CaptureAsync(bool advanced, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
            await keyboard.SendCopyAsync(advanced, cancellationToken).ConfigureAwait(false);

            var started = time.GetTimestamp();
            string? captured = null;
            while (time.GetElapsedTime(started) < Timeout)
            {
                await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
                var current = await clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (current is not null && !string.Equals(current, previous, StringComparison.Ordinal) && LooksLikeItem(current))
                {
                    captured = current;
                    break;
                }
            }

            // The same item copied twice leaves the clipboard unchanged; accept it if it is item text.
            if (captured is null && previous is not null && LooksLikeItem(previous))
            {
                captured = previous;
            }

            if (captured is null)
            {
                logger.LogDebug("Item capture timed out after {Timeout} ms", Timeout.TotalMilliseconds);
                return null;
            }

            if (previous is not null && !LooksLikeItem(previous))
            {
                _ = RestoreAsync(previous, captured);
            }

            return captured;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restores the user's clipboard only if it still holds the text we captured (never clobbers newer user copies).</summary>
    private async Task RestoreAsync(string previous, string captured)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), time).ConfigureAwait(false);
            var now = await clipboard.GetTextAsync().ConfigureAwait(false);
            if (string.Equals(now, captured, StringComparison.Ordinal))
            {
                await clipboard.SetTextAsync(previous).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never log clipboard contents.
            logger.LogDebug(ex, "Clipboard restore skipped");
        }
    }

    public void Dispose() => _gate.Dispose();

    public static bool LooksLikeItem(string text) =>
        text.StartsWith("Item Class:", StringComparison.Ordinal)
        || (text.StartsWith("Rarity:", StringComparison.Ordinal) && text.Contains("--------", StringComparison.Ordinal));
}
