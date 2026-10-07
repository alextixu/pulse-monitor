using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>"--demo": eight devices covering every visual state (charging, full, low, critical, disconnected, sleeping, unknown level).</summary>
public sealed class DemoBatteryProvider : IBatteryProvider
{
    private readonly ILogger<DemoBatteryProvider> _log;
    private readonly object _gate = new();
    private readonly List<DemoDevice> _devices;
    private int _polls;

    public DemoBatteryProvider(ILogger<DemoBatteryProvider> log)
    {
        _log = log;
        _devices = new List<DemoDevice>
        {
            new("demo:mouse", "G PRO X SUPERLIGHT 2", DeviceKind.Mouse, ConnectionType.UsbReceiver, 87, BatteryStatus.Discharging, true, "LIGHTSPEED"),
            new("demo:keyboard", "MX Keys S", DeviceKind.Keyboard, ConnectionType.UsbReceiver, 42, BatteryStatus.Discharging, true, "Bolt"),
            new("demo:controller", "Xbox Wireless Controller", DeviceKind.Controller, ConnectionType.BluetoothLE, 75, BatteryStatus.Discharging, true, "藍牙 LE"),
            new("demo:headset", "G735 Headset", DeviceKind.Headset, ConnectionType.UsbReceiver, 18, BatteryStatus.Low, true, "LIGHTSPEED"),
            new("demo:phone", "iPhone 15 Pro", DeviceKind.Phone, ConnectionType.Bluetooth, 55, BatteryStatus.Charging, true, "藍牙"),
            new("demo:stylus", "Surface Slim Pen 2", DeviceKind.Stylus, ConnectionType.BluetoothLE, 7, BatteryStatus.Critical, true, "藍牙 LE"),
            new("demo:speaker", "Soundcore Motion+", DeviceKind.Speaker, ConnectionType.Bluetooth, 100, BatteryStatus.Full, true, "藍牙"),
            new("demo:presenter", "Spotlight 簡報器", DeviceKind.Presenter, ConnectionType.UsbReceiver, 63, BatteryStatus.Unknown, false, "未連線"),
            new("demo:receiver", "LIGHTSPEED 接收器", DeviceKind.Receiver, ConnectionType.Usb, null, BatteryStatus.Unknown, true, "USB"),
        };
    }

    public string Name => "Demo";

    public bool IsSupported => true;

    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _polls++;
            var now = DateTimeOffset.Now;
            var result = new List<BatteryDevice>(_devices.Count);
            foreach (var d in _devices)
            {
                // Slow drift so the UI visibly changes between polls.
                if (d.Percent is { } p && d.IsConnected && _polls % 3 == 0)
                {
                    var delta = d.Status == BatteryStatus.Charging ? 1 : -1;
                    var next = Math.Clamp(p + delta, 3, 100);
                    if (d.Status == BatteryStatus.Charging && next == 100) d.Status = BatteryStatus.Full;
                    d.Percent = next;
                }

                result.Add(new BatteryDevice
                {
                    Id = d.Id,
                    Name = d.Name,
                    Kind = d.Kind,
                    Connection = d.Connection,
                    Source = Name,
                    Percent = d.Percent,
                    Status = d.Status,
                    IsConnected = d.IsConnected,
                    Detail = d.Detail,
                    VoltageMillivolts = d.Percent is { } v ? 3500 + v * 7 : null,
                    UpdatedAt = now,
                });
            }

            _log.LogDebug("Demo battery poll #{Poll}: {Count} devices", _polls, result.Count);
            return Task.FromResult<IReadOnlyList<BatteryDevice>>(result);
        }
    }

    private sealed class DemoDevice
    {
        public DemoDevice(string id, string name, DeviceKind kind, ConnectionType connection, int? percent, BatteryStatus status, bool isConnected, string? detail)
        {
            Id = id;
            Name = name;
            Kind = kind;
            Connection = connection;
            Percent = percent;
            Status = status;
            IsConnected = isConnected;
            Detail = detail;
        }

        public string Id { get; }
        public string Name { get; }
        public DeviceKind Kind { get; }
        public ConnectionType Connection { get; }
        public int? Percent { get; set; }
        public BatteryStatus Status { get; set; }
        public bool IsConnected { get; }
        public string? Detail { get; }
    }
}
