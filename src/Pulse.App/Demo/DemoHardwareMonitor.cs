using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>"--demo": i7-12700K + RTX 3080 Ti style telemetry with gently changing values, 4 fans (2 controllable).</summary>
public sealed class DemoHardwareMonitor : IHardwareMonitor
{
    private readonly ILogger<DemoHardwareMonitor> _log;
    private readonly object _gate = new();
    private readonly Random _random = new(7);
    private readonly Dictionary<string, double?> _manualFans = new();
    private double _phase;
    private HardwareMonitorStatus _status = new() { State = HardwareMonitorState.NotStarted };

    private static readonly (string Id, string Name, string Group, bool CanControl, double BasePercent)[] FanDefs =
    {
        ("demo:fan:cpu", "CPU 風扇", "主機板", true, 45),
        ("demo:fan:chassis1", "機殼風扇 1", "主機板", true, 35),
        ("demo:fan:chassis2", "機殼風扇 2", "主機板", false, 30),
        ("demo:fan:gpu", "GPU 風扇", "GPU", false, 38),
    };

    public DemoHardwareMonitor(ILogger<DemoHardwareMonitor> log)
    {
        _log = log;
    }

    public HardwareMonitorStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<HardwareMonitorStatus>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(new HardwareMonitorStatus { State = HardwareMonitorState.Initializing });
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        SetStatus(new HardwareMonitorStatus
        {
            State = HardwareMonitorState.Ready,
            IsElevated = false,
            CpuTemperatureAvailable = true,
            FanControlAvailable = true,
            Message = null,
        });
        _log.LogInformation("Demo hardware monitor ready");
    }

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status.State != HardwareMonitorState.Ready) return Task.FromResult(HardwareSnapshot.Empty);

            _phase += 0.35;
            var wave = Math.Sin(_phase);
            var cpuLoad = Clamp(22 + 14 * wave + Jitter(4), 0, 100);
            var cpuTemp = Clamp(52 + 9 * wave + Jitter(1.5), 30, 100);
            var coreLoads = new double?[20];
            var coreTemps = new double?[20];
            for (var i = 0; i < coreLoads.Length; i++)
            {
                coreLoads[i] = Clamp(cpuLoad + Jitter(18) + (i < 8 ? 6 : -4), 0, 100);
                coreTemps[i] = Clamp(cpuTemp + Jitter(5) + (i < 8 ? 2 : -3), 25, 100);
            }

            var gpuLoad = Clamp(12 + 10 * Math.Sin(_phase * 0.7) + Jitter(3), 0, 100);
            var gpuTemp = Clamp(41 + 6 * Math.Sin(_phase * 0.5) + Jitter(1), 25, 100);

            var fans = new List<FanInfo>(FanDefs.Length);
            foreach (var def in FanDefs)
            {
                var manual = _manualFans.TryGetValue(def.Id, out var m) ? m : null;
                var percent = manual ?? Clamp(def.BasePercent + 10 * wave + Jitter(2), 0, 100);
                fans.Add(new FanInfo
                {
                    Id = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    Rpm = Math.Round(percent * 26 + 120 + Jitter(15)),
                    Percent = def.CanControl || def.Group == "GPU" ? Math.Round(percent) : null,
                    CanControl = def.CanControl,
                    IsManual = manual is not null,
                    MinPercent = def.CanControl ? 20 : 0,
                    MaxPercent = 100,
                });
            }

            var snapshot = new HardwareSnapshot
            {
                Cpu = new CpuInfo
                {
                    Name = "Intel Core i7-12700K",
                    LoadPercent = cpuLoad,
                    PackageTempC = cpuTemp,
                    CoreLoadsPercent = coreLoads,
                    CoreTempsC = coreTemps,
                    ClockMhz = 4900 + Jitter(150),
                    PowerWatts = Clamp(65 + 40 * wave + Jitter(3), 10, 250),
                },
                Gpus = new[]
                {
                    new GpuInfo
                    {
                        Id = "demo:gpu0",
                        Name = "NVIDIA GeForce RTX 3080 Ti",
                        Vendor = "NVIDIA",
                        LoadPercent = gpuLoad,
                        CoreTempC = gpuTemp,
                        HotSpotTempC = gpuTemp + 11,
                        MemoryTempC = gpuTemp + 6,
                        MemoryUsedMb = 3280 + Jitter(60),
                        MemoryTotalMb = 12288,
                        FanPercent = fans[3].Percent,
                        FanRpm = fans[3].Rpm,
                        PowerWatts = Clamp(92 + 30 * Math.Sin(_phase * 0.7) + Jitter(4), 10, 400),
                        CoreClockMhz = 1695 + Jitter(30),
                        MemoryClockMhz = 9501,
                    },
                },
                Memory = new MemoryInfo { UsedGb = 12.3 + Jitter(0.2), TotalGb = 32, LoadPercent = 38.4 },
                Fans = fans,
                OtherTemperatures = new[]
                {
                    new TemperatureReading("demo:temp:mb", "主機板", "主機板", Clamp(38 + Jitter(1), 20, 90)),
                    new TemperatureReading("demo:temp:pch", "晶片組", "主機板", Clamp(47 + Jitter(1), 20, 90)),
                    new TemperatureReading("demo:temp:ssd", "Samsung 990 PRO", "儲存裝置", Clamp(44 + Jitter(1.5), 20, 90)),
                    new TemperatureReading("demo:temp:vrm", "VRM", "主機板", Clamp(51 + Jitter(2), 20, 110)),
                },
                Timestamp = DateTimeOffset.Now,
            };
            return Task.FromResult(snapshot);
        }
    }

    public Task SetFanAsync(string fanId, double? percent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var def = FanDefs.FirstOrDefault(f => f.Id == fanId);
            if (def.Id is null) throw new InvalidOperationException($"找不到風扇 {fanId}。");
            if (!def.CanControl) throw new InvalidOperationException($"{def.Name} 僅支援監測，無法控制。");
            if (percent is null) _manualFans.Remove(fanId);
            else _manualFans[fanId] = Math.Clamp(percent.Value, 0, 100);
            _log.LogInformation("Demo fan {Fan} → {Percent}", def.Name, percent?.ToString("0") ?? "auto");
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate) _manualFans.Clear();
    }

    private void SetStatus(HardwareMonitorStatus status)
    {
        lock (_gate) _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private double Jitter(double amplitude) => (_random.NextDouble() * 2 - 1) * amplitude;

    private static double Clamp(double v, double min, double max) => Math.Round(Math.Clamp(v, min, max), 1);
}
