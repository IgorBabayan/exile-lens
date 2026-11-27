using Avalonia.Controls;
using ExileLens.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace ExileLens.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = App.LoadIcon();
        var settings = App.Services.GetRequiredService<ISettingsService>();
        ApplyMotion(settings.Current.Appearance.AnimationsEnabled);
        settings.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyMotion(settings.Current.Appearance.AnimationsEnabled));
    }

    private void ApplyMotion(bool enabled)
    {
        Classes.Set("no-anim", !enabled);
        if (this.FindControl<TransitioningContentControl>("Pages") is { } pages)
        {
            pages.PageTransition = enabled ? new Avalonia.Animation.CrossFade(TimeSpan.FromMilliseconds(150)) : null;
        }
    }
}
