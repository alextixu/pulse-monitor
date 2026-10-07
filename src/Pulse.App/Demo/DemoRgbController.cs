using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>"--demo": in-memory OpenRGB stand-in with three devices (motherboard, mouse, DRAM) that remember colours / modes.</summary>
public sealed class DemoRgbController : IRgbController
{
    private readonly ILogger<DemoRgbController> _log;
    private readonly object _gate = new();
    private readonly List<DeviceState> _devices;
    private RgbStatus _status = new() { State = RgbConnectionState.Disconnected, Message = "示範模式：尚未連線" };

    private static readonly RgbMode[] StandardModes =
    {
        new() { Index = 0, Name = "Direct", SupportsColor = true, IsPerLed = true },
        new() { Index = 1, Name = "Static", SupportsColor = true, SupportsBrightness = true, MinBrightness = 0, MaxBrightness = 100 },
        new() { Index = 2, Name = "Breathing", SupportsColor = true, SupportsSpeed = true, SupportsBrightness = true, MinSpeed = 0, MaxSpeed = 100, MinBrightness = 0, MaxBrightness = 100 },
        new() { Index = 3, Name = "Rainbow", SupportsSpeed = true, MinSpeed = 0, MaxSpeed = 100 },
        new() { Index = 4, Name = "Off" },
    };

    public DemoRgbController(ILogger<DemoRgbController> log)
    {
        _log = log;
        _devices = new List<DeviceState>
        {
            new(0, "ASUS ROG STRIX Z790-I GAMING WIFI", "Motherboard", "ASUS", RgbColor.FromHex("#0A84FF"),
                new[] { new RgbZone { Index = 0, Name = "Aura 燈條", LedCount = 8 }, new RgbZone { Index = 1, Name = "ARGB 接頭", LedCount = 24 } }),
            new(1, "Logitech G PRO X SUPERLIGHT 2", "Mouse", "Logitech", RgbColor.FromHex("#AF52DE"),
                new[] { new RgbZone { Index = 0, Name = "Logo", LedCount = 1 } }),
            new(2, "Corsair Vengeance RGB", "DRAM", "Corsair", RgbColor.FromHex("#30D5C8"),
                new[] { new RgbZone { Index = 0, Name = "DIMM 1", LedCount = 10 }, new RgbZone { Index = 1, Name = "DIMM 2", LedCount = 10 } }),
        };
    }

    public RgbStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<RgbStatus>? StatusChanged;

    public void Configure(string host, int port)
    {
        SetStatus(Status with { Host = host, Port = port });
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(Status with { State = RgbConnectionState.Connecting, Message = "示範模式：正在連線…" });
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        SetStatus(Status with { State = RgbConnectionState.Connected, Message = "示範模式（模擬的 OpenRGB 伺服器）", ServerVersion = "demo 1.0" });
        _log.LogInformation("Demo RGB connected");
        return true;
    }

    public Task DisconnectAsync()
    {
        SetStatus(Status with { State = RgbConnectionState.Disconnected, Message = "示範模式：已中斷連線", ServerVersion = null });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status.State != RgbConnectionState.Connected) return Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());
            return Task.FromResult<IReadOnlyList<RgbDevice>>(_devices.Select(d => d.ToDevice()).ToList());
        }
    }

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var d = Find(deviceIndex);
            if (!StandardModes[d.ActiveMode].SupportsColor) d.ActiveMode = 0;
            Array.Fill(d.Colors, color);
            _log.LogInformation("Demo RGB device {Device} → {Color}", d.Name, color);
        }
        return Task.CompletedTask;
    }

    public Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var d = Find(deviceIndex);
            if (zoneIndex < 0 || zoneIndex >= d.Zones.Length) throw new InvalidOperationException($"區域 {zoneIndex} 不存在。");
            var start = d.Zones.Take(zoneIndex).Sum(z => z.LedCount);
            for (var i = 0; i < d.Zones[zoneIndex].LedCount; i++) d.Colors[start + i] = color;
            _log.LogInformation("Demo RGB {Device} zone {Zone} → {Color}", d.Name, zoneIndex, color);
        }
        return Task.CompletedTask;
    }

    public Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var d = Find(deviceIndex);
            if (modeIndex < 0 || modeIndex >= StandardModes.Length) throw new InvalidOperationException($"模式 {modeIndex} 不存在。");
            d.ActiveMode = modeIndex;
            if (color is { } c && StandardModes[modeIndex].SupportsColor) Array.Fill(d.Colors, c);
            if (speed is { } s) d.Speed = Math.Clamp(s, 0, 100);
            if (brightness is { } b) d.Brightness = Math.Clamp(b, 0, 100);
            _log.LogInformation("Demo RGB {Device} mode → {Mode}", d.Name, StandardModes[modeIndex].Name);
        }
        return Task.CompletedTask;
    }

    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var d in _devices)
            {
                if (!StandardModes[d.ActiveMode].SupportsColor) d.ActiveMode = 0;
                Array.Fill(d.Colors, color);
            }
            _log.LogInformation("Demo RGB all devices → {Color}", color);
        }
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private DeviceState Find(int index)
    {
        if (_status.State != RgbConnectionState.Connected) throw new InvalidOperationException("尚未連線到 OpenRGB。");
        return _devices.FirstOrDefault(d => d.Index == index) ?? throw new InvalidOperationException($"裝置 {index} 不存在。");
    }

    private void SetStatus(RgbStatus status)
    {
        lock (_gate) _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private sealed class DeviceState
    {
        public DeviceState(int index, string name, string type, string vendor, RgbColor color, RgbZone[] zones)
        {
            Index = index;
            Name = name;
            Type = type;
            Vendor = vendor;
            Zones = zones;
            Colors = new RgbColor[zones.Sum(z => z.LedCount)];
            Array.Fill(Colors, color);
            ActiveMode = 1;
            Brightness = 80;
            Speed = 50;
        }

        public int Index { get; }
        public string Name { get; }
        public string Type { get; }
        public string Vendor { get; }
        public RgbZone[] Zones { get; }
        public RgbColor[] Colors { get; }
        public int ActiveMode { get; set; }
        public int Brightness { get; set; }
        public int Speed { get; set; }

        public RgbDevice ToDevice() => new()
        {
            Index = Index,
            Name = Name,
            Type = Type,
            Vendor = Vendor,
            Description = $"示範裝置（{Type}）",
            Zones = Zones,
            Modes = StandardModes,
            ActiveModeIndex = ActiveMode,
            LedCount = Colors.Length,
            Colors = (RgbColor[])Colors.Clone(),
        };
    }
}
