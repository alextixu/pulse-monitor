using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>
/// Source of battery information for one family of devices (Bluetooth, Logitech HID++, the system battery, ...).
/// Implementations must be safe to call repeatedly from a background timer and must never throw for
/// "no devices" – return an empty list instead. Per-device failures should be logged and skipped.
/// </summary>
public interface IBatteryProvider
{
    /// <summary>Short display name, also used as <see cref="BatteryDevice.Source"/>.</summary>
    string Name { get; }

    /// <summary>False on platforms where this provider cannot work (the app then never calls it).</summary>
    bool IsSupported { get; }

    Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
}
