using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Demo;

/// <summary>
/// "--demo": i7-12700K + RTX 3080 Ti style telemetry with gently changing values. Fans exercise every fan-mode path:
/// five board headers with fans (one "AIO Pump" flat out at 100 % / 2400 RPM, one monitor-only), two empty headers
/// (0 RPM → hidden), and two GPU fans. The GPU starts in a warm phase above 75 °C (Quiet hands its fans back) and the
/// CPU spikes past 75 °C now and then. Fans not set manually follow a firmware-like auto curve.
/// </summary>
public sealed class DemoHardwareMonitor : IHardwareMonitor
{
    private const string GpuId = "demo:gpu0";
    private const string BoardGroup = "主機板";
    private const string GpuGroup = "顯示卡";

    private readonly ILogger<DemoHardwareMonitor> _log;
    private readonly object _gate = new();
    private readonly Random _random = new(7);
    private readonly Dictionary<string, double> _manualFans = new();
    private double _phase;
    /// <summary>Starts just before the peak of the GPU "load burst" so the Quiet handoff is visible right away.</summary>
    private double _gpuPhase = Math.PI / 2 - 0.45;
    private HardwareMonitorStatus _status = new() { State = HardwareMonitorState.NotStarted };

    private enum Source { Cpu, Gpu, Pump, None }

    private sealed record FanDef(string Id, string Name, string Group, bool CanControl, double Min, Source Source, double RpmPerPercent);

    private static readonly FanDef[] FanDefs =
    {
        new("demo:fan:cpu", "CPU 風扇", BoardGroup, true, 20, Source.Cpu, 26),
        new("demo:fan:pump", "AIO Pump", BoardGroup, true, 20, Source.Pump, 24),
        new("demo:fan:chassis1", "機殼風扇 1", BoardGroup, true, 20, Source.Cpu, 18),
        new("demo:fan:chassis2", "機殼風扇 2", BoardGroup, true, 20, Source.Cpu, 18),
        new("demo:fan:chassis3", "機殼風扇 3", BoardGroup, false, 0, Source.Cpu, 16),
        new("demo:fan:header6", "Fan #6", BoardGroup, true, 0, Source.None, 0),
        new("demo:fan:header7", "Fan #7", BoardGroup, true, 0, Source.None, 0),
        new($"{GpuId}/fan/1", "GPU Fan 1", GpuGroup, true, 30, Source.Gpu, 29),
        new($"{GpuId}/fan/2", "GPU Fan 2", GpuGroup, true, 30, Source.Gpu, 29),
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
            _gpuPhase += 0.0875;
            var wave = Math.Sin(_phase);
            var cpuLoad = Clamp(22 + 14 * wave + Jitter(4), 0, 100);
            // Mostly 42–60 °C; a narrow burst every ~2 minutes peaks around 80 °C.
            var burst = Math.Pow(Math.Max(0, Math.Sin(_phase * 0.13)), 6);
            var cpuTemp = Clamp(50 + 8 * wave + 24 * burst + Jitter(1.5), 30, 100);
            var coreLoads = new double?[20];
            var coreTemps = new double?[20];
            for (var i = 0; i < coreLoads.Length; i++)
            {
                coreLoads[i] = Clamp(cpuLoad + Jitter(18) + (i < 8 ? 6 : -4), 0, 100);
                coreTemps[i] = Clamp(cpuTemp + Jitter(5) + (i < 8 ? 2 : -3), 25, 100);
            }

            // GPU load bursts: 38–78 °C, above 75 °C for ~20 s per ~2.5 min cycle.
            var gpuWave = Math.Sin(_gpuPhase);
            var gpuLoad = Clamp(45 + 50 * gpuWave + Jitter(3), 0, 100);
            var gpuTemp = Clamp(58 + 20 * gpuWave + Jitter(0.5), 25, 100);

            var fans = new List<FanInfo>(FanDefs.Length);
            foreach (var def in FanDefs)
            {
                var manual = _manualFans.TryGetValue(def.Id, out var m) ? m : (double?)null;
                var auto = def.Source switch
                {
                    Source.Pump => 100,
                    Source.Gpu => Clamp(30 + Math.Max(0, gpuTemp - 50) * 2.2 + Jitter(1), 30, 100),
                    Source.Cpu => Clamp(32 + Math.Max(0, cpuTemp - 40) * 1.1 + Jitter(2), 20, 100),
                    _ => 60,
                };
                var percent = manual ?? auto;
                var rpm = def.Source == Source.None ? 0 : Math.Round(percent * def.RpmPerPercent + Jitter(12));
                fans.Add(new FanInfo
                {
                    Id = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    Rpm = rpm,
                    Percent = def.CanControl || def.Group == GpuGroup ? Math.Round(percent) : null,
                    CanControl = def.CanControl,
                    IsManual = manual is not null,
                    MinPercent = def.Min,
                    MaxPercent = 100,
                });
            }

            var gpuFan = fans.First(f => f.Group == GpuGroup);
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
                        Id = GpuId,
                        Name = "NVIDIA GeForce RTX 3080 Ti",
                        Vendor = "NVIDIA",
                        LoadPercent = gpuLoad,
                        CoreTempC = gpuTemp,
                        HotSpotTempC = gpuTemp + 11,
                        MemoryTempC = gpuTemp + 6,
                        MemoryUsedMb = 3280 + Jitter(60),
                        MemoryTotalMb = 12288,
                        FanPercent = gpuFan.Percent,
                        FanRpm = gpuFan.Rpm,
                        PowerWatts = Clamp(92 + 180 * Math.Max(0, gpuWave) + Jitter(4), 10, 400),
                        CoreClockMhz = 1695 + Jitter(30),
                        MemoryClockMhz = 9501,
                    },
                },
                Memory = new MemoryInfo { UsedGb = 12.3 + Jitter(0.2), TotalGb = 32, LoadPercent = 38.4 },
                Fans = fans,
                OtherTemperatures = new[]
                {
                    new TemperatureReading("demo:temp:mb", "主機板", BoardGroup, Clamp(38 + Jitter(1), 20, 90)),
                    new TemperatureReading("demo:temp:pch", "晶片組", BoardGroup, Clamp(47 + Jitter(1), 20, 90)),
                    new TemperatureReading("demo:temp:ssd", "Samsung 990 PRO", "儲存裝置", Clamp(44 + Jitter(1.5), 20, 90)),
                    new TemperatureReading("demo:temp:vrm", "VRM", BoardGroup, Clamp(51 + Jitter(2), 20, 110)),
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
            if (def is null) throw new InvalidOperationException($"找不到風扇 {fanId}。");
            if (!def.CanControl) throw new InvalidOperationException($"{def.Name} 僅支援監測，無法控制。");
            if (percent is null) _manualFans.Remove(fanId);
            else _manualFans[fanId] = Math.Clamp(percent.Value, def.Min, 100);
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
