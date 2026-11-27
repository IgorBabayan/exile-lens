using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;

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

    public object? OverlayContent
    {
        get => Host.Content;
        set => Host.Content = value;
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

        // Drag the overlay by its background (useful when pinned).
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Border or ScrollViewer or LayoutTransformControl)
        {
            BeginMoveDrag(e);
        }
    }
}
