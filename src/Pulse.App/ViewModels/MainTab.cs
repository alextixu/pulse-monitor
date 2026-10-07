using Pulse.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pulse.App.ViewModels;

public enum MainTab
{
    Devices,
    System,
    Lighting,
    Fans,
    Settings,
}

/// <summary>One pill of the segmented tab control. <see cref="IsSelected"/> is two-way bound to the RadioButton.</summary>
public sealed partial class MainTabItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public MainTabItem(MainTab tab, string title, string iconKey)
    {
        Tab = tab;
        Title = title;
        IconKey = iconKey;
    }

    public MainTab Tab { get; }

    /// <summary>Localized tab title (Strings.TabXxx).</summary>
    public string Title { get; }

    /// <summary>Resource key of the StreamGeometry in Styles/Icons.axaml, e.g. "IconMouse".</summary>
    public string IconKey { get; }

    /// <summary>Lower-case English name used for screenshot file names and logging.</summary>
    public string Key => Tab.ToString().ToLowerInvariant();

    public static string TitleOf(MainTab tab) => tab switch
    {
        MainTab.Devices => Strings.TabDevices,
        MainTab.System => Strings.TabSystem,
        MainTab.Lighting => Strings.TabLighting,
        MainTab.Fans => Strings.TabFans,
        MainTab.Settings => Strings.TabSettings,
        _ => tab.ToString(),
    };

    public static string IconKeyOf(MainTab tab) => tab switch
    {
        MainTab.Devices => "IconDevice",
        MainTab.System => "IconCpu",
        MainTab.Lighting => "IconLightbulb",
        MainTab.Fans => "IconFan",
        MainTab.Settings => "IconSettings",
        _ => "IconDevice",
    };
}
