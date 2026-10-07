using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>
/// RGB lighting backend (OpenRGB SDK). Connection is lazy: callers invoke <see cref="ConnectAsync"/> and inspect <see cref="Status"/>.
/// All methods must be safe to call when disconnected (they return empty data / no-op and leave a hint in <see cref="RgbStatus.Message"/>).
/// </summary>
public interface IRgbController : IDisposable
{
    RgbStatus Status { get; }

    event EventHandler<RgbStatus>? StatusChanged;

    /// <summary>Changes the server endpoint used by the next <see cref="ConnectAsync"/>.</summary>
    void Configure(string host, int port);

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    /// <param name="refresh">True to re-query the server; false may return a cached list.</param>
    Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Paints every LED of a device one colour (switching to a direct/static mode first when required).</summary>
    Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default);

    Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default);

    /// <summary>Activates a mode and optionally sets its colour / speed / brightness where the mode supports them.</summary>
    Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default);

    /// <summary>Paints every LED of every device one colour.</summary>
    Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default);
}
