using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>
/// CPU / GPU / memory / fan telemetry plus fan control. One singleton per process.
/// <see cref="InitializeAsync"/> may take a few seconds (kernel driver + sensor discovery) and must not block the UI thread.
/// </summary>
public interface IHardwareMonitor : IDisposable
{
    HardwareMonitorStatus Status { get; }

    /// <summary>Raised when <see cref="Status"/> changes (e.g. after initialization finishes).</summary>
    event EventHandler<HardwareMonitorStatus>? StatusChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes all sensors and returns a fresh snapshot. Returns <see cref="HardwareSnapshot.Empty"/> when not initialized.</summary>
    Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a fan duty cycle (0..100) for the fan with the given <see cref="FanInfo.Id"/>.
    /// Pass <c>null</c> to hand control back to the firmware / automatic curve.
    /// </summary>
    Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default);
}
