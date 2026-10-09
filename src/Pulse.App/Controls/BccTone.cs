using Pulse.Core.Models;

namespace Pulse.App.Controls;

/// <summary>Semantic colour family. Maps to BccXxxBrush / BccXxxTintBrush resources (see <see cref="BccTones"/>).</summary>
public enum BccTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
    Purple,
    Teal,
    Pink,
}

/// <summary>Threshold rules shared by converters, controls and the tray icon.</summary>
public static class BccTones
{
    /// <summary>≥50 success, 20–49 warning, &lt;20 danger; charging → accent; null → neutral.</summary>
    public static BccTone ForBattery(double? percent, bool isCharging = false)
    {
        if (isCharging) return BccTone.Accent;
        if (percent is not { } p) return BccTone.Neutral;
        return p >= 50 ? BccTone.Success : p >= 20 ? BccTone.Warning : BccTone.Danger;
    }

    public static BccTone ForBattery(BatteryDevice device) =>
        ForBattery(device.Percent, device.Status == BatteryStatus.Charging);

    /// <summary>&lt;60 success, 60–79 warning, ≥80 danger; null → neutral.</summary>
    public static BccTone ForTemperature(double? celsius)
    {
        if (celsius is not { } t) return BccTone.Neutral;
        return t < 60 ? BccTone.Success : t < 80 ? BccTone.Warning : BccTone.Danger;
    }

    /// <summary>Drives throttle earlier than CPUs: &lt;55 success, 55–69 warning, ≥70 danger; null → neutral.</summary>
    public static BccTone ForStorageTemperature(double? celsius)
    {
        if (celsius is not { } t) return BccTone.Neutral;
        return t < 55 ? BccTone.Success : t < 70 ? BccTone.Warning : BccTone.Danger;
    }

    /// <summary>&lt;60 accent, 60–84 warning, ≥85 danger; null → neutral.</summary>
    public static BccTone ForLoad(double? percent)
    {
        if (percent is not { } p) return BccTone.Neutral;
        return p < 60 ? BccTone.Accent : p < 85 ? BccTone.Warning : BccTone.Danger;
    }

    public static BccTone ForBatteryStatus(BatteryStatus status, int? percent) => status switch
    {
        BatteryStatus.Charging => BccTone.Accent,
        BatteryStatus.Full => BccTone.Success,
        BatteryStatus.Low => BccTone.Warning,
        BatteryStatus.Critical => BccTone.Danger,
        _ => ForBattery(percent),
    };

    public static BccTone ForHardwareState(HardwareMonitorState state) => state switch
    {
        HardwareMonitorState.Ready => BccTone.Success,
        HardwareMonitorState.Degraded => BccTone.Warning,
        HardwareMonitorState.Failed => BccTone.Danger,
        HardwareMonitorState.Initializing => BccTone.Accent,
        _ => BccTone.Neutral,
    };

    public static BccTone ForRgbState(RgbConnectionState state) => state switch
    {
        RgbConnectionState.Connected => BccTone.Success,
        RgbConnectionState.Connecting => BccTone.Accent,
        RgbConnectionState.Error => BccTone.Danger,
        _ => BccTone.Neutral,
    };

    /// <summary>Resource key of the solid brush, e.g. "BccSuccessBrush".</summary>
    public static string BrushKey(BccTone tone) => tone switch
    {
        BccTone.Accent => "BccAccentBrush",
        BccTone.Success => "BccSuccessBrush",
        BccTone.Warning => "BccWarningBrush",
        BccTone.Danger => "BccDangerBrush",
        BccTone.Purple => "BccPurpleBrush",
        BccTone.Teal => "BccTealBrush",
        BccTone.Pink => "BccPinkBrush",
        _ => "BccNeutralBrush",
    };

    /// <summary>Resource key of the translucent tint brush, e.g. "BccSuccessTintBrush".</summary>
    public static string TintBrushKey(BccTone tone) => tone switch
    {
        BccTone.Accent => "BccAccentTintBrush",
        BccTone.Success => "BccSuccessTintBrush",
        BccTone.Warning => "BccWarningTintBrush",
        BccTone.Danger => "BccDangerTintBrush",
        BccTone.Purple => "BccPurpleTintBrush",
        BccTone.Teal => "BccTealTintBrush",
        BccTone.Pink => "BccPinkTintBrush",
        _ => "BccNeutralTintBrush",
    };
}
