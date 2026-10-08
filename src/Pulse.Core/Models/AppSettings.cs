namespace Pulse.Core.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>What the dynamic tray icon renders.</summary>
public enum TrayIconMode
{
    /// <summary>Lowest battery percentage among connected devices; falls back to CPU temperature when no device reports.</summary>
    LowestBattery,
    CpuTemperature,
    GpuTemperature,
    CpuLoad,
    IconOnly,
}

/// <summary>How Pulse drives the controllable fans (see <c>Pulse.Core.FanControl.FanModeController</c>).</summary>
public enum FanMode
{
    /// <summary>Per-fan cards: each fan is either left to the firmware or set manually by the user.</summary>
    Individual,
    /// <summary>Every fan is handed back to the firmware (BIOS curve / GPU driver).</summary>
    Auto,
    /// <summary>Temperature curve tuned for low noise; hands fans back to the firmware when it gets hot.</summary>
    Quiet,
    /// <summary>All group members run at <see cref="AppSettings.SyncedFanPercent"/>.</summary>
    Synced,
}

/// <summary>Persisted user settings. Mutable so the settings view can bind directly; saved as JSON by <see cref="Abstractions.ISettingsStore"/>.</summary>
public sealed class AppSettings
{
    public int HardwareRefreshSeconds { get; set; } = 2;
    public int BatteryRefreshSeconds { get; set; } = 30;

    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
    public bool AutoConnectOpenRgb { get; set; } = true;
    /// <summary>Start the OpenRGB shipped with Pulse when nothing answers on the SDK port (local host only).</summary>
    public bool UseBundledOpenRgb { get; set; } = true;

    public bool StartWithSystem { get; set; }
    /// <summary>On Windows: re-launch elevated at startup so CPU temperature and fan control work without a manual step.</summary>
    public bool RequestElevationOnStartup { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;
    public TrayIconMode TrayIconMode { get; set; } = TrayIconMode.LowestBattery;

    public bool NotifyOnLowBattery { get; set; } = true;
    public int LowBatteryThresholdPercent { get; set; } = 20;

    /// <summary>Device ids the user chose to hide from the battery list.</summary>
    public List<string> HiddenDeviceIds { get; set; } = new();

    public FanMode FanMode { get; set; } = FanMode.Individual;

    /// <summary>Duty (0..100) applied to every group member in <see cref="FanMode.Synced"/>; clamped per fan to its min/max.</summary>
    public double SyncedFanPercent { get; set; } = 50;

    /// <summary>Fan ids kept out of the Quiet / Synced group (left to the firmware).</summary>
    public List<string> FanGroupExcludedIds { get; set; } = new();

    /// <summary>Fan ids explicitly opted into the group; overrides the pump heuristic.</summary>
    public List<string> FanGroupIncludedIds { get; set; } = new();

    /// <summary>Write a per-second hardware CSV (temperatures, loads, power, fans); cleared every hour. Forces a 1 s hardware poll.</summary>
    public bool TelemetryEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
