namespace Pulse.Core.Models;

public sealed record CpuInfo
{
    public required string Name { get; init; }
    public double? LoadPercent { get; init; }
    /// <summary>Package / Tctl temperature in °C. Null when the kernel driver is unavailable (not elevated).</summary>
    public double? PackageTempC { get; init; }
    public IReadOnlyList<double?> CoreTempsC { get; init; } = Array.Empty<double?>();
    public IReadOnlyList<double?> CoreLoadsPercent { get; init; } = Array.Empty<double?>();
    public double? ClockMhz { get; init; }
    public double? PowerWatts { get; init; }
}

public sealed record GpuInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>"NVIDIA", "AMD", "Intel" or "Unknown".</summary>
    public required string Vendor { get; init; }
    public double? LoadPercent { get; init; }
    public double? CoreTempC { get; init; }
    public double? HotSpotTempC { get; init; }
    public double? MemoryTempC { get; init; }
    public double? MemoryUsedMb { get; init; }
    public double? MemoryTotalMb { get; init; }
    public double? FanPercent { get; init; }
    public double? FanRpm { get; init; }
    public double? PowerWatts { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
}

public sealed record MemoryInfo
{
    public double? UsedGb { get; init; }
    public double? TotalGb { get; init; }
    public double? LoadPercent { get; init; }
}

public sealed record FanInfo
{
    /// <summary>Stable id used with <see cref="Abstractions.IHardwareMonitor.SetFanAsync"/>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Grouping label for the UI, e.g. "主機板", "GPU", "控制器".</summary>
    public required string Group { get; init; }
    public double? Rpm { get; init; }
    /// <summary>Current duty cycle 0..100 when a control channel exists.</summary>
    public double? Percent { get; init; }
    public bool CanControl { get; init; }
    /// <summary>True while a manual (software) value is applied; false when the firmware curve is in charge.</summary>
    public bool IsManual { get; init; }
    public double MinPercent { get; init; } = 0;
    public double MaxPercent { get; init; } = 100;
}

public sealed record TemperatureReading(string Id, string Name, string Group, double ValueC);

/// <summary>One drive (NVMe / SSD / HDD). Values are null when the drive does not report them or is not polled.</summary>
public sealed record StorageInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>"NVMe", "SATA", "USB", … from Windows' storage metadata; null when unknown.</summary>
    public string? Bus { get; init; }
    /// <summary>Rotating hard disk: never queried (SMART reads could spin it up).</summary>
    public bool IsHardDisk { get; init; }
    public double? TemperatureC { get; init; }
    public double? UsedSpacePercent { get; init; }
    public double? ActivityPercent { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }
    /// <summary>Remaining life 0..100 (NVMe: 100 − "Percentage Used").</summary>
    public double? LifePercent { get; init; }
    public double? DataWrittenGb { get; init; }
}

public sealed record HardwareSnapshot
{
    public CpuInfo? Cpu { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<FanInfo> Fans { get; init; } = Array.Empty<FanInfo>();
    /// <summary>Motherboard / memory / other temperatures not already covered by CPU, GPU and storage.</summary>
    public IReadOnlyList<TemperatureReading> OtherTemperatures { get; init; } = Array.Empty<TemperatureReading>();
    public IReadOnlyList<StorageInfo> Storages { get; init; } = Array.Empty<StorageInfo>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public static HardwareSnapshot Empty { get; } = new();
}

public enum HardwareMonitorState
{
    NotStarted,
    Initializing,
    Ready,
    /// <summary>Running, but some data (typically CPU temperature / fan control) is unavailable.</summary>
    Degraded,
    Failed,
    Unsupported,
}

public sealed record HardwareMonitorStatus
{
    public HardwareMonitorState State { get; init; } = HardwareMonitorState.NotStarted;
    public bool IsElevated { get; init; }
    public bool CpuTemperatureAvailable { get; init; }
    public bool FanControlAvailable { get; init; }
    /// <summary>Human readable hint for the UI (why something is missing and how to fix it).</summary>
    public string? Message { get; init; }

    /// <summary>Set when the previous session ended without restoring fans it had put in manual mode (crash / forced kill).</summary>
    public string? RecoveryNotice { get; init; }

    /// <summary>
    /// Fans whose firmware (BIOS) curve cannot be restored until the next reboot: a previous Pulse was killed while it held
    /// them, and Super I/O chips only restore the mode saved by the process that changed it. "Handing back" such a fan
    /// would re-apply the stuck manual duty, so Pulse drives them with a stand-in curve instead.
    /// </summary>
    public IReadOnlyList<string> OrphanedFanIds { get; init; } = Array.Empty<string>();
}
