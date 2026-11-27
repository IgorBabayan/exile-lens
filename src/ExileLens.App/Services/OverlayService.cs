using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ExileLens.App.ViewModels;
using ExileLens.App.Views;
using ExileLens.Application.Capture;
using ExileLens.Application.Maps;
using ExileLens.Application.Platform;
using ExileLens.Application.Pricing;
using ExileLens.Application.Settings;
using ExileLens.Core.Maps;
using Microsoft.Extensions.DependencyInjection;
using AppPixelPoint = ExileLens.Application.Platform.PixelPoint;

namespace ExileLens.App.Services;

/// <summary>
/// Shows overlays near the cursor (or relative to the game window) without taking keyboard focus from the
/// game. One window instance is reused for all overlay kinds.
/// </summary>
internal sealed class OverlayService(
    IServiceProvider services,
    ISettingsService settings,
    IGameWindowService gameWindows,
    IModifierProfileService profiles,
    IMapModifierAnalyzer analyzer,
    DangerPalette palette) : IOverlayService
{
    private OverlayWindow? _window;
    private IOverlayContent? _content;
    private DispatcherTimer? _focusWatch;
    private DispatcherTimer? _noticeTimer;
    private GameWindowInfo? _gameWindow;
    private AppPixelPoint? _anchor;

    // Set once the user drags the overlay; from then on size changes keep the user's spot instead of snapping
    // back to the item. Reset whenever a new overlay is shown so it opens next to the item again.
    private bool _userPlaced;

    public bool IsVisible { get; private set; }

    public event EventHandler? VisibilityChanged;

    public void ShowPriceCheck(PriceCheckSession session, GameWindowInfo window, AppPixelPoint? cursor) =>
        Dispatcher.UIThread.Post(() =>
        {
            var vm = services.GetRequiredService<PriceCheckOverlayViewModel>();
            vm.Load(session);
            Show(vm, window, cursor);
            vm.Start();
        });

    public void ShowMapAnalysis(MapAnalysisResult result, GameWindowInfo window, AppPixelPoint? cursor) =>
        Dispatcher.UIThread.Post(() => Show(new MapOverlayViewModel(result, profiles, analyzer, palette, settings, Close), window, cursor));

    public void ShowNotice(string title, string message, GameWindowInfo? window, AppPixelPoint? cursor) =>
        Dispatcher.UIThread.Post(() =>
        {
            Show(new NoticeOverlayViewModel(title, message, Close), window, cursor);
            _noticeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) =>
            {
                _noticeTimer!.Stop();
                if (_content is NoticeOverlayViewModel)
                {
                    Close();
                }
            });
            _noticeTimer.Stop();
            _noticeTimer.Start();
        });

    /// <summary>Shows content created by the main window (e.g. a manual price check) as an overlay.</summary>
    public void ShowContent(IOverlayContent content) => Dispatcher.UIThread.Post(() => Show(content, null, null));

    public void Close()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Close);
            return;
        }

        _focusWatch?.Stop();
        _content?.Detach();
        _content = null;
        if (_window is not null)
        {
            _window.OverlayContent = null;
            _window.Hide();
        }

        if (IsVisible)
        {
            IsVisible = false;
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_gameWindow is { } game)
        {
            gameWindows.FocusWindow(game);
        }
    }

    private void Show(IOverlayContent content, GameWindowInfo? gameWindow, AppPixelPoint? cursor)
    {
        _content?.Detach();
        _content = content;
        _gameWindow = gameWindow;
        _anchor = cursor;
        _userPlaced = false;

        var window = EnsureWindow();
        var appearance = settings.Current.Appearance;
        var screen = ScreenFor(window, cursor, gameWindow);
        var maxHeight = screen is null ? 900 : screen.WorkingArea.Height / screen.Scaling * 0.9;
        window.ApplyAppearance(appearance.OverlayScale, appearance.OverlayOpacity, appearance.AnimationsEnabled, maxHeight);
        window.OverlayContent = content;

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            window.Show();
        }

        Position(window, screen);
        window.PlayEnter(appearance.AnimationsEnabled && !wasVisible);

        if (!wasVisible)
        {
            IsVisible = true;
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        if (appearance.CloseOverlayOnGameFocusLoss && gameWindow is not null)
        {
            _focusWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => CheckGameFocus());
            _focusWatch.Start();
        }
    }

    private OverlayWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        _window = new OverlayWindow();
        _window.CloseRequested += (_, _) => Close();
        _window.UserMoved += (_, _) => _userPlaced = true;
        _window.Deactivated += (_, _) =>
        {
            if (settings.Current.Appearance.CloseOverlayOnClickOutside && _content is { IsPinned: false })
            {
                Close();
            }
        };
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.BoundsProperty || !IsVisible)
            {
                return;
            }

            if (_userPlaced)
            {
                KeepOnScreen(_window);
            }
            else
            {
                Position(_window, ScreenFor(_window, _anchor, _gameWindow));
            }
        };
        return _window;
    }

    private void CheckGameFocus()
    {
        if (_window is null || !IsVisible || _content is { IsPinned: true } || _window.IsActive)
        {
            return;
        }

        if (gameWindows.GetActiveGameWindow() is null)
        {
            Close();
        }
    }

    private static Screen? ScreenFor(Window window, AppPixelPoint? cursor, GameWindowInfo? game)
    {
        if (cursor is { } c)
        {
            return window.Screens.ScreenFromPoint(ToAvalonia(c, window));
        }

        if (game is { Bounds.Width: > 0 } g)
        {
            return window.Screens.ScreenFromPoint(new Avalonia.PixelPoint(g.Bounds.X + (g.Bounds.Width / 2), g.Bounds.Y + (g.Bounds.Height / 2)));
        }

        return window.Screens.Primary;
    }

    private static Avalonia.PixelPoint ToAvalonia(AppPixelPoint p, Window window)
    {
        // macOS reports the cursor in points; everything else is already in physical pixels.
        if (OperatingSystem.IsMacOS())
        {
            var scale = window.Screens.Primary?.Scaling ?? 1;
            return new Avalonia.PixelPoint((int)(p.X * scale), (int)(p.Y * scale));
        }

        return new Avalonia.PixelPoint(p.X, p.Y);
    }

    /// <summary>
    /// Keeps a user-placed overlay where it is, only nudging it back inside the working area when it grows
    /// (e.g. listings expanded) past the screen edge.
    /// </summary>
    private static void KeepOnScreen(OverlayWindow window)
    {
        var position = window.Position;
        var probe = window.Screens.ScreenFromPoint(position) ?? window.Screens.Primary;
        if (probe is null)
        {
            return;
        }

        var scaling = probe.Scaling;
        var width = (int)(window.Bounds.Width * scaling);
        var height = (int)(window.Bounds.Height * scaling);
        var work = probe.WorkingArea;
        var x = Math.Clamp(position.X, work.X, Math.Max(work.X, work.Right - width));
        var y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Bottom - height));
        if (x != position.X || y != position.Y)
        {
            window.Position = new Avalonia.PixelPoint(x, y);
        }
    }

    private void Position(OverlayWindow window, Screen? screen)
    {
        if (screen is null)
        {
            return;
        }

        var work = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)(window.Bounds.Width * scaling);
        var height = (int)(window.Bounds.Height * scaling);
        if (width <= 0)
        {
            width = (int)(window.Width * scaling);
        }

        var placement = settings.Current.Appearance.OverlayPlacement;
        Avalonia.PixelPoint target;
        if (placement != OverlayPlacement.NearCursor && _gameWindow is { Bounds.Width: > 0 } g)
        {
            var b = g.Bounds;
            target = placement switch
            {
                OverlayPlacement.LeftOfGame => new Avalonia.PixelPoint(b.X + 24, b.Y + (b.Height - height) / 2),
                OverlayPlacement.RightOfGame => new Avalonia.PixelPoint(b.X + b.Width - width - 24, b.Y + (b.Height - height) / 2),
                _ => new Avalonia.PixelPoint(b.X + (b.Width - width) / 2, b.Y + (b.Height - height) / 2),
            };
        }
        else if (_anchor is { } a)
        {
            var c = ToAvalonia(a, window);
            var gap = (int)(18 * scaling);
            var x = c.X + gap;
            if (x + width > work.Right)
            {
                x = c.X - gap - width; // flip to the left of the cursor near the right edge
            }

            target = new Avalonia.PixelPoint(x, c.Y - (height / 3));
        }
        else
        {
            target = new Avalonia.PixelPoint(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 3);
        }

        var clampedX = Math.Clamp(target.X, work.X, Math.Max(work.X, work.Right - width));
        var clampedY = Math.Clamp(target.Y, work.Y, Math.Max(work.Y, work.Bottom - height));
        var position = new Avalonia.PixelPoint(clampedX, clampedY);
        if (window.Position != position)
        {
            window.Position = position;
        }
    }
}

/// <summary>Implemented by overlay view models.</summary>
public interface IOverlayContent
{
    bool IsPinned { get; }

    /// <summary>
    /// True when the content scrolls its own long sections, so the overlay window must not wrap it in a
    /// page-level scroller (which would put a scrollbar across the whole popup).
    /// </summary>
    bool ManagesOwnScrolling => false;

    /// <summary>Called when the content is replaced or the overlay closes: cancel work, release subscriptions.</summary>
    void Detach();
}
