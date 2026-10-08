using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>
/// "--demo": in-memory OpenRGB stand-in with three devices (motherboard, mouse, DRAM) that remember colours / modes.
/// The mouse has no "Off" mode (so 關燈 paints it black) and the state of every device is snapshotted on the first
/// connect so 還原預設 has something to restore.
/// </summary>
public sealed class DemoRgbController : IRgbController
{
    private readonly ILogger<DemoRgbController> _log;
    private readonly object _gate = new();
    private readonly List<DeviceState> _devices;
    private readonly Dictionary<int, DemoDefault> _defaults = new();
    private RgbStatus _status = new() { State = RgbConnectionState.Disconnected, Message = "示範模式：尚未連線" };

    private static readonly RgbMode[] StandardModes =
    {
        new() { Index = 0, Name = "Direct", SupportsColor = true, IsPerLed = true },
        new() { Index = 1, Name = "Static", SupportsColor = true, SupportsBrightness = true, MinBrightness = 0, MaxBrightness = 100 },
        new() { Index = 2, Name = "Breathing", SupportsColor = true, SupportsSpeed = true, SupportsBrightness = true, MinSpeed = 0, MaxSpeed = 100, MinBrightness = 0, MaxBrightness = 100 },
        new() { Index = 3, Name = "Rainbow", SupportsSpeed = true, MinSpeed = 0, MaxSpeed = 100 },
        new() { Index = 4, Name = "Off" },
    };

    /// <summary>Mouse-style mode list without an "Off" mode.</summary>
    private static readonly RgbMode[] MouseModes =
    {
        new() { Index = 0, Name = "Direct", SupportsColor = true, IsPerLed = true },
        new() { Index = 1, Name = "Static", SupportsColor = true, SupportsBrightness = true, MinBrightness = 0, MaxBrightness = 100 },
        new() { Index = 2, Name = "Breathing", SupportsColor = true, SupportsSpeed = true, MinSpeed = 0, MaxSpeed = 100 },
        new() { Index = 3, Name = "Color Cycle", SupportsSpeed = true, MinSpeed = 0, MaxSpeed = 100 },
    };

    public DemoRgbController(ILogger<DemoRgbController> log)
    {
        _log = log;
        _devices = new List<DeviceState>
        {
            new(0, "ASUS ROG STRIX Z790-I GAMING WIFI", "Motherboard", "ASUS", RgbColor.FromHex("#0A84FF"),
                new[] { new RgbZone { Index = 0, Name = "Aura 燈條", LedCount = 8 }, new RgbZone { Index = 1, Name = "ARGB 接頭", LedCount = 24 } }, StandardModes),
            new(1, "Logitech G PRO X SUPERLIGHT 2", "Mouse", "Logitech", RgbColor.FromHex("#AF52DE"),
                new[] { new RgbZone { Index = 0, Name = "Logo", LedCount = 1 } }, MouseModes),
            new(2, "Corsair Vengeance RGB", "DRAM", "Corsair", RgbColor.FromHex("#30D5C8"),
                new[] { new RgbZone { Index = 0, Name = "DIMM 1", LedCount = 10 }, new RgbZone { Index = 1, Name = "DIMM 2", LedCount = 10 } }, StandardModes),
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
        lock (_gate)
        {
            // First time each device is seen: remember its state as the "default".
            foreach (var d in _devices) _defaults.TryAdd(d.Index, DemoDefault.Capture(d));
        }

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
            Paint(d, color);
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
            if (modeIndex < 0 || modeIndex >= d.Modes.Length) throw new InvalidOperationException($"模式 {modeIndex} 不存在。");
            d.ActiveMode = modeIndex;
            if (color is { } c && d.Modes[modeIndex].SupportsColor) Array.Fill(d.Colors, c);
            if (speed is { } s) d.Speed = Math.Clamp(s, 0, 100);
            if (brightness is { } b) d.Brightness = Math.Clamp(b, 0, 100);
            _log.LogInformation("Demo RGB {Device} mode → {Mode}", d.Name, d.Modes[modeIndex].Name);
        }
        return Task.CompletedTask;
    }

    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var d in _devices) Paint(d, color);
            _log.LogInformation("Demo RGB all devices → {Color}", color);
        }
        return Task.CompletedTask;
    }

    public Task TurnOffAsync(int deviceIndex, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (TryFind(deviceIndex) is { } d) TurnOff(d);
        }
        return Task.CompletedTask;
    }

    public Task TurnOffAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_status.State != RgbConnectionState.Connected) return Task.CompletedTask;
            foreach (var d in _devices) TurnOff(d);
        }
        return Task.CompletedTask;
    }

    public Task RestoreDefaultAsync(int deviceIndex, CancellationToken ct = default)
    {
        string? skipped = null;
        lock (_gate)
        {
            if (TryFind(deviceIndex) is { } d && !Restore(d)) skipped = d.Name;
        }

        if (skipped is not null) SetStatus(Status with { Message = "這個裝置沒有可還原的預設狀態，也沒有可切換的韌體燈效，因此無法還原。" });
        return Task.CompletedTask;
    }

    public Task RestoreAllDefaultsAsync(CancellationToken ct = default)
    {
        var skipped = new List<string>();
        lock (_gate)
        {
            if (_status.State != RgbConnectionState.Connected) return Task.CompletedTask;
            foreach (var d in _devices)
            {
                if (!Restore(d)) skipped.Add(d.Name);
            }
        }

        if (skipped.Count > 0) SetStatus(Status with { Message = $"以下裝置沒有可還原的預設狀態，也沒有可切換的韌體燈效，已略過：{string.Join("、", skipped)}。" });
        return Task.CompletedTask;
    }

    public Task SaveCurrentAsDefaultAsync(int deviceIndex, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (TryFind(deviceIndex) is { } d)
            {
                _defaults[d.Index] = DemoDefault.Capture(d);
                _log.LogInformation("Demo RGB {Device}: current state saved as default ({Mode})", d.Name, d.Modes[d.ActiveMode].Name);
            }
        }
        return Task.CompletedTask;
    }

    public bool HasDefault(int deviceIndex)
    {
        lock (_gate) return _defaults.ContainsKey(deviceIndex);
    }

    private static void Paint(DeviceState d, RgbColor color)
    {
        if (!d.Modes[d.ActiveMode].SupportsColor) d.ActiveMode = 0;
        Array.Fill(d.Colors, color);
    }

    private void TurnOff(DeviceState d)
    {
        if (RgbModeRules.FindOffMode(d.Modes) is { } off)
        {
            d.ActiveMode = off.Index;
            Array.Fill(d.Colors, default);
            _log.LogInformation("Demo RGB {Device} → Off mode", d.Name);
        }
        else
        {
            Paint(d, default);
            _log.LogInformation("Demo RGB {Device} has no Off mode → painted black", d.Name);
        }
    }

    /// <summary>Restores the snapshot (or the first firmware effect); false when there is nothing to restore.</summary>
    private bool Restore(DeviceState d)
    {
        if (_defaults.TryGetValue(d.Index, out var saved))
        {
            d.ActiveMode = saved.Mode;
            Array.Copy(saved.Colors, d.Colors, Math.Min(saved.Colors.Length, d.Colors.Length));
            d.Speed = saved.Speed;
            d.Brightness = saved.Brightness;
            _log.LogInformation("Demo RGB {Device} restored to default ({Mode})", d.Name, d.Modes[d.ActiveMode].Name);
            return true;
        }

        if (RgbModeRules.FindFirmwareEffect(d.Modes) is { } effect)
        {
            d.ActiveMode = effect.Index;
            _log.LogInformation("Demo RGB {Device} has no default → {Mode}", d.Name, effect.Name);
            return true;
        }

        return false;
    }

    /// <summary>Null when disconnected (no-op); <see cref="ArgumentOutOfRangeException"/> for an unknown index.</summary>
    private DeviceState? TryFind(int index)
    {
        if (_status.State != RgbConnectionState.Connected) return null;
        return _devices.FirstOrDefault(d => d.Index == index)
               ?? throw new ArgumentOutOfRangeException(nameof(index), index, $"裝置 {index} 不存在。");
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

    /// <summary>In-memory "restore default" snapshot of a demo device.</summary>
    private sealed record DemoDefault(int Mode, RgbColor[] Colors, int Speed, int Brightness)
    {
        public static DemoDefault Capture(DeviceState d) => new(d.ActiveMode, (RgbColor[])d.Colors.Clone(), d.Speed, d.Brightness);
    }

    private sealed class DeviceState
    {
        public DeviceState(int index, string name, string type, string vendor, RgbColor color, RgbZone[] zones, RgbMode[] modes)
        {
            Modes = modes;
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
        public RgbMode[] Modes { get; }
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
            Modes = Modes,
            ActiveModeIndex = ActiveMode,
            LedCount = Colors.Length,
            Colors = (RgbColor[])Colors.Clone(),
        };
    }
}
