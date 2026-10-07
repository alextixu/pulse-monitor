using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Pulse.Core.Models;

namespace Pulse.App.Converters;

/// <summary><see cref="DeviceKind"/> → icon geometry from Styles/Icons.axaml (IconMouse, IconKeyboard, …; Unknown/Other → IconDevice).</summary>
public sealed class DeviceKindToIconConverter : IValueConverter
{
    public static readonly DeviceKindToIconConverter Instance = new();

    public static string KeyOf(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "IconMouse",
        DeviceKind.Keyboard => "IconKeyboard",
        DeviceKind.Headset => "IconHeadset",
        DeviceKind.Speaker => "IconSpeaker",
        DeviceKind.Controller => "IconController",
        DeviceKind.Phone => "IconPhone",
        DeviceKind.Tablet => "IconTablet",
        DeviceKind.Laptop => "IconLaptop",
        DeviceKind.Watch => "IconWatch",
        DeviceKind.Stylus => "IconStylus",
        DeviceKind.Trackpad => "IconTrackpad",
        DeviceKind.Presenter => "IconPresenter",
        DeviceKind.Receiver => "IconReceiver",
        _ => "IconDevice",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ResourceLookup.Icon(KeyOf(value is DeviceKind kind ? kind : DeviceKind.Unknown));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="ConnectionType"/> → icon geometry (Bluetooth/BluetoothLE → IconBluetooth, UsbReceiver → IconReceiver, Usb → IconUsb, Internal → IconBattery, Unknown → IconPlug).</summary>
public sealed class ConnectionTypeToIconConverter : IValueConverter
{
    public static readonly ConnectionTypeToIconConverter Instance = new();

    public static string KeyOf(ConnectionType type) => type switch
    {
        ConnectionType.Bluetooth or ConnectionType.BluetoothLE => "IconBluetooth",
        ConnectionType.UsbReceiver => "IconReceiver",
        ConnectionType.Usb => "IconUsb",
        ConnectionType.Internal => "IconBattery",
        _ => "IconPlug",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ResourceLookup.Icon(KeyOf(value is ConnectionType type ? type : ConnectionType.Unknown));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resource key string ("IconFan") → icon geometry; unknown keys → IconDevice.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is string key ? ResourceLookup.Icon(key) : null) ?? ResourceLookup.Icon("IconDevice");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>OpenRGB device type string ("Motherboard", "Mouse", "Keyboard", "DRAM", "GPU", "Cooler", "LED Strip", …) → icon geometry.</summary>
public sealed class RgbDeviceTypeToIconConverter : IValueConverter
{
    public static readonly RgbDeviceTypeToIconConverter Instance = new();

    public static string KeyOf(string? type)
    {
        var t = (type ?? string.Empty).ToLowerInvariant();
        if (t.Contains("mouse")) return "IconMouse";
        if (t.Contains("keyboard")) return "IconKeyboard";
        if (t.Contains("headset")) return "IconHeadset";
        if (t.Contains("gpu")) return "IconGpu";
        if (t.Contains("dram") || t.Contains("memory")) return "IconMemory";
        if (t.Contains("cooler") || t.Contains("fan")) return "IconFan";
        if (t.Contains("motherboard")) return "IconCpu";
        if (t.Contains("speaker")) return "IconSpeaker";
        if (t.Contains("mousemat") || t.Contains("mat")) return "IconTrackpad";
        return "IconLightbulb";
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ResourceLookup.Icon(KeyOf(value as string));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
