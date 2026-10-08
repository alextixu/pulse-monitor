using Pulse.Core.Models;

namespace Pulse.Core.FanControl;

/// <summary>
/// The 靜音 (Quiet) temperature curve. Pure functions plus the constants the UI and README quote.
/// <list type="bullet">
/// <item>T ≤ 50 °C → quiet floor (max(fan min, 35 % board / 30 % GPU)).</item>
/// <item>50–70 °C → linear from the floor to 60 %; above 70 °C the curve stays at 60 %.</item>
/// <item>T ≥ 75 °C → the fan is handed back to the firmware until T ≤ 68 °C (hysteresis).</item>
/// </list>
/// </summary>
public static class QuietFanCurve
{
    public const double QuietUpToC = 50;
    public const double RampEndC = 70;
    public const double RampEndPercent = 60;
    public const double HandoffAtC = 75;
    public const double ResumeAtC = 68;
    public const double BoardFloorPercent = 35;
    public const double GpuFloorPercent = 30;

    /// <summary>Quiet writes are skipped unless the duty moves by at least this much…</summary>
    public const double MinStepPercent = 3;

    /// <summary>…and at most one write per fan per interval (handoffs are always immediate).</summary>
    public static readonly TimeSpan MinWriteInterval = TimeSpan.FromSeconds(4);

    public static double FloorFor(FanInfo fan, bool isGpuFan)
        => Math.Max(fan.MinPercent, isGpuFan ? GpuFloorPercent : BoardFloorPercent);

    /// <summary>Curve value for <paramref name="tempC"/> starting at <paramref name="floorPercent"/> (not clamped to a fan's max).</summary>
    public static double TargetPercent(double tempC, double floorPercent)
    {
        var top = Math.Max(RampEndPercent, floorPercent);
        if (tempC <= QuietUpToC) return floorPercent;
        if (tempC >= RampEndC) return top;
        var t = (tempC - QuietUpToC) / (RampEndC - QuietUpToC);
        return floorPercent + t * (top - floorPercent);
    }

    /// <summary>Curve value for a specific fan, clamped to its reported min/max.</summary>
    public static double TargetFor(FanInfo fan, bool isGpuFan, double tempC)
        => ClampToFan(fan, TargetPercent(tempC, FloorFor(fan, isGpuFan)));

    public static double ClampToFan(FanInfo fan, double percent)
    {
        var min = Math.Clamp(fan.MinPercent, 0, 100);
        var max = Math.Clamp(Math.Max(fan.MaxPercent, min), 0, 100);
        return Math.Clamp(percent, min, max);
    }
}
