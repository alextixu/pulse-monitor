using Pulse.Core.FanControl;
using Pulse.Core.Models;
using Pulse.Core.Tests.Fakes;
using Xunit;

namespace Pulse.Core.Tests.FanControl;

/// <summary>
/// Pins the fan-mode state machine (src/Pulse.Core/FanControl/FanModeController.cs) before it moves into the headless
/// agent. The hardware is the recording fake; time only moves when a test advances the clock.
/// </summary>
public sealed class FanModeControllerTests
{
    private readonly FakeHardwareMonitor _hw = new();
    private readonly TestClock _clock = new();
    private readonly FanModeController _c;

    public FanModeControllerTests() => _c = new FanModeController(_hw, null, _clock.Func);

    private Task<FanModeResult> Run(HardwareSnapshot snap, FanControlSettings settings, FanPresenceSnapshot? presence = null, bool controlAvailable = true)
        => _c.UpdateAsync(snap, settings, controlAvailable, presence);

    private static FanPresenceSnapshot FirstSight(string id, double? percent, double? rpm)
        => Fx.Presence(firstSight: new Dictionary<string, FanFirstSight> { [id] = new(percent, rpm) });

    private static FanPresenceSnapshot PresenceOf(string id, FanPresence presence)
        => Fx.Presence(presence: new Dictionary<string, FanPresence> { [id] = presence });

    // ───────────────────────────── Auto / mode switches / orphans ─────────────────────────────

    [Fact]
    public async Task Auto_HandsBackEveryControllableManualFan_ExceptOrphans_WhichGetTheStandInCurve()
    {
        var snap = Fx.Snap(60,
            Fx.Board("f1", manual: true),
            Fx.Board("f2", manual: true),
            Fx.Board("f3", manual: false),
            Fx.Board("f4", manual: true, canControl: false),
            Fx.Board("orph", manual: true));

        var r = await Run(snap, Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }));

        Assert.Equal(new[] { "f1", "f2" }, _hw.HandBacks);
        Assert.Equal(new[] { ("orph", 50.0) }, _hw.Writes); // stand-in curve at 60 °C = 50 %
        Assert.Equal(FanModeState.Auto, r.Fans["f1"].State);
        Assert.Equal(FanModeState.Auto, r.Fans["f2"].State);
        Assert.Equal(FanModeState.Auto, r.Fans["f3"].State);
        Assert.Equal(FanModeState.NotControllable, r.Fans["f4"].State);
        Assert.Equal(FanModeState.StandIn, r.Fans["orph"].State);
        Assert.Equal(50.0, r.Fans["orph"].Percent);
    }

    [Theory]
    [InlineData(FanMode.Individual)]
    [InlineData(FanMode.Auto)]
    [InlineData(FanMode.Quiet)]
    [InlineData(FanMode.Synced)]
    public async Task OrphanedFans_AreNeverHandedBack_InAnyMode(FanMode mode)
    {
        var snap = Fx.Snap(60, Fx.Board("orph", manual: true), Fx.Board("f1", manual: true));

        await Run(snap, Fx.Settings(mode, orphaned: new[] { "orph" }));

        Assert.DoesNotContain("orph", _hw.HandBacks);
        if (mode != FanMode.Individual) Assert.Contains("f1", _hw.HandBacks);
    }

    [Theory]
    [InlineData(true, 60.0, 50.0)]   // user-excluded orphan: stand-in, not firmware
    [InlineData(false, null, 100.0)] // no temperature: stand-in at 100 %, not firmware
    [InlineData(false, 95.0, 100.0)] // emergency heat: stand-in at 100 %, not firmware
    public async Task Quiet_OrphanedFan_GetsTheStandInCurve_WhereOthersWouldGoToFirmware(bool userExcluded, double? cpuTemp, double expectedPercent)
    {
        var snap = Fx.Snap(cpuTemp, Fx.Board("orph", manual: true));
        var settings = Fx.Settings(FanMode.Quiet, orphaned: new[] { "orph" }, excluded: userExcluded ? new[] { "orph" } : null);

        var r = await Run(snap, settings);

        Assert.Empty(_hw.HandBacks);
        Assert.Equal(FanModeState.StandIn, r.Fans["orph"].State);
        Assert.Equal(new[] { ("orph", expectedPercent) }, _hw.Writes);
    }

    [Fact]
    public async Task ModeSwitch_DoesNotHandBackAHeldOrphan_ItMovesToTheStandInCurveInstead()
    {
        var orphan = Fx.Board("orph");
        await Run(Fx.Snap(60, orphan), Fx.Settings(FanMode.Quiet, orphaned: new[] { "orph" }));
        Assert.Equal(new[] { ("orph", 35.0) }, _hw.Writes); // Quiet drives a non-excluded orphan like any member
        _hw.Clear();

        var r = await Run(Fx.Snap(60, orphan), Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }));

        Assert.Empty(_hw.HandBacks);
        Assert.Equal(FanModeState.StandIn, r.Fans["orph"].State);
        Assert.Equal(new[] { ("orph", 50.0) }, _hw.Writes);
        Assert.Equal(50.0, _c.Controlled["orph"]);
    }

    [Fact]
    public async Task ModeSwitch_IssuesEveryHandBackBeforeAnyDutyWrite()
    {
        var snap = Fx.Snap(60, Fx.Board("a"), Fx.Board("b"));
        await Run(snap, Fx.Settings(FanMode.Quiet));
        Assert.Equal(new[] { ("a", 35.0), ("b", 35.0) }, _hw.Writes);
        _hw.Clear();

        await Run(snap, Fx.Settings(FanMode.Synced, synced: 50));

        Assert.Equal(new (string, double?)[] { ("a", null), ("b", null), ("a", 50), ("b", 50) }, _hw.Calls);
    }

    [Fact]
    public async Task Individual_AtStartup_HandsNothingBack_AndWritesNothing_TheCardsOwnTheirFans()
    {
        var r = await Run(Fx.Snap(60, Fx.Board("f1", manual: true), Fx.Board("f2")), Fx.Settings(FanMode.Individual));

        Assert.Empty(_hw.Calls);
        Assert.All(r.Fans.Values, f => Assert.Equal(FanModeState.Individual, f.State));
    }

    [Fact]
    public async Task SwitchingFromIndividualToAuto_HandsBackTheFansLeftManualByTheCards()
    {
        var snap = Fx.Snap(60, Fx.Board("f1", manual: true), Fx.Board("f2"));
        await Run(snap, Fx.Settings(FanMode.Individual));
        Assert.Empty(_hw.Calls);

        await Run(snap, Fx.Settings(FanMode.Auto));

        Assert.Equal(new[] { "f1" }, _hw.HandBacks);
        Assert.Empty(_hw.Writes);
    }

    [Fact]
    public async Task Individual_DrivesOrphansWithTheStandInCurve_ButLeavesOtherFansToTheCards()
    {
        var r = await Run(Fx.Snap(50, Fx.Board("f1", manual: true), Fx.Board("orph")), Fx.Settings(FanMode.Individual, orphaned: new[] { "orph" }));

        Assert.Equal(new (string, double?)[] { ("orph", 40) }, _hw.Calls);
        Assert.Equal(FanModeState.Individual, r.Fans["f1"].State);
        Assert.Equal(FanModeState.StandIn, r.Fans["orph"].State);
    }

    // ───────────────────────────── Quiet: board profile ─────────────────────────────

    [Theory]
    [InlineData(40.0, 35.0)]
    [InlineData(75.0, 35.0)]   // floor up to and including 75 °C
    [InlineData(77.5, 42.5)]   // linear ramp 35 → 50 % between 75 and 80 °C
    [InlineData(79.0, 47.0)]
    public async Task Quiet_Board_Floor35PercentUpTo75C_ThenLinearRampTo50PercentAt80C(double cpuTemp, double expected)
    {
        var r = await Run(Fx.Snap(cpuTemp, Fx.Board("a")), Fx.Settings(FanMode.Quiet));

        Assert.Equal(FanModeState.Quiet, r.Fans["a"].State);
        Assert.Single(_hw.Writes);
        Assert.Equal("a", _hw.Writes[0].Id);
        Assert.Equal(expected, _hw.Writes[0].Percent, 1e-9);
        Assert.Equal(cpuTemp, r.Fans["a"].SourceTempC);
    }

    [Fact]
    public async Task Quiet_Board_FloorNeverGoesBelowTheFansOwnMinimum()
    {
        await Run(Fx.Snap(60, Fx.Board("a", min: 45)), Fx.Settings(FanMode.Quiet));

        Assert.Equal(new[] { ("a", 45.0) }, _hw.Writes);
    }

    [Fact]
    public async Task Quiet_Board_HandsOffAtSmoothed80C_AndHandsBackAFanItHolds()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(60, fan), quiet);
        Assert.Equal(new[] { ("a", 35.0) }, _hw.Writes);

        _clock.AdvanceSeconds(31); // > 3τ: the smoother restarts from the raw value, so smoothed == 80
        var r = await Run(Fx.Snap(80, fan), quiet);

        Assert.Equal(new (string, double?)[] { ("a", 35), ("a", null) }, _hw.Calls);
        Assert.Equal(FanModeState.HandedOff, r.Fans["a"].State);
        Assert.Equal(80.0, r.Fans["a"].SourceTempC);
        Assert.Empty(_c.Controlled);
    }

    [Fact]
    public async Task Quiet_Board_ResumesOnlyWhenSmoothedIsAtMost72C_AndTheHandOffHasLastedAtLeast30s()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);

        var r = await Run(Fx.Snap(80, fan), quiet); // t0: smoothed 80 → handed off (nothing held, nothing written)
        Assert.Equal(FanModeState.HandedOff, r.Fans["a"].State);
        Assert.Empty(_hw.Calls);

        _clock.AdvanceSeconds(10);
        r = await Run(Fx.Snap(70, fan), quiet); // smoothed ≈ 73.7 °C: still above the 72 °C resume point
        Assert.Equal(FanModeState.HandedOff, r.Fans["a"].State);
        Assert.True(r.Fans["a"].SourceTempC > 72);

        _clock.AdvanceSeconds(10);
        r = await Run(Fx.Snap(60, fan), quiet); // smoothed ≈ 65 °C, but handed off for only 20 s
        Assert.Equal(FanModeState.HandedOff, r.Fans["a"].State);
        Assert.True(r.Fans["a"].SourceTempC <= 72);
        Assert.Empty(_hw.Calls);

        _clock.AdvanceSeconds(10);
        r = await Run(Fx.Snap(60, fan), quiet); // 30 s held and cool → the curve resumes
        Assert.Equal(FanModeState.Quiet, r.Fans["a"].State);
        Assert.Equal(new[] { ("a", 35.0) }, _hw.Writes);
    }

    [Fact]
    public async Task Quiet_Board_EmergencyHandOffAtRaw90C_IgnoresTheSmoothedTemperature()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(60, fan), quiet);

        _clock.AdvanceSeconds(1);
        var r = await Run(Fx.Snap(95, fan), quiet); // smoothed ≈ 63 °C, raw 95 °C

        Assert.Equal(FanModeState.HandedOff, r.Fans["a"].State);
        Assert.True(r.Fans["a"].SourceTempC < 80);
        Assert.Equal(new (string, double?)[] { ("a", 35), ("a", null) }, _hw.Calls);
    }

    [Fact]
    public async Task Quiet_NoTemperature_HandsBackAHeldFan_AndReportsNoTemperature()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(60, fan), quiet);

        var r = await Run(Fx.Snap(null, fan), quiet);

        Assert.Equal(new (string, double?)[] { ("a", 35), ("a", null) }, _hw.Calls);
        Assert.Equal(FanModeState.NoTemperature, r.Fans["a"].State);
    }

    [Fact]
    public async Task Quiet_FanExcludedLater_IsHandedBack()
    {
        var fan = Fx.Board("a");
        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Quiet));

        var r = await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Quiet, excluded: new[] { "a" }));

        Assert.Equal(new (string, double?)[] { ("a", 35), ("a", null) }, _hw.Calls);
        Assert.Equal(FanModeState.Excluded, r.Fans["a"].State);
        Assert.Equal(FanExclusion.User, r.Fans["a"].Exclusion);
    }

    // ───────────────────────────── Quiet: GPU profile ─────────────────────────────

    [Theory]
    [InlineData(40.0, 30.0)]
    [InlineData(50.0, 30.0)]   // floor up to 50 °C
    [InlineData(60.0, 45.0)]   // linear ramp 30 → 60 % between 50 and 70 °C
    [InlineData(70.0, 60.0)]
    [InlineData(74.0, 60.0)]   // flat until the 75 °C hand-off
    public async Task Quiet_Gpu_Floor30PercentUpTo50C_RampTo60PercentAt70C(double gpuTemp, double expected)
    {
        var r = await Run(Fx.Snap(90, gpuTemp, Fx.Gpu()), Fx.Settings(FanMode.Quiet)); // CPU is hot: must be ignored for a GPU fan

        Assert.Equal(FanModeState.Quiet, r.Fans[Fx.GpuId + "/fan0"].State);
        Assert.True(r.Fans[Fx.GpuId + "/fan0"].IsGpuFan);
        Assert.Single(_hw.Writes);
        Assert.Equal(expected, _hw.Writes[0].Percent, 1e-9);
    }

    [Fact]
    public async Task Quiet_Gpu_HandsOffAt75C_ResumesAtMost68C_AfterTheMinimumHold()
    {
        var fan = Fx.Gpu();
        var quiet = Fx.Settings(FanMode.Quiet);

        var r = await Run(Fx.Snap(60, 75, fan), quiet);
        Assert.Equal(FanModeState.HandedOff, r.Fans[fan.Id].State);

        _clock.AdvanceSeconds(31); // smoother restarts: smoothed == 69 > 68
        r = await Run(Fx.Snap(60, 69, fan), quiet);
        Assert.Equal(FanModeState.HandedOff, r.Fans[fan.Id].State);
        Assert.Empty(_hw.Calls);

        _clock.AdvanceSeconds(10); // smoothed ≈ 63.3 ≤ 68 and held 41 s
        r = await Run(Fx.Snap(60, 60, fan), quiet);
        Assert.Equal(FanModeState.Quiet, r.Fans[fan.Id].State);
        // The resumed duty follows the GPU ramp at the smoothed temperature (30 % at 50 °C → 60 % at 70 °C), not the floor.
        var smoothed = 69 + (1 - Math.Exp(-1)) * (60 - 69);
        var write = Assert.Single(_hw.Writes);
        Assert.Equal(fan.Id, write.Id);
        Assert.Equal(30 + (smoothed - 50) / 20 * 30, write.Percent, 1e-6);
    }

    [Fact]
    public async Task Quiet_Gpu_EmergencyHandOffAtRaw85C()
    {
        var fan = Fx.Gpu();
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(60, 45, fan), quiet);

        _clock.AdvanceSeconds(1);
        var r = await Run(Fx.Snap(60, 85, fan), quiet);

        Assert.Equal(FanModeState.HandedOff, r.Fans[fan.Id].State);
        Assert.Equal(new (string, double?)[] { (fan.Id, 30), (fan.Id, null) }, _hw.Calls);
    }

    [Fact]
    public async Task Quiet_FanWithGpuIdPrefix_FollowsTheGpuTemperature_EvenWhenListedInTheBoardGroup()
    {
        var fan = Fx.Board(Fx.GpuId + "/fan0", group: Fx.BoardGroup);

        var r = await Run(Fx.Snap(60, 40, fan), Fx.Settings(FanMode.Quiet));

        Assert.True(r.Fans[fan.Id].IsGpuFan);
        Assert.Equal(new[] { (fan.Id, 30.0) }, _hw.Writes); // GPU floor, not the 35 % board floor
        Assert.Equal(40.0, r.Fans[fan.Id].SourceTempC);
    }

    [Theory]
    [InlineData("x", Fx.GpuGroup, true)]
    [InlineData("x", "GPU", true)]
    [InlineData(Fx.GpuId + "/fan1", Fx.BoardGroup, true)]
    [InlineData("fan1", Fx.BoardGroup, false)]
    [InlineData("gpu01/fan1", Fx.BoardGroup, false)] // the prefix must be the whole GPU id plus "/"
    public void IsGpuFan_ByGpuGroupLabel_OrByOwningGpuIdPrefix(string id, string group, bool expected)
    {
        var fan = Fx.Board(id, group: group);
        Assert.Equal(expected, FanModeController.IsGpuFan(fan, Fx.Snap(60, 50, fan)));
    }

    // ───────────────────────────── Quiet: write throttle ─────────────────────────────

    [Fact]
    public async Task Quiet_WriteThrottle_SkipsStepsSmallerThan3Percent_AndKeepsShowingTheAppliedDuty()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(76, fan), quiet);
        Assert.Equal(new[] { ("a", 38.0) }, _hw.Writes);

        _clock.AdvanceSeconds(10);
        var r = await Run(Fx.Snap(76.5, fan), quiet); // smoothed ≈ 76.3 → target ≈ 39 %, a step under 3 %

        Assert.Single(_hw.Writes);
        Assert.Equal(FanModeState.Quiet, r.Fans["a"].State);
        Assert.Equal(38.0, r.Fans["a"].Percent);
    }

    [Fact]
    public async Task Quiet_WriteThrottle_WaitsAtLeast4SecondsBetweenWritesToTheSameFan()
    {
        var fan = Fx.Board("a");
        var quiet = Fx.Settings(FanMode.Quiet);
        await Run(Fx.Snap(76, fan), quiet);
        Assert.Equal(new[] { ("a", 38.0) }, _hw.Writes);

        _clock.AdvanceSeconds(2);
        var s1 = Fx.Ema(76, 85, 2);
        var r = await Run(Fx.Snap(85, fan), quiet); // target ≈ 43 % (≥ 3 % step) but only 2 s since the last write
        Assert.True(QuietFanCurve.TargetFor(fan, false, s1) - 38 >= QuietFanCurve.MinStepPercent);
        Assert.Single(_hw.Writes);
        Assert.Equal(38.0, r.Fans["a"].Percent);

        _clock.AdvanceSeconds(2);
        var s2 = Fx.Ema(s1, 85, 2);
        r = await Run(Fx.Snap(85, fan), quiet); // 4 s since the last write → written
        Assert.Equal(2, _hw.Writes.Count);
        Assert.Equal(QuietFanCurve.TargetFor(fan, false, s2), _hw.Writes[1].Percent, 1e-9);
        Assert.Equal(_hw.Writes[1].Percent, r.Fans["a"].Percent!.Value, 1e-9);
    }

    [Fact]
    public async Task Quiet_FirstTakeoverAfterAModeSwitch_WritesImmediately_IgnoringTheIntervalThrottle()
    {
        var fan = Fx.Board("a");
        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Synced, synced: 50));
        Assert.Equal(new[] { ("a", 50.0) }, _hw.Writes);
        _hw.Clear();

        _clock.AdvanceSeconds(1); // well inside the 4 s interval
        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Quiet));

        Assert.Equal(new (string, double?)[] { ("a", null), ("a", 35) }, _hw.Calls);
    }

    // ───────────────────────────── Stand-in curve (orphans) ─────────────────────────────

    [Fact]
    public async Task StandIn_IncreasesAreWrittenImmediately_EvenUnder3PercentAndWithin4Seconds()
    {
        var orphan = Fx.Board("orph");
        var auto = Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" });
        await Run(Fx.Snap(50, orphan), auto);
        Assert.Equal(new[] { ("orph", 40.0) }, _hw.Writes);

        _clock.AdvanceSeconds(1);
        var smoothed = Fx.Ema(50, 70, 1); // ≈ 51.9 °C → ≈ 41.9 %: +1.9 % after 1 s
        var r = await Run(Fx.Snap(70, orphan), auto);

        Assert.Equal(2, _hw.Writes.Count);
        Assert.Equal(QuietFanCurve.StandInPercent(smoothed, false), _hw.Writes[1].Percent, 1e-9);
        Assert.Equal(FanModeState.StandIn, r.Fans["orph"].State);
    }

    [Fact]
    public async Task StandIn_DecreasesFollowThe3PercentAnd4SecondThrottle()
    {
        var orphan = Fx.Board("orph");
        var auto = Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" });
        await Run(Fx.Snap(80, orphan), auto);
        Assert.Equal(new[] { ("orph", 85.0) }, _hw.Writes);

        _clock.AdvanceSeconds(1);
        var s1 = Fx.Ema(80, 50, 1); // ≈ 77.1 °C → ≈ 77.9 %: a −7 % step after only 1 s
        var r = await Run(Fx.Snap(50, orphan), auto);
        Assert.True(85 - QuietFanCurve.StandInPercent(s1, false) >= QuietFanCurve.MinStepPercent);
        Assert.Single(_hw.Writes);
        Assert.Equal(85.0, r.Fans["orph"].Percent); // still shows the applied duty

        _clock.AdvanceSeconds(3);
        var s2 = Fx.Ema(s1, 50, 3);
        r = await Run(Fx.Snap(50, orphan), auto); // 4 s since the last write → the decrease is written
        Assert.Equal(2, _hw.Writes.Count);
        Assert.Equal(QuietFanCurve.StandInPercent(s2, false), _hw.Writes[1].Percent, 1e-9);
    }

    [Fact]
    public async Task StandIn_RawEmergencyTemperature_Writes100PercentAtOnce()
    {
        var orphan = Fx.Board("orph");
        var auto = Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" });
        await Run(Fx.Snap(50, orphan), auto);

        _clock.AdvanceSeconds(1);
        await Run(Fx.Snap(90, orphan), auto); // smoothed ≈ 53.8 °C, raw 90 °C

        Assert.Equal(new[] { ("orph", 40.0), ("orph", 100.0) }, _hw.Writes);
    }

    [Fact]
    public async Task StandIn_OrphanNamedPump_RunsFlatOut()
    {
        var r = await Run(Fx.Snap(50, Fx.Board("orph", name: "CPU Pump")), Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }));

        Assert.Equal(new[] { ("orph", 100.0) }, _hw.Writes);
        Assert.Equal(FanExclusion.PumpName, r.Fans["orph"].Exclusion);
    }

    [Fact]
    public async Task StandIn_PumpLikeOrphan_IsNotFlatOut_OnlyTheNameCounts()
    {
        var r = await Run(Fx.Snap(50, Fx.Board("orph")), Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }), FirstSight("orph", 50, 3000));

        Assert.Equal(FanExclusion.PumpLike, r.Fans["orph"].Exclusion);
        Assert.Equal(new[] { ("orph", 40.0) }, _hw.Writes);
    }

    [Fact]
    public async Task StandIn_UnknownTemperature_Writes100Percent()
    {
        var r = await Run(Fx.Snap(null, Fx.Board("orph")), Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }));

        Assert.Equal(new[] { ("orph", 100.0) }, _hw.Writes);
        Assert.Null(r.Fans["orph"].SourceTempC);
    }

    [Fact]
    public async Task StandIn_IsClampedToTheFansRange()
    {
        await Run(Fx.Snap(90, Fx.Board("orph", max: 70)), Fx.Settings(FanMode.Auto, orphaned: new[] { "orph" }));

        Assert.Equal(new[] { ("orph", 70.0) }, _hw.Writes);
    }

    // ───────────────────────────── Synced ─────────────────────────────

    [Fact]
    public async Task Synced_WritesTheSyncedPercentClampedToEachFansRange()
    {
        var r = await Run(Fx.Snap(60, Fx.Board("a", min: 20, max: 80), Fx.Board("b")), Fx.Settings(FanMode.Synced, synced: 90));

        Assert.Equal(new[] { ("a", 80.0), ("b", 90.0) }, _hw.Writes);
        Assert.Equal(FanModeState.Synced, r.Fans["a"].State);
        Assert.Equal(80.0, r.Fans["a"].Percent);
        Assert.Equal(90.0, r.Fans["b"].Percent);
    }

    [Fact]
    public async Task Synced_NonFiniteSyncedPercent_FallsBackTo50()
    {
        await Run(Fx.Snap(60, Fx.Board("a")), Fx.Settings(FanMode.Synced, synced: double.NaN));

        Assert.Equal(new[] { ("a", 50.0) }, _hw.Writes);
    }

    [Fact]
    public async Task Synced_ChangedSyncedPercent_IsWrittenAtOnce()
    {
        var fan = Fx.Board("a");
        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Synced, synced: 50));

        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Synced, synced: 60)); // same instant: no interval throttle for a new target

        Assert.Equal(new[] { ("a", 50.0), ("a", 60.0) }, _hw.Writes);
    }

    [Fact]
    public async Task Synced_RewritesOnlyWhenTheReportedDutyDriftsAtLeast5Percent_AndAtLeast4SecondsPassed()
    {
        var s = Fx.Settings(FanMode.Synced, synced: 50);
        await Run(Fx.Snap(60, Fx.Board("a", percent: 30)), s);
        Assert.Equal(new[] { ("a", 50.0) }, _hw.Writes);

        _clock.AdvanceSeconds(2);
        await Run(Fx.Snap(60, Fx.Board("a", percent: 50, manual: true)), s); // on target: nothing
        Assert.Single(_hw.Writes);

        await Run(Fx.Snap(60, Fx.Board("a", percent: 56, manual: true)), s); // drifted 6 %, but only 2 s since the write
        Assert.Single(_hw.Writes);

        _clock.AdvanceSeconds(2);
        await Run(Fx.Snap(60, Fx.Board("a", percent: 56, manual: true)), s); // 4 s → re-applied
        Assert.Equal(new[] { ("a", 50.0), ("a", 50.0) }, _hw.Writes);

        _clock.AdvanceSeconds(10);
        await Run(Fx.Snap(60, Fx.Board("a", percent: 54, manual: true)), s); // 4 % drift is tolerated
        Assert.Equal(2, _hw.Writes.Count);
    }

    [Theory]
    [InlineData("Fan #1", "a", null, null, FanExclusion.User)]
    [InlineData("CPU Pump", null, null, null, FanExclusion.PumpName)]
    [InlineData("水泵", null, null, null, FanExclusion.PumpName)]
    [InlineData("AIO", null, null, null, FanExclusion.PumpName)]
    [InlineData("Fan #2", null, 50.0, 3000.0, FanExclusion.PumpLike)]   // first seen ≥ 2800 RPM
    [InlineData("Fan #3", null, 95.0, 1800.0, FanExclusion.PumpLike)]   // first seen flat out: ≥ 95 % and ≥ 1800 RPM
    public async Task Synced_ExcludedFans_AreNotMembers_AndAreNeverWritten(string name, string? excludedId, double? firstPercent, double? firstRpm, FanExclusion expected)
    {
        var fan = Fx.Board("a", name: name);
        var presence = firstRpm is null && firstPercent is null ? null : FirstSight("a", firstPercent, firstRpm);

        var r = await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Synced, excluded: excludedId is null ? null : new[] { excludedId }), presence);

        Assert.Empty(_hw.Calls);
        Assert.Equal(FanModeState.Excluded, r.Fans["a"].State);
        Assert.Equal(expected, r.Fans["a"].Exclusion);
    }

    [Fact]
    public async Task Synced_IncludedId_OverridesThePumpHeuristic()
    {
        var r = await Run(Fx.Snap(60, Fx.Board("a", name: "CPU Pump")), Fx.Settings(FanMode.Synced, included: new[] { "a" }), FirstSight("a", 100, 3500));

        Assert.Equal(new[] { ("a", 50.0) }, _hw.Writes);
        Assert.Equal(FanModeState.Synced, r.Fans["a"].State);
        Assert.Equal(FanExclusion.None, r.Fans["a"].Exclusion);
    }

    [Fact]
    public async Task Synced_GpuFans_AreNeverPumpLike()
    {
        var fan = Fx.Gpu();

        var r = await Run(Fx.Snap(60, 50, fan), Fx.Settings(FanMode.Synced), FirstSight(fan.Id, 100, 3200));

        Assert.Equal(new[] { (fan.Id, 50.0) }, _hw.Writes);
        Assert.Equal(FanExclusion.None, r.Fans[fan.Id].Exclusion);
    }

    // ───────────────────────────── Stop / failures / availability / presence ─────────────────────────────

    [Fact]
    public async Task Stop_BeforeAnUpdate_WritesNothing_AndReportsNoFans()
    {
        _c.Stop();

        var r = await Run(Fx.Snap(60, Fx.Board("a", manual: true)), Fx.Settings(FanMode.Synced));

        Assert.True(_c.IsStopped);
        Assert.Empty(_hw.Calls);
        Assert.Empty(r.Fans);
    }

    [Fact]
    public async Task Stop_MidFlight_AbortsTheRemainingCalls_AndNothingIsEverWrittenAgain()
    {
        _hw.OnSet = (_, _) => { _c.Stop(); return Task.CompletedTask; };
        var snap = Fx.Snap(60, Fx.Board("a"), Fx.Board("b"));

        await Run(snap, Fx.Settings(FanMode.Synced));
        Assert.Single(_hw.Calls);

        _hw.OnSet = null;
        await Run(snap, Fx.Settings(FanMode.Synced));
        await Run(snap, Fx.Settings(FanMode.Auto));
        Assert.Single(_hw.Calls);
    }

    [Fact]
    public async Task FailedHandBack_KeepsTheFanInControlled_AndReportsTheError()
    {
        var fan = Fx.Board("a");
        await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Quiet));
        Assert.Equal(35.0, _c.Controlled["a"]);

        _hw.FailingIds.Add("a");
        var r = await Run(Fx.Snap(60, fan), Fx.Settings(FanMode.Auto));

        Assert.Equal(new[] { "a" }, _hw.HandBacks); // attempted
        Assert.Equal(35.0, _c.Controlled["a"]);     // still ours: the next evaluation retries
        Assert.NotNull(r.Fans["a"].Error);
    }

    [Fact]
    public async Task FailedWrite_SuppressesWritesToThatFanFor4Seconds_ThenRetries()
    {
        _hw.FailingIds.Add("a");
        var s = Fx.Settings(FanMode.Synced);
        var snap = Fx.Snap(60, Fx.Board("a"));

        var r = await Run(snap, s);
        Assert.Single(_hw.CallsFor("a"));
        Assert.NotNull(r.Fans["a"].Error);
        Assert.Empty(_c.Controlled);

        _hw.FailingIds.Clear();
        _clock.AdvanceSeconds(2);
        r = await Run(snap, s);
        Assert.Single(_hw.CallsFor("a"));
        Assert.Null(r.Fans["a"].Error);

        _clock.AdvanceSeconds(2);
        await Run(snap, s);
        Assert.Equal(2, _hw.CallsFor("a").Count);
        Assert.Equal(50.0, _c.Controlled["a"]);
    }

    [Fact]
    public async Task FailedWrite_DoesNotStopTheOtherFansFromBeingWritten()
    {
        _hw.FailingIds.Add("a");

        var r = await Run(Fx.Snap(60, Fx.Board("a"), Fx.Board("b")), Fx.Settings(FanMode.Synced));

        Assert.Equal(new[] { ("a", 50.0), ("b", 50.0) }, _hw.Writes);
        Assert.NotNull(r.Fans["a"].Error);
        Assert.Null(r.Fans["b"].Error);
        Assert.Equal(new[] { "b" }, _c.Controlled.Keys);
    }

    [Theory]
    [InlineData(FanMode.Individual)]
    [InlineData(FanMode.Auto)]
    [InlineData(FanMode.Quiet)]
    [InlineData(FanMode.Synced)]
    public async Task ControlUnavailable_MarksEveryFanUnavailable_AndWritesNothing(FanMode mode)
    {
        var snap = Fx.Snap(95, Fx.Board("f1", manual: true), Fx.Board("orph", manual: true), Fx.Gpu());

        var r = await Run(snap, Fx.Settings(mode, orphaned: new[] { "orph" }), controlAvailable: false);

        Assert.Empty(_hw.Calls);
        Assert.False(r.ControlAvailable);
        Assert.Equal(3, r.Fans.Count);
        Assert.All(r.Fans.Values, f => Assert.Equal(FanModeState.Unavailable, f.State));
    }

    [Fact]
    public async Task ControlUnavailable_ForgetsTheMode_SoTheNextAvailableSnapshotHandsBackAgain()
    {
        var auto = Fx.Settings(FanMode.Auto);
        var snap = Fx.Snap(60, Fx.Board("f1", manual: true));

        await Run(snap, auto);
        await Run(snap, auto); // same mode: a fan still reported manual is not handed back again
        Assert.Equal(new[] { "f1" }, _hw.HandBacks);

        await Run(snap, auto, controlAvailable: false);
        await Run(snap, auto);

        Assert.Equal(new[] { "f1", "f1" }, _hw.HandBacks);
    }

    [Theory]
    [InlineData(FanMode.Quiet, FanPresence.Hidden)]
    [InlineData(FanMode.Quiet, FanPresence.Pending)]
    [InlineData(FanMode.Synced, FanPresence.Hidden)]
    [InlineData(FanMode.Synced, FanPresence.Pending)]
    public async Task HiddenAndPendingFans_AreNeverWrittenInQuietOrSynced(FanMode mode, FanPresence presence)
    {
        var r = await Run(Fx.Snap(60, Fx.Board("a"), Fx.Board("b")), Fx.Settings(mode), PresenceOf("a", presence));

        Assert.Equal(FanModeState.NotDetected, r.Fans["a"].State);
        Assert.Equal(presence, r.Fans["a"].Presence);
        Assert.Single(_hw.Writes);
        Assert.Equal("b", _hw.Writes[0].Id);
    }

    [Fact]
    public async Task HiddenFan_HeldByUs_IsHandedBack()
    {
        var fan = Fx.Board("a");
        var s = Fx.Settings(FanMode.Synced);
        await Run(Fx.Snap(60, fan), s);

        var r = await Run(Fx.Snap(60, fan), s, PresenceOf("a", FanPresence.Hidden));

        Assert.Equal(new (string, double?)[] { ("a", 50), ("a", null) }, _hw.Calls);
        Assert.Equal(FanModeState.NotDetected, r.Fans["a"].State);
        Assert.Empty(_c.Controlled);
    }

    [Theory]
    [InlineData(FanMode.Auto)]
    [InlineData(FanMode.Quiet)]
    [InlineData(FanMode.Synced)]
    public async Task NotControllableFans_AreNeverWritten_EvenWhenOrphaned(FanMode mode)
    {
        var r = await Run(Fx.Snap(60, Fx.Board("a", manual: true, canControl: false)), Fx.Settings(mode, orphaned: new[] { "a" }));

        Assert.Empty(_hw.Calls);
        Assert.Equal(FanModeState.NotControllable, r.Fans["a"].State);
    }

    [Fact]
    public async Task Result_ReportsModeAvailabilityAndSourceTemperatures()
    {
        var r = await Run(Fx.Snap(61.5, 48.25, Fx.Board("a")), Fx.Settings(FanMode.Quiet));

        Assert.Equal(FanMode.Quiet, r.Mode);
        Assert.True(r.ControlAvailable);
        Assert.Equal(61.5, r.CpuTempC);
        Assert.Equal(48.25, r.GpuTempC);
    }
}
