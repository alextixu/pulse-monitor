using Microsoft.Extensions.Logging.Abstractions;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine;

namespace Pulse.Engine.Tests.Fakes;

/// <summary>Shared, thread-safe log of "who did what, in which order" for the shutdown-order tests.</summary>
public sealed class OrderLog
{
    private readonly List<string> _entries = new();

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public IReadOnlyList<string> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public int IndexOf(string entry) => Entries.ToList().IndexOf(entry);
}

/// <summary>
/// Hardware monitor that returns a fresh snapshot (built by <see cref="Build"/>) per poll, records fan writes and
/// dispose calls, and can be made to hang inside <see cref="GetSnapshot"/> (a stuck sensor read).
/// </summary>
public sealed class FakeHardwareMonitor : IHardwareMonitor
{
    private readonly object _gate = new();
    private readonly List<(string Id, double? Percent)> _writes = new();
    private int _polls;

    public HardwareMonitorStatus Status { get; private set; } = new()
    {
        State = HardwareMonitorState.Ready,
        IsElevated = true,
        CpuTemperatureAvailable = true,
        FanControlAvailable = true,
    };

    public event EventHandler<HardwareMonitorStatus>? StatusChanged;

    /// <summary>The fans of every snapshot (a fresh list and record per call).</summary>
    public Func<IReadOnlyList<FanInfo>> Fans { get; set; } = () => Array.Empty<FanInfo>();

    /// <summary>When set, <see cref="GetSnapshot"/> blocks on it (never set = stuck forever).</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public OrderLog? Order { get; set; }

    public int DisposeCount { get; private set; }

    public int Polls => Volatile.Read(ref _polls);

    public IReadOnlyList<(string Id, double? Percent)> Writes
    {
        get { lock (_gate) return _writes.ToList(); }
    }

    public void SetStatus(HardwareMonitorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetSnapshot(cancellationToken));

    public HardwareSnapshot GetSnapshot(CancellationToken cancellationToken = default)
    {
        Gate?.Wait();
        Interlocked.Increment(ref _polls);
        return new HardwareSnapshot
        {
            Cpu = new CpuInfo { Name = "CPU", LoadPercent = 10, PackageTempC = 45 },
            Gpus = new[] { new GpuInfo { Id = "gpu0", Name = "GPU", Vendor = "NVIDIA", CoreTempC = 40 } },
            Fans = Fans().ToList(),
        };
    }

    public Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
    {
        lock (_gate) _writes.Add((fanId, percent));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeCount++;
        Order?.Add("hardware.Dispose");
    }

    public static FanInfo Fan(string id, bool canControl = true, bool manual = false, double rpm = 900, double percent = 40)
        => new() { Id = id, Name = id, Group = "主機板", Rpm = rpm, Percent = percent, CanControl = canControl, IsManual = manual };
}

/// <summary>RGB backend with a mutable device list; records every call as text ("SetDeviceColor:1:#FF0000").</summary>
public sealed class FakeRgbController : IRgbController
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private RgbStatus _status = new() { State = RgbConnectionState.Disconnected };
    private int _refreshes;
    private int _connects;

    public List<RgbDevice> Devices { get; } = new();

    /// <summary>Called with the refresh number (1, 2, …) before a refresh:true read returns.</summary>
    public Action<int>? OnRefresh { get; set; }

    public OrderLog? Order { get; set; }

    public int Connects => Volatile.Read(ref _connects);

    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToList(); }
    }

    public RgbStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<RgbStatus>? StatusChanged;

    public void SetStatus(RgbStatus status)
    {
        lock (_gate) _status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Configure(string host, int port) => SetStatus(Status with { Host = host, Port = port });

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connects);
        SetStatus(Status with { State = RgbConnectionState.Connected, Message = "connected" });
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        SetStatus(Status with { State = RgbConnectionState.Disconnected });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (refresh) OnRefresh?.Invoke(Interlocked.Increment(ref _refreshes));
        lock (_gate) return Task.FromResult<IReadOnlyList<RgbDevice>>(Devices.ToList());
    }

    public Task SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken = default) => Record($"SetDeviceColor:{deviceIndex}:{color}");
    public Task SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken = default) => Record($"SetZoneColor:{deviceIndex}:{zoneIndex}:{color}");
    public Task SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color = null, int? speed = null, int? brightness = null, CancellationToken cancellationToken = default)
        => Record($"SetMode:{deviceIndex}:{modeIndex}:{color}:{speed}:{brightness}");
    public Task SetAllAsync(RgbColor color, CancellationToken cancellationToken = default) => Record($"SetAll:{color}");
    public Task TurnOffAsync(int deviceIndex, CancellationToken ct = default) => Record($"TurnOff:{deviceIndex}");
    public Task TurnOffAllAsync(CancellationToken ct = default) => Record("TurnOffAll");
    public Task RestoreDefaultAsync(int deviceIndex, CancellationToken ct = default) => Record($"RestoreDefault:{deviceIndex}");
    public Task RestoreAllDefaultsAsync(CancellationToken ct = default) => Record("RestoreAllDefaults");
    public Task SaveCurrentAsDefaultAsync(int deviceIndex, CancellationToken ct = default) => Record($"SaveDefault:{deviceIndex}");
    public bool HasDefault(int deviceIndex) => false;

    public void Dispose() => Order?.Add("rgb.Dispose");

    private Task Record(string call)
    {
        lock (_gate) _calls.Add(call);
        return Task.CompletedTask;
    }

    public static RgbDevice Device(int index, string key, int zones = 1, params string[] modes) => new()
    {
        Index = index,
        Key = key,
        Name = key.Split('|')[0],
        Type = "LED Strip",
        Zones = Enumerable.Range(0, zones).Select(z => new RgbZone { Index = z, Name = $"Zone {z}", LedCount = 4 }).ToList(),
        Modes = (modes.Length == 0 ? new[] { "Direct", "Static", "Breathing", "Off" } : modes)
            .Select((name, i) => new RgbMode
            {
                Index = i,
                Name = name,
                SupportsColor = name is "Direct" or "Static" or "Breathing",
                SupportsSpeed = name is "Breathing" or "Rainbow",
                IsPerLed = name == "Direct",
            }).ToList(),
        LedCount = zones * 4,
    };
}

public sealed class FakeLauncher : IRgbServerLauncher
{
    public OrderLog? Order { get; set; }
    public RgbLaunchResult Result { get; set; } = RgbLaunchResult.NotBundled;
    public int Launches { get; private set; }
    public bool IsBundled => true;

    public Task<RgbLaunchResult> EnsureRunningAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        Launches++;
        return Task.FromResult(Result);
    }

    public void Dispose() => Order?.Add("launcher.Dispose");
}

public sealed class FakeBatteryProvider : IBatteryProvider
{
    public string Name => "Fake";
    public bool IsSupported => true;
    public Func<IReadOnlyList<BatteryDevice>> Devices { get; set; } = () => Array.Empty<BatteryDevice>();

    public Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Devices());
}

/// <summary>Settings in memory; records saves. The default path exists, so the engine never does its first-run save.</summary>
public sealed class RecordingSettingsStore : ISettingsStore
{
    private readonly List<AppSettings> _saves = new();

    public AppSettings Initial { get; set; } = new();
    public OrderLog? Order { get; set; }
    public string FilePath { get; set; } = typeof(RecordingSettingsStore).Assembly.Location;

    public IReadOnlyList<AppSettings> Saves
    {
        get { lock (_saves) return _saves.ToList(); }
    }

    public AppSettings Load() => Initial.Clone();

    public void Save(AppSettings settings)
    {
        lock (_saves) _saves.Add(settings.Clone());
        Order?.Add("settings.Save");
    }
}

public sealed class RecordingStateStore : IStateStore
{
    private readonly List<PulseState> _saves = new();

    public PulseState Initial { get; set; } = new();
    public OrderLog? Order { get; set; }
    public string FilePath => "(test)";

    public IReadOnlyList<PulseState> Saves
    {
        get { lock (_saves) return _saves.ToList(); }
    }

    public PulseState Load() => Initial;

    public void Save(PulseState state)
    {
        lock (_saves) _saves.Add(state);
        Order?.Add("state.Save");
    }
}

/// <summary>Builds an <see cref="AgentCore"/> from fakes (no files, no telemetry).</summary>
public static class TestAgent
{
    public static AgentCore Create(
        FakeHardwareMonitor? hardware = null,
        FakeRgbController? rgb = null,
        RecordingSettingsStore? settings = null,
        RecordingStateStore? state = null,
        FakeLauncher? launcher = null,
        IReadOnlyList<IBatteryProvider>? providers = null,
        bool demo = false)
        => new(new AgentCoreOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            SettingsStore = settings ?? new RecordingSettingsStore(),
            StateStore = state ?? new RecordingStateStore(),
            Hardware = hardware ?? new FakeHardwareMonitor(),
            Rgb = rgb ?? new FakeRgbController(),
            RgbLauncher = launcher ?? new FakeLauncher(),
            BatteryProviders = providers ?? Array.Empty<IBatteryProvider>(),
            Elevation = new NullElevationService(),
            AutoStart = new NullAutoStartService(),
            TelemetryDirectory = null,
            IsDemo = demo,
        });

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
