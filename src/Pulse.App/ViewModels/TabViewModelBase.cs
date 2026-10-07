using System.Windows.Input;
using Pulse.App.Services;

namespace Pulse.App.ViewModels;

/// <summary>
/// Base of the five tab view models. Holds the shared <see cref="MonitoringService"/> and settings so part 2 can
/// bind to <c>Monitoring.LatestBatteries</c>, <c>Monitoring.LatestHardware</c>, <c>Settings.Settings</c> etc.
/// <see cref="OnActivated"/> / <see cref="OnDeactivated"/> are called by <see cref="MainViewModel"/> on tab switches.
/// </summary>
public abstract class TabViewModelBase : ViewModelBase
{
    protected TabViewModelBase(MainTab tab, MonitoringService monitoring, SettingsService settings)
    {
        Tab = tab;
        Monitoring = monitoring;
        Settings = settings;
        Title = MainTabItem.TitleOf(tab);
        IconKey = MainTabItem.IconKeyOf(tab);
    }

    public MainTab Tab { get; }

    /// <summary>Localized tab title (Strings.TabXxx).</summary>
    public string Title { get; }

    /// <summary>Icon resource key (Styles/Icons.axaml).</summary>
    public string IconKey { get; }

    public MonitoringService Monitoring { get; }

    public SettingsService Settings { get; }

    /// <summary>"Run as administrator" (MainViewModel.ElevateCommand); assigned by <see cref="MainViewModel"/> so banners can reuse it.</summary>
    public ICommand? ElevateCommand { get; internal set; }

    /// <summary>Placeholder body text shown by the part-1 views.</summary>
    public abstract string PlaceholderText { get; }

    public bool IsActive { get; private set; }

    internal void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        OnActivated();
    }

    internal void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>Called on the UI thread when the tab becomes visible.</summary>
    protected virtual void OnActivated() { }

    /// <summary>Called on the UI thread when another tab is selected.</summary>
    protected virtual void OnDeactivated() { }
}
