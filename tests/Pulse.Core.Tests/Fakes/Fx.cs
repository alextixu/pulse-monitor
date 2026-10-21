using Pulse.Core.FanControl;
using Pulse.Core.Models;

namespace Pulse.Core.Tests.Fakes;

/// <summary>Builders for snapshots, fans, settings and presence used by the fan-mode tests.</summary>
public static class Fx
{
    /// <summary>LibreHardwareMonitor's motherboard group label (board fans follow the CPU package temperature).</summary>
    public const string BoardGroup = "主機板";

    /// <summary>LibreHardwareMonitor's GPU group label (GPU fans follow the GPU core temperature).</summary>
    public const string GpuGroup = "顯示卡";

    public const string GpuId = "gpu0";

    public static FanInfo Board(
        string id,
        string? name = null,
        double? percent = 40,
        double? rpm = 1000,
        bool manual = false,
        bool canControl = true,
        double min = 0,
        double max = 100,
        string group = BoardGroup)
        => new()
        {
            Id = id,
            Name = name ?? id,
            Group = group,
            Percent = percent,
            Rpm = rpm,
            IsManual = manual,
            CanControl = canControl,
            MinPercent = min,
            MaxPercent = max,
        };

    /// <summary>A GPU fan: lives under the GPU node (id prefix "gpu0/") and in the GPU group.</summary>
    public static FanInfo Gpu(
        string id = GpuId + "/fan0",
        string? name = null,
        double? percent = 40,
        double? rpm = 1000,
        bool manual = false,
        double min = 0,
        double max = 100)
        => Board(id, name ?? "GPU Fan", percent, rpm, manual, canControl: true, min, max, group: GpuGroup);

    public static HardwareSnapshot Snap(double? cpuTempC, params FanInfo[] fans) => Snap(cpuTempC, null, fans);

    public static HardwareSnapshot Snap(double? cpuTempC, double? gpuTempC, params FanInfo[] fans) => new()
    {
        Cpu = new CpuInfo { Name = "CPU", PackageTempC = cpuTempC },
        Gpus = new[] { new GpuInfo { Id = GpuId, Name = "GPU", Vendor = "NVIDIA", CoreTempC = gpuTempC } },
        Fans = fans,
    };

    public static FanControlSettings Settings(
        FanMode mode,
        double synced = 50,
        IEnumerable<string>? excluded = null,
        IEnumerable<string>? included = null,
        IEnumerable<string>? orphaned = null)
        => new()
        {
            Mode = mode,
            SyncedPercent = synced,
            ExcludedIds = new HashSet<string>(excluded ?? Array.Empty<string>(), StringComparer.Ordinal),
            IncludedIds = new HashSet<string>(included ?? Array.Empty<string>(), StringComparer.Ordinal),
            OrphanedIds = new HashSet<string>(orphaned ?? Array.Empty<string>(), StringComparer.Ordinal),
        };

    public static FanPresenceSnapshot Presence(
        IDictionary<string, FanPresence>? presence = null,
        IDictionary<string, FanFirstSight>? firstSight = null)
        => new(
            new Dictionary<string, FanPresence>(presence ?? new Dictionary<string, FanPresence>(), StringComparer.Ordinal),
            new Dictionary<string, FanFirstSight>(firstSight ?? new Dictionary<string, FanFirstSight>(), StringComparer.Ordinal));

    /// <summary>One EMA step with the Quiet smoothing time constant (10 s): what <c>TemperatureSmoother</c> returns.</summary>
    public static double Ema(double previous, double raw, double dtSeconds)
        => previous + (1 - Math.Exp(-dtSeconds / QuietFanCurve.SmoothingTime.TotalSeconds)) * (raw - previous);
}
