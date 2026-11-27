using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExileLens.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }

    /// <summary>Called once after profiles/history have loaded.</summary>
    public virtual void OnServicesReady()
    {
    }
}

public sealed class NavItem(string title, string iconKey, PageViewModel page)
{
    public string Title => title;
    public PageViewModel Page => page;
    public Geometry? Icon => Avalonia.Application.Current is { } app && app.TryGetResource(iconKey, app.ActualThemeVariant, out var g) ? g as Geometry : null;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsPage;

    public MainWindowViewModel(
        OverviewViewModel overview,
        PriceCheckPageViewModel priceCheck,
        MapModifiersViewModel mapModifiers,
        DisenchantmentViewModel disenchantment,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        _settingsPage = settings;
        NavItems =
        [
            new NavItem("Overview", "IconOverview", overview),
            new NavItem("Price Check", "IconSearch", priceCheck),
            new NavItem("Map Modifiers", "IconMap", mapModifiers),
            new NavItem("Disenchantment", "IconSparkle", disenchantment),
            new NavItem("History", "IconHistory", history),
            new NavItem("Settings", "IconSettings", settings),
        ];
        _selected = NavItems[0];
        overview.OnActivated();
    }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem _selected;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public PageViewModel CurrentPage => Selected.Page;

    public double SidebarWidth => IsSidebarCollapsed ? 60 : 220;

    partial void OnSelectedChanging(NavItem value) => Selected?.Page.OnDeactivated();

    partial void OnSelectedChanged(NavItem value)
    {
        value.Page.OnActivated();
        OnPropertyChanged(nameof(CurrentPage));
    }

    partial void OnIsSidebarCollapsedChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    public void OnServicesReady()
    {
        foreach (var item in NavItems)
        {
            item.Page.OnServicesReady();
        }

        CurrentPage.OnActivated();
    }

    public void OpenSettingsAndCheckUpdates()
    {
        Selected = NavItems[^1];
        _settingsPage.CheckUpdatesCommand.Execute(null);
    }
}
