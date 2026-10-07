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

/// <summary>Persisted user settings. Mutable so the settings view can bind directly; saved as JSON by <see cref="Abstractions.ISettingsStore"/>.</summary>
public sealed class AppSettings
{
    public int HardwareRefreshSeconds { get; set; } = 2;
    public int BatteryRefreshSeconds { get; set; } = 30;

    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
    public bool AutoConnectOpenRgb { get; set; } = true;

    public bool StartWithSystem { get; set; }
    /// <summary>On Windows: re-launch elevated at startup so CPU temperature and fan control work without a manual step.</summary>
    public bool RequestElevationOnStartup { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;
    public TrayIconMode TrayIconMode { get; set; } = TrayIconMode.LowestBattery;

    public bool NotifyOnLowBattery { get; set; } = true;
    public int LowBatteryThresholdPercent { get; set; } = 20;

    /// <summary>Device ids the user chose to hide from the battery list.</summary>
    public List<string> HiddenDeviceIds { get; set; } = new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
