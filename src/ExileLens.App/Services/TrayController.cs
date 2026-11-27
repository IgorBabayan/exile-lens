using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ExileLens.App.ViewModels;
using ExileLens.Application.Capture;
using ExileLens.Application.Maps;
using ExileLens.Application.Settings;
using ExileLens.Core.Items;

namespace ExileLens.App.Services;

/// <summary>System tray icon and menu. Closing the main window keeps shortcuts alive; only "Exit" quits.</summary>
internal sealed class TrayController(
    IClassicDesktopStyleApplicationLifetime desktop,
    ISettingsService settings,
    IModifierProfileService profiles,
    IGameDetectionService detection,
    MainWindowViewModel main) : IDisposable
{
    private TrayIcon? _tray;

    public void Install()
    {
        _tray = new TrayIcon
        {
            Icon = App.LoadIcon(),
            ToolTipText = "Exile Lens",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Avalonia.Application.Current!, [_tray]);
        Refresh();

        settings.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        profiles.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);

        if (desktop.MainWindow is { } window)
        {
            window.Closing += (_, e) =>
            {
                if (settings.Current.General.CloseToTray && !_exiting)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            // With "close to tray" off, closing the window exits the app (shortcuts stop with it).
            window.Closed += (_, _) =>
            {
                if (!_exiting)
                {
                    Exit();
                }
            };
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty && window.WindowState == WindowState.Minimized && settings.Current.General.MinimizeToTray)
                {
                    window.Hide();
                }
            };
        }
    }

    private bool _exiting;

    public void Refresh()
    {
        if (_tray is null)
        {
            return;
        }

        var s = settings.Current;
        var menu = new NativeMenu();
        menu.Add(Item("Open Exile Lens", ShowWindow));
        menu.Add(new NativeMenuItemSeparator());

        var gameMenu = new NativeMenu();
        foreach (var (label, value) in new[] { ("Detect automatically", GameSelection.Auto), ("Path of Exile", GameSelection.Poe1), ("Path of Exile 2", GameSelection.Poe2) })
        {
            var item = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = s.General.Game == value };
            item.Click += (_, _) => settings.Update(x => x.General.Game = value);
            gameMenu.Add(item);
        }

        menu.Add(new NativeMenuItem("Active game") { Menu = gameMenu });

        var game = detection.Resolve(null);
        var profileMenu = new NativeMenu();
        try
        {
            var active = profiles.GetActiveProfile(game);
            foreach (var p in profiles.GetProfiles(game))
            {
                var id = p.Id;
                var item = new NativeMenuItem(p.Name) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = p.Id == active.Id };
                item.Click += (_, _) => profiles.SetActiveProfile(game, id);
                profileMenu.Add(item);
            }
        }
        catch (InvalidOperationException)
        {
            // profiles not loaded yet
        }

        menu.Add(new NativeMenuItem($"Modifier profile ({(game == GameId.Poe1 ? "PoE 1" : "PoE 2")})") { Menu = profileMenu });
        menu.Add(new NativeMenuItemSeparator());

        var price = new NativeMenuItem("Price checking") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = s.General.PriceCheckEnabled };
        price.Click += (_, _) => settings.Update(x => x.General.PriceCheckEnabled = !x.General.PriceCheckEnabled);
        menu.Add(price);
        var maps = new NativeMenuItem("Map checking") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = s.General.MapCheckEnabled };
        maps.Click += (_, _) => settings.Update(x => x.General.MapCheckEnabled = !x.General.MapCheckEnabled);
        menu.Add(maps);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Check for updates…", () =>
        {
            ShowWindow();
            main.OpenSettingsAndCheckUpdates();
        }));
        menu.Add(Item("Exit", Exit));
        _tray.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowWindow()
    {
        if (desktop.MainWindow is { } window)
        {
            App.ShowMainWindow(window);
        }
    }

    private void Exit()
    {
        _exiting = true;
        desktop.Shutdown();
    }

    public void Dispose()
    {
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }
    }
}
