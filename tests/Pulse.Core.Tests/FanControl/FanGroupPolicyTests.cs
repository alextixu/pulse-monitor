using Pulse.Core.FanControl;
using Pulse.Core.Tests.Fakes;
using Xunit;

namespace Pulse.Core.Tests.FanControl;

/// <summary>Pins the group membership rules (src/Pulse.Core/FanControl/FanGroupPolicy.cs).</summary>
public sealed class FanGroupPolicyTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private static IReadOnlySet<string> Ids(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void Evaluate_UserExclusion_BeatsEverythingIncludingAnExplicitInclude()
    {
        var fan = Fx.Board("a", name: "Fan");
        Assert.Equal(FanExclusion.User, FanGroupPolicy.Evaluate(fan, new FanFirstSight(50, 3000), Ids("a"), Ids("a")));
    }

    [Fact]
    public void Evaluate_Include_BeatsThePumpName_AndThePumpLikeHeuristic()
    {
        Assert.Equal(FanExclusion.None, FanGroupPolicy.Evaluate(Fx.Board("a", name: "AIO Pump"), null, None, Ids("a")));
        Assert.Equal(FanExclusion.None, FanGroupPolicy.Evaluate(Fx.Board("a", name: "Fan"), new FanFirstSight(100, 4000), None, Ids("a")));
    }

    [Fact]
    public void Evaluate_PumpName_BeatsThePumpLikeHeuristic()
    {
        Assert.Equal(FanExclusion.PumpName, FanGroupPolicy.Evaluate(Fx.Board("a", name: "Pump"), new FanFirstSight(100, 4000), None, None));
    }

    [Theory]
    [InlineData(50.0, 3000.0, FanExclusion.PumpLike)]   // ≥ 2800 RPM whatever the duty
    [InlineData(null, 2800.0, FanExclusion.PumpLike)]
    [InlineData(50.0, 2799.0, FanExclusion.None)]
    [InlineData(95.0, 1800.0, FanExclusion.PumpLike)]   // flat out: ≥ 95 % and ≥ 1800 RPM
    [InlineData(95.0, 1799.0, FanExclusion.None)]
    [InlineData(94.9, 2000.0, FanExclusion.None)]
    [InlineData(100.0, null, FanExclusion.None)]        // no RPM → never pump-like
    [InlineData(null, null, FanExclusion.None)]
    public void Evaluate_PumpLike_FirstSeenAtHighRpm_OrFlatOut(double? percent, double? rpm, FanExclusion expected)
    {
        Assert.Equal(expected, FanGroupPolicy.Evaluate(Fx.Board("a", name: "Fan #1"), new FanFirstSight(percent, rpm), None, None));
    }

    [Fact]
    public void Evaluate_WithoutAFirstSight_IsNotPumpLike()
    {
        Assert.Equal(FanExclusion.None, FanGroupPolicy.Evaluate(Fx.Board("a", name: "Fan #1", percent: 100, rpm: 4000), null, None, None));
    }

    [Fact]
    public void Evaluate_GpuGroupFans_AreNeverPumpLike()
    {
        Assert.Equal(FanExclusion.None, FanGroupPolicy.Evaluate(Fx.Gpu(), new FanFirstSight(100, 3500), None, None));
        Assert.Equal(FanExclusion.None, FanGroupPolicy.Evaluate(Fx.Board("x", name: "Fan", group: "GPU"), new FanFirstSight(100, 3500), None, None));
    }

    [Theory]
    [InlineData("CPU Pump", true)]
    [InlineData("PUMP_FAN", true)]
    [InlineData("水泵", true)]
    [InlineData("AIO", true)]
    [InlineData("Corsair aio", true)]
    [InlineData("Fan #1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikePumpName_MatchesPumpOrAioCaseInsensitively_OrTheChineseCharacter(string? name, bool expected)
    {
        Assert.Equal(expected, FanGroupPolicy.LooksLikePumpName(name));
    }

    [Theory]
    [InlineData("顯示卡", true)]
    [InlineData("GPU", true)]
    [InlineData("gpu", true)]
    [InlineData("主機板", false)]
    [InlineData("GPU Fans", false)]
    [InlineData(null, false)]
    public void IsGpuGroup_MatchesTheLhmLabel_OrTheLegacyGpuLabel(string? group, bool expected)
    {
        Assert.Equal(expected, FanGroupPolicy.IsGpuGroup(group));
    }
}

/// <summary>Pins the per-session presence memory (FanPresenceTracker in src/Pulse.Core/FanControl/FanGroupPolicy.cs).</summary>
public sealed class FanPresenceTrackerTests
{
    [Fact]
    public void GraceSamples_IsThree()
    {
        Assert.Equal(3, FanPresenceTracker.GraceSamples);
    }

    [Fact]
    public void UnknownFan_IsPending()
    {
        Assert.Equal(FanPresence.Pending, new FanPresenceTracker().PresenceOf("never-seen"));
    }

    [Fact]
    public void BoardHeaderWithoutRpm_IsPendingForTheGracePeriod_ThenHidden()
    {
        var t = new FanPresenceTracker();
        var silent = Fx.Board("a", rpm: 0, percent: 0);

        t.Observe(new[] { silent });
        Assert.Equal(FanPresence.Pending, t.PresenceOf("a"));
        t.Observe(new[] { silent });
        Assert.Equal(FanPresence.Pending, t.PresenceOf("a"));
        t.Observe(new[] { silent });
        Assert.Equal(FanPresence.Hidden, t.PresenceOf("a"));
        t.Observe(new[] { silent });
        Assert.Equal(FanPresence.Hidden, t.PresenceOf("a"));
    }

    [Fact]
    public void NullRpm_CountsAsNotSpinning()
    {
        var t = new FanPresenceTracker();
        var silent = Fx.Board("a", rpm: null);
        for (var i = 0; i < FanPresenceTracker.GraceSamples; i++) t.Observe(new[] { silent });

        Assert.Equal(FanPresence.Hidden, t.PresenceOf("a"));
    }

    [Fact]
    public void GpuFan_IsAlwaysVisible_EvenWithZeroRpm()
    {
        var t = new FanPresenceTracker();
        var idle = Fx.Gpu(rpm: 0, percent: 0);

        Assert.Equal(FanPresence.Pending, t.PresenceOf(idle.Id)); // unknown until observed
        for (var i = 0; i < 5; i++) t.Observe(new[] { idle });

        Assert.Equal(FanPresence.Visible, t.PresenceOf(idle.Id));
    }

    [Fact]
    public void EverSpun_IsSticky_AFanThatSpunOnceStaysVisible()
    {
        var t = new FanPresenceTracker();
        t.Observe(new[] { Fx.Board("a", rpm: 900) });
        Assert.Equal(FanPresence.Visible, t.PresenceOf("a"));

        for (var i = 0; i < 10; i++) t.Observe(new[] { Fx.Board("a", rpm: 0) });

        Assert.Equal(FanPresence.Visible, t.PresenceOf("a"));
    }

    [Fact]
    public void SpinningUpAfterTheGracePeriod_MakesTheFanVisible()
    {
        var t = new FanPresenceTracker();
        for (var i = 0; i < 4; i++) t.Observe(new[] { Fx.Board("a", rpm: 0) });
        Assert.Equal(FanPresence.Hidden, t.PresenceOf("a"));

        t.Observe(new[] { Fx.Board("a", rpm: 500) });

        Assert.Equal(FanPresence.Visible, t.PresenceOf("a"));
    }

    [Fact]
    public void FirstSight_RecordsTheFirstSampleOnly()
    {
        var t = new FanPresenceTracker();
        Assert.Null(t.FirstSight("a"));

        t.Observe(new[] { Fx.Board("a", percent: 100, rpm: 3200) });
        t.Observe(new[] { Fx.Board("a", percent: 40, rpm: 1000) });

        Assert.Equal(new FanFirstSight(100, 3200), t.FirstSight("a"));
    }

    [Fact]
    public void Capture_CopiesPresenceAndFirstSights_AndUnknownIdsDefaultToVisible()
    {
        var t = new FanPresenceTracker();
        t.Observe(new[] { Fx.Board("spin", rpm: 1200, percent: 60), Fx.Board("dead", rpm: 0) });

        var snap = t.Capture();

        Assert.Equal(FanPresence.Visible, snap.PresenceOf("spin"));
        Assert.Equal(FanPresence.Pending, snap.PresenceOf("dead"));
        Assert.Equal(FanPresence.Visible, snap.PresenceOf("unknown"));
        Assert.Equal(new FanFirstSight(60, 1200), snap.FirstSight("spin"));
        Assert.Null(snap.FirstSight("unknown"));

        // The snapshot is a copy: later samples do not change it.
        for (var i = 0; i < 3; i++) t.Observe(new[] { Fx.Board("dead", rpm: 0) });
        Assert.Equal(FanPresence.Hidden, t.PresenceOf("dead"));
        Assert.Equal(FanPresence.Pending, snap.PresenceOf("dead"));
    }

    [Fact]
    public void AllVisible_TreatsEveryFanAsVisible_WithNoFirstSight()
    {
        Assert.Equal(FanPresence.Visible, FanPresenceSnapshot.AllVisible.PresenceOf("anything"));
        Assert.Null(FanPresenceSnapshot.AllVisible.FirstSight("anything"));
    }
}
