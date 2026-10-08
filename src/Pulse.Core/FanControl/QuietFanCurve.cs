using Pulse.Core.Models;

namespace Pulse.Core.FanControl;

/// <summary>Thresholds of the Quiet curve for one temperature source.</summary>
/// <param name="QuietUpToC">Up to this temperature the fan stays at <paramref name="FloorPercent"/>.</param>
/// <param name="RampEndC">The curve reaches <paramref name="RampEndPercent"/> here and stays there.</param>
/// <param name="HandoffAtC">At or above this the fan is handed back to the firmware…</param>
/// <param name="ResumeAtC">…until the temperature is back at or below this (hysteresis).</param>
public sealed record QuietCurveProfile(double QuietUpToC, double RampEndC, double RampEndPercent, double HandoffAtC, double ResumeAtC, double FloorPercent);

/// <summary>
/// The 靜音 (Quiet) temperature curves. Pure functions plus the constants the UI and README quote.
/// <list type="bullet">
/// <item>Board fans (CPU package temperature): ≤ 75 °C quiet floor (35 %), 75–80 °C up to 50 %, ≥ 80 °C firmware until ≤ 72 °C.</item>
/// <item>GPU fans (GPU core temperature): ≤ 50 °C floor (30 %), 50–70 °C up to 60 %, ≥ 75 °C firmware until ≤ 68 °C.</item>
/// </list>
/// The floor never goes below the fan's own minimum.
/// </summary>
public static class QuietFanCurve
{
    /// <summary>CPU coolers / case fans: the user wants them slow as long as the CPU stays under 80 °C.</summary>
    public static QuietCurveProfile Board { get; } = new(QuietUpToC: 75, RampEndC: 80, RampEndPercent: 50, HandoffAtC: 80, ResumeAtC: 72, FloorPercent: 35);

    public static QuietCurveProfile Gpu { get; } = new(QuietUpToC: 50, RampEndC: 70, RampEndPercent: 60, HandoffAtC: 75, ResumeAtC: 68, FloorPercent: 30);

    /// <summary>Quiet writes are skipped unless the duty moves by at least this much…</summary>
    public const double MinStepPercent = 3;

    /// <summary>…and at most one write per fan per interval (handoffs are always immediate).</summary>
    public static readonly TimeSpan MinWriteInterval = TimeSpan.FromSeconds(4);

    public static QuietCurveProfile ProfileFor(bool isGpuFan) => isGpuFan ? Gpu : Board;

    public static double FloorFor(FanInfo fan, bool isGpuFan)
        => Math.Max(fan.MinPercent, ProfileFor(isGpuFan).FloorPercent);

    /// <summary>Curve value for <paramref name="tempC"/> starting at <paramref name="floorPercent"/> (not clamped to a fan's max).</summary>
    public static double TargetPercent(QuietCurveProfile profile, double tempC, double floorPercent)
    {
        var top = Math.Max(profile.RampEndPercent, floorPercent);
        if (tempC <= profile.QuietUpToC) return floorPercent;
        if (tempC >= profile.RampEndC) return top;
        var t = (tempC - profile.QuietUpToC) / (profile.RampEndC - profile.QuietUpToC);
        return floorPercent + t * (top - floorPercent);
    }

    /// <summary>Curve value for a specific fan, clamped to its reported min/max.</summary>
    public static double TargetFor(FanInfo fan, bool isGpuFan, double tempC)
        => ClampToFan(fan, TargetPercent(ProfileFor(isGpuFan), tempC, FloorFor(fan, isGpuFan)));

    /// <summary>True when the fan must be (or stay) with the firmware at <paramref name="tempC"/>.</summary>
    public static bool ShouldHandOff(bool isGpuFan, double tempC, bool currentlyHandedOff)
    {
        var profile = ProfileFor(isGpuFan);
        return tempC >= profile.HandoffAtC || (currentlyHandedOff && tempC > profile.ResumeAtC);
    }

    public static double ClampToFan(FanInfo fan, double percent)
    {
        var min = Math.Clamp(fan.MinPercent, 0, 100);
        var max = Math.Clamp(Math.Max(fan.MaxPercent, min), 0, 100);
        return Math.Clamp(percent, min, max);
    }
}
