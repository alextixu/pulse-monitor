namespace Pulse.Core.Models;

/// <summary>What kind of peripheral/device a battery belongs to. Drives the icon shown in the UI.</summary>
public enum DeviceKind
{
    Unknown,
    Mouse,
    Keyboard,
    Headset,
    Speaker,
    Controller,
    Phone,
    Tablet,
    Laptop,
    Watch,
    Stylus,
    Trackpad,
    Presenter,
    Receiver,
    Other,
}

/// <summary>How the device is attached to this computer.</summary>
public enum ConnectionType
{
    Unknown,
    Bluetooth,
    BluetoothLE,
    /// <summary>Logitech Unifying / LIGHTSPEED / Bolt style USB receiver.</summary>
    UsbReceiver,
    Usb,
    /// <summary>The computer's own battery (laptop/tablet).</summary>
    Internal,
}

public enum BatteryStatus
{
    Unknown,
    Discharging,
    Charging,
    Full,
    Low,
    Critical,
    /// <summary>Plugged in but not charging (e.g. charge limit reached).</summary>
    NotCharging,
}

/// <summary>A single device that reports a battery level. Immutable snapshot; providers create a fresh instance per poll.</summary>
public sealed record BatteryDevice
{
    /// <summary>Stable unique id across polls, e.g. "bt:AA:BB:CC:DD:EE:FF", "hidpp:C54D-318139673433:1", "system:0".</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;

    public ConnectionType Connection { get; init; } = ConnectionType.Unknown;

    /// <summary>Name of the provider that produced this entry (e.g. "Bluetooth", "Logitech", "System").</summary>
    public required string Source { get; init; }

    /// <summary>0..100, or null when the device is known but its level is currently unavailable.</summary>
    public int? Percent { get; init; }

    public BatteryStatus Status { get; init; } = BatteryStatus.Unknown;

    /// <summary>False for remembered-but-disconnected devices. The UI greys these out.</summary>
    public bool IsConnected { get; init; } = true;

    public double? VoltageMillivolts { get; init; }

    /// <summary>Short secondary text, e.g. "LIGHTSPEED", "Bluetooth LE", model or firmware.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
