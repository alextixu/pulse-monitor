using Pulse.Core.Abstractions;
using Pulse.Core.FanControl;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests;

public class AgentCoreFanTests
{
    private static FakeHardwareMonitor Monitor(params string[] orphaned)
    {
        var hardware = new FakeHardwareMonitor
        {
            Fans = () => new[]
            {
                FakeHardwareMonitor.Fan("cpu"),
                FakeHardwareMonitor.Fan("monitor-only", canControl: false),
                FakeHardwareMonitor.Fan("orphan"),
            },
        };
        if (orphaned.Length > 0) hardware.SetStatus(hardware.Status with { OrphanedFanIds = orphaned });
        return hardware;
    }

    private static async Task<AgentCore> StartedAgent(FakeHardwareMonitor hardware)
    {
        var agent = TestAgent.Create(hardware);
        await agent.StartAsync();
        Assert.True(await TestAgent.Eventually(() => agent.Hardware.Snapshot.Fans.Count == 3 && agent.Hardware.Fans.Fans.Count == 3));
        return agent;
    }

    [Fact]
    public async Task AManualWriteIsAcceptedForAControllableFanInIndividualMode()
    {
        var hardware = Monitor();
        await using var agent = await StartedAgent(hardware);
        var polls = hardware.Polls;

        var result = await agent.SetFanAsync("cpu", 42);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(("cpu", (double?)42), hardware.Writes);
        // The write is followed by a poll that re-evaluates the fans.
        Assert.True(await TestAgent.Eventually(() => hardware.Polls > polls));

        Assert.True((await agent.SetFanAsync("cpu", null)).Ok);
        Assert.Equal(("cpu", (double?)null), hardware.Writes[^1]);
    }

    [Fact]
    public async Task ManualWritesAreRejectedOutsideTheirRules()
    {
        var hardware = Monitor("orphan");
        await using var agent = await StartedAgent(hardware);

        Assert.Equal(EngineStrings.FanUnknown, (await agent.SetFanAsync("nope", 50)).Error);
        Assert.Equal(EngineStrings.FanNotControllable, (await agent.SetFanAsync("monitor-only", 50)).Error);
        Assert.Equal(EngineStrings.FanOrphaned, (await agent.SetFanAsync("orphan", 50)).Error);
        Assert.Equal(EngineStrings.FanInvalidPercent, (await agent.SetFanAsync("cpu", double.NaN)).Error);
        Assert.Equal(EngineStrings.FanUnknownId, (await agent.SetFanAsync("", 50)).Error);

        Assert.True((await agent.SetFanModeAsync(FanMode.Quiet)).Ok);
        Assert.Equal(EngineStrings.FanNotIndividual, (await agent.SetFanAsync("cpu", 50)).Error);

        Assert.DoesNotContain(hardware.Writes, w => w.Id is "nope" or "monitor-only" || w.Id == "cpu" && w.Percent == 50);
    }

    [Fact]
    public async Task ManualWritesNeedFanControl()
    {
        var hardware = Monitor();
        await using var agent = await StartedAgent(hardware);

        hardware.SetStatus(hardware.Status with { FanControlAvailable = false, State = HardwareMonitorState.Degraded });
        Assert.True(await TestAgent.Eventually(() => !agent.HardwareStatus.FanControlAvailable));

        var result = await agent.SetFanAsync("cpu", 50);
        Assert.False(result.Ok);
        Assert.Equal(Strings.FanControlUnavailable, result.Error);
        Assert.True(await TestAgent.Eventually(() => agent.Hardware.Fans.Fans["cpu"].State == FanModeState.Unavailable));
    }

    [Fact]
    public async Task AModeChangeIsAppliedByTheNextEvaluation()
    {
        var hardware = Monitor();
        await using var agent = await StartedAgent(hardware);

        Assert.True((await agent.SetFanModeAsync(FanMode.Synced)).Ok);
        Assert.True((await agent.SetSyncedPercentAsync(60)).Ok);
        Assert.True(await TestAgent.Eventually(() => agent.Hardware.Fans.Mode == FanMode.Synced && agent.Hardware.Fans.Fans["cpu"].State == FanModeState.Synced));
        Assert.True(await TestAgent.Eventually(() => hardware.Writes.Contains(("cpu", 60))));
    }

    [Fact]
    public async Task ManualWritesFailOnceShutdownStarted()
    {
        var hardware = Monitor();
        var agent = await StartedAgent(hardware);
        await agent.ShutdownAsync(AgentExitReason.Quit);
        var result = await agent.SetFanAsync("cpu", 50);
        Assert.False(result.Ok);
        Assert.DoesNotContain(("cpu", (double?)50), hardware.Writes);
    }
}

public class AgentCoreWatchdogTests
{
    [Fact]
    public async Task AStuckMonitorIsReportedFailedAndRecovers()
    {
        var gate = new ManualResetEventSlim(true);
        var hardware = new FakeHardwareMonitor { Gate = gate, Fans = () => new[] { FakeHardwareMonitor.Fan("cpu") } };
        var agent = TestAgent.Create(hardware);
        agent.WatchdogTimeout = TimeSpan.FromMilliseconds(600);
        try
        {
            await agent.StartAsync();
            Assert.True(await TestAgent.Eventually(() => agent.Hardware.Fans.Fans.ContainsKey("cpu")));

            gate.Reset(); // the next sensor read hangs
            Assert.True(await TestAgent.Eventually(() => agent.HardwareStatus.State == HardwareMonitorState.Failed));
            Assert.Equal(EngineStrings.HardwareStalled, agent.HardwareStatus.Message);
            Assert.False(agent.Hardware.Fans.ControlAvailable);
            Assert.Equal(FanModeState.Unavailable, agent.Hardware.Fans.Fans["cpu"].State);

            gate.Set(); // reads resume
            Assert.True(await TestAgent.Eventually(() => agent.HardwareStatus.State == HardwareMonitorState.Ready));
            Assert.True(await TestAgent.Eventually(() => agent.Hardware.Fans.ControlAvailable));
        }
        finally
        {
            gate.Set();
            await agent.ShutdownAsync(AgentExitReason.Quit);
        }
    }
}
