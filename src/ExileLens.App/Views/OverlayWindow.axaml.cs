using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using ExileLens.App.Services;

namespace ExileLens.App.Views;

/// <summary>
/// The single, reused overlay window. Content is swapped (price check / map analysis / notice) instead of
/// creating windows, so showing an overlay costs one layout pass rather than a native window creation.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Icon = App.LoadIcon();
        KeyDown += OnKeyDown;
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user starts dragging the overlay to a new spot.</summary>
    public event EventHandler? UserMoved;

    public object? OverlayContent
    {
        get => Host.Content;
        set
        {
            // Content that scrolls its own sections (price check listings) gets the height constraint passed
            // through instead of a page-level scrollbar spanning the whole popup.
            Scroller.VerticalScrollBarVisibility = value is IOverlayContent { ManagesOwnScrolling: true }
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            Host.Content = value;
        }
    }

    public void ApplyAppearance(double scale, double opacity, bool animations, double maxHeight)
    {
        ScaleHost.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? null : new ScaleTransform(scale, scale);
        Width = 452 * scale;
        Card.Opacity = 1;
        Opacity = Math.Clamp(opacity, 0.3, 1);
        Scroller.MaxHeight = Math.Max(240, maxHeight / Math.Max(scale, 0.5));
        Classes.Set("no-anim", !animations);
        if (animations)
        {
            Card.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160) },
            };
        }
        else
        {
            Card.Transitions = null;
        }
    }

    /// <summary>Fade/slide in; callers set the target position before calling.</summary>
    public void PlayEnter(bool animations)
    {
        if (!animations)
        {
            Card.Opacity = 1;
            Card.RenderTransform = null;
            return;
        }

        var transitions = Card.Transitions;
        Card.Transitions = null;
        Card.Opacity = 0;
        Card.RenderTransform = TransformOperations.Parse("translateY(6px)");
        Card.Transitions = transitions;
        Card.Opacity = 1;
        Card.RenderTransform = TransformOperations.Parse("translateY(0px)");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Drag the overlay by its background or by any header marked "drag-handle" (except its buttons).
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsDragSource(e.Source))
        {
            UserMoved?.Invoke(this, EventArgs.Empty);
            BeginMoveDrag(e);
        }
    }

    private static bool IsDragSource(object? source)
    {
        if (source is Border or ScrollViewer or LayoutTransformControl)
        {
            return true;
        }

        // Short walk up to the nearest drag handle; any interactive control on the way cancels the drag.
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case Button or TextBox or ComboBox or Slider:
                    return false;
                case StyledElement element when element.Classes.Contains("drag-handle"):
                    return true;
                case OverlayWindow:
                    return false;
            }
        }

        return false;
    }
}
