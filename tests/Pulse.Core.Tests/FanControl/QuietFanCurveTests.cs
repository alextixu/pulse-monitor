using Pulse.Core.FanControl;
using Pulse.Core.Tests.Fakes;
using Xunit;

namespace Pulse.Core.Tests.FanControl;

/// <summary>Pins the pure Quiet / stand-in curve maths (src/Pulse.Core/FanControl/QuietFanCurve.cs).</summary>
public sealed class QuietFanCurveTests
{
    [Fact]
    public void Profiles_MatchTheDocumentedThresholds()
    {
        var b = QuietFanCurve.Board;
        Assert.Equal((75.0, 80.0, 50.0, 80.0, 72.0, 35.0, 90.0), (b.QuietUpToC, b.RampEndC, b.RampEndPercent, b.HandoffAtC, b.ResumeAtC, b.FloorPercent, b.EmergencyC));

        var g = QuietFanCurve.Gpu;
        Assert.Equal((50.0, 70.0, 60.0, 75.0, 68.0, 30.0, 85.0), (g.QuietUpToC, g.RampEndC, g.RampEndPercent, g.HandoffAtC, g.ResumeAtC, g.FloorPercent, g.EmergencyC));

        Assert.Equal(TimeSpan.FromSeconds(10), QuietFanCurve.SmoothingTime);
        Assert.Equal(TimeSpan.FromSeconds(30), QuietFanCurve.MinHandoffTime);
        Assert.Equal(TimeSpan.FromSeconds(4), QuietFanCurve.MinWriteInterval);
        Assert.Equal(3, QuietFanCurve.MinStepPercent);
        Assert.Equal(100, QuietFanCurve.OrphanExitPercent);
        Assert.Same(QuietFanCurve.Gpu, QuietFanCurve.ProfileFor(true));
        Assert.Same(QuietFanCurve.Board, QuietFanCurve.ProfileFor(false));
    }

    [Theory]
    [InlineData(false, 20.0, 35.0)]
    [InlineData(false, 75.0, 35.0)]
    [InlineData(false, 77.5, 42.5)]
    [InlineData(false, 80.0, 50.0)]
    [InlineData(false, 100.0, 50.0)] // stays at the ramp end (the hand-off is decided elsewhere)
    [InlineData(true, 20.0, 30.0)]
    [InlineData(true, 50.0, 30.0)]
    [InlineData(true, 60.0, 45.0)]
    [InlineData(true, 70.0, 60.0)]
    [InlineData(true, 80.0, 60.0)]
    public void TargetPercent_FloorUntilQuietUpTo_LinearRampToRampEnd_ThenFlat(bool gpu, double temp, double expected)
    {
        var profile = QuietFanCurve.ProfileFor(gpu);
        Assert.Equal(expected, QuietFanCurve.TargetPercent(profile, temp, profile.FloorPercent), 1e-9);
    }

    [Fact]
    public void TargetPercent_FloorAboveTheRampEnd_StaysFlatAtTheFloor()
    {
        Assert.Equal(55, QuietFanCurve.TargetPercent(QuietFanCurve.Board, 60, 55));
        Assert.Equal(55, QuietFanCurve.TargetPercent(QuietFanCurve.Board, 78, 55));
        Assert.Equal(55, QuietFanCurve.TargetPercent(QuietFanCurve.Board, 90, 55));
    }

    [Fact]
    public void FloorFor_NeverBelowTheFansOwnMinimum()
    {
        Assert.Equal(35, QuietFanCurve.FloorFor(Fx.Board("a", min: 10), false));
        Assert.Equal(45, QuietFanCurve.FloorFor(Fx.Board("a", min: 45), false));
        Assert.Equal(30, QuietFanCurve.FloorFor(Fx.Gpu(min: 0), true));
        Assert.Equal(40, QuietFanCurve.FloorFor(Fx.Gpu(min: 40), true));
    }

    [Fact]
    public void TargetFor_UsesTheFansFloor_AndClampsToItsMaximum()
    {
        Assert.Equal(45, QuietFanCurve.TargetFor(Fx.Board("a", min: 45), false, 60));
        Assert.Equal(40, QuietFanCurve.TargetFor(Fx.Board("a", max: 40), false, 85));
    }

    [Theory]
    [InlineData(0.0, 100.0, 120.0, 100.0)]
    [InlineData(0.0, 100.0, -5.0, 0.0)]
    [InlineData(20.0, 80.0, 90.0, 80.0)]
    [InlineData(20.0, 80.0, 10.0, 20.0)]
    [InlineData(20.0, 80.0, 50.0, 50.0)]
    [InlineData(60.0, 40.0, 50.0, 60.0)]   // max below min: the max is lifted to the min
    [InlineData(-10.0, 150.0, 120.0, 100.0)] // fan limits themselves are clamped to 0..100
    public void ClampToFan_RespectsMinMax_AndSanitisesBrokenLimits(double min, double max, double value, double expected)
    {
        Assert.Equal(expected, QuietFanCurve.ClampToFan(Fx.Board("a", min: min, max: max), value));
    }

    [Theory]
    [InlineData(30.0, 40.0)]
    [InlineData(50.0, 40.0)]
    [InlineData(60.0, 50.0)]
    [InlineData(70.0, 60.0)]
    [InlineData(75.0, 72.5)]
    [InlineData(80.0, 85.0)]
    [InlineData(82.5, 92.5)]
    [InlineData(85.0, 100.0)]
    [InlineData(95.0, 100.0)]
    public void StandInPercent_Is40UpTo50C_60At70C_85At80C_100From85C(double temp, double expected)
    {
        Assert.Equal(expected, QuietFanCurve.StandInPercent(temp, pumpLike: false), 1e-9);
    }

    [Fact]
    public void StandInPercent_PumpOrUnknownTemperature_Is100()
    {
        Assert.Equal(100, QuietFanCurve.StandInPercent(30, pumpLike: true));
        Assert.Equal(100, QuietFanCurve.StandInPercent(null, pumpLike: false));
        Assert.Equal(100, QuietFanCurve.StandInPercent(double.NaN, pumpLike: false));
        Assert.Equal(100, QuietFanCurve.StandInPercent(double.PositiveInfinity, pumpLike: false));
    }

    [Theory]
    // board: hand-off at smoothed ≥ 80, emergency at raw ≥ 90, resume at ≤ 72 after ≥ 30 s
    [InlineData(false, 80.0, 80.0, null, true)]
    [InlineData(false, 79.9, 79.9, null, false)]
    [InlineData(false, 60.0, 90.0, null, true)]   // raw emergency alone
    [InlineData(false, 60.0, 89.9, null, false)]
    [InlineData(false, 72.1, 72.1, 60.0, true)]   // held, still above the resume point
    [InlineData(false, 72.0, 72.0, 60.0, false)]  // held, cooled to the resume point, long enough
    [InlineData(false, 60.0, 60.0, 29.9, true)]   // held, cool, but not yet 30 s
    [InlineData(false, 60.0, 60.0, 30.0, false)]
    // gpu: hand-off ≥ 75, emergency ≥ 85, resume ≤ 68
    [InlineData(true, 75.0, 75.0, null, true)]
    [InlineData(true, 74.9, 74.9, null, false)]
    [InlineData(true, 60.0, 85.0, null, true)]
    [InlineData(true, 68.1, 68.1, 60.0, true)]
    [InlineData(true, 68.0, 68.0, 60.0, false)]
    public void ShouldHandOff_UsesSmoothedForTheThreshold_RawForTheEmergency_AndHysteresisPlusMinimumHold(
        bool gpu, double smoothed, double raw, double? heldSeconds, bool expected)
    {
        TimeSpan? held = heldSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Assert.Equal(expected, QuietFanCurve.ShouldHandOff(gpu, smoothed, raw, held));
    }
}
