using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ExileLens.Application.Platform;

namespace ExileLens.App.Services;

/// <summary>Cross-platform clipboard (Avalonia 12 ClipboardExtensions) through Avalonia (Win32, X11/XWayland, macOS pasteboard). Always used on the UI thread.</summary>
internal sealed class AvaloniaClipboardService : IClipboardService
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    private IClipboard Clipboard => _topLevel?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available yet.");

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync<string?>(async () => await Clipboard.TryGetTextAsync()).WaitAsync(cancellationToken);

    public Task SetTextAsync(string? text, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(async () => await Clipboard.SetTextAsync(text)).WaitAsync(cancellationToken);
}
