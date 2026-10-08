using Pulse.Core.Models;

namespace Pulse.Core.Abstractions;

/// <summary>Placeholder used on platforms without a hardware backend. Reports <see cref="HardwareMonitorState.Unsupported"/>.</summary>
public sealed class NullHardwareMonitor : IHardwareMonitor
{
    public HardwareMonitorStatus Status { get; } = new()
    {
        State = HardwareMonitorState.Unsupported,
        Message = "此平台尚未支援硬體監控。",
    };

    public event EventHandler<HardwareMonitorStatus>? StatusChanged { add { } remove { } }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(HardwareSnapshot.Empty);

    public Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose() { }
}

/// <summary>Placeholder RGB backend that is always disconnected.</summary>
public sealed class NullRgbController : IRgbController
{
    private RgbStatus _status = new() { State = RgbConnectionState.Disconnected, Message = "此平台尚未支援 RGB 控制。" };

    public RgbStatus Status => _status;

    public event EventHandler<RgbStatus>? StatusChanged;

    public void Configure(string host, int port)
    {
        _status = _status with { Host = host, Port = port };
        StatusChanged?.Invoke(this, _status);
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TurnOffAsync(int deviceIndex, CancellationToken ct = default) => Task.CompletedTask;

    public Task TurnOffAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RestoreDefaultAsync(int deviceIndex, CancellationToken ct = default) => Task.CompletedTask;

    public Task RestoreAllDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveCurrentAsDefaultAsync(int deviceIndex, CancellationToken ct = default) => Task.CompletedTask;

    public bool HasDefault(int deviceIndex) => false;

    public void Dispose() { }
}
