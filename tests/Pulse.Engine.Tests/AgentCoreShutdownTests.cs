using System.Diagnostics;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests;

public class AgentCoreShutdownTests
{
    [Fact]
    public async Task FansAreHandedBackFirstEvenWhenTheHardwareThreadIsStuck()
    {
        var order = new OrderLog();
        var stuck = new ManualResetEventSlim(false);
        var hardware = new FakeHardwareMonitor { Order = order, Gate = stuck, Fans = () => new[] { FakeHardwareMonitor.Fan("cpu") } };
        var rgb = new FakeRgbController { Order = order };
        var launcher = new FakeLauncher { Order = order };
        var settings = new RecordingSettingsStore { Order = order };
        var state = new RecordingStateStore { Order = order };
        var agent = TestAgent.Create(hardware, rgb, settings, state, launcher);
        agent.HardwareStopTimeout = TimeSpan.FromMilliseconds(300);
        agent.Exiting += (_, reason) => order.Add($"Exiting:{reason}");
        try
        {
            await agent.StartAsync();
            // A change still waiting for its debounced save when the shutdown starts.
            await agent.UpdateSettingAsync(SettingsPatch.Of(nameof(AppSettings.Theme), AppTheme.Dark));

            var watch = Stopwatch.StartNew();
            var shutdown = agent.ShutdownAsync(AgentExitReason.Quit);
            // The hand-back is synchronous: done before ShutdownAsync even returned its task.
            Assert.Equal(1, hardware.DisposeCount);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));

            var entries = order.Entries;
            Assert.Equal("hardware.Dispose", entries[0]);
            Assert.True(order.IndexOf("Exiting:Quit") > order.IndexOf("hardware.Dispose"));
            Assert.True(order.IndexOf("rgb.Dispose") > order.IndexOf("Exiting:Quit"));
            Assert.True(order.IndexOf("launcher.Dispose") > order.IndexOf("rgb.Dispose"));
            Assert.True(order.IndexOf("settings.Save") > order.IndexOf("launcher.Dispose"));
            Assert.True(order.IndexOf("state.Save") > order.IndexOf("launcher.Dispose"));
            Assert.Equal(AppTheme.Dark, settings.Saves[^1].Theme);
            Assert.Empty(hardware.Writes);

            // Idempotent: the same task, nothing disposed twice.
            Assert.Same(shutdown, agent.ShutdownAsync(AgentExitReason.Relaunch));
            Assert.Equal(1, hardware.DisposeCount);
        }
        finally
        {
            stuck.Set();
        }
    }

    [Fact]
    public async Task ShutdownOfAnEngineThatNeverStartedStillRestoresTheFans()
    {
        var hardware = new FakeHardwareMonitor();
        var agent = TestAgent.Create(hardware);
        await agent.ShutdownAsync(AgentExitReason.Quit).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hardware.DisposeCount);
    }

    [Fact]
    public async Task AbnormalExitRestoresOnceAndIsANoOpAfterAShutdown()
    {
        var hardware = new FakeHardwareMonitor();
        var agent = TestAgent.Create(hardware);
        await agent.StartAsync();

        agent.RestoreFansOnAbnormalExit("test");
        agent.RestoreFansOnAbnormalExit("test again");
        Assert.Equal(1, hardware.DisposeCount);

        await agent.ShutdownAsync(AgentExitReason.Quit).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hardware.DisposeCount);

        var second = new FakeHardwareMonitor();
        var other = TestAgent.Create(second);
        await other.StartAsync();
        await other.ShutdownAsync(AgentExitReason.Quit).WaitAsync(TimeSpan.FromSeconds(10));
        other.RestoreFansOnAbnormalExit("process exit");
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task QuitAndElevationAskTheHostToShutDown()
    {
        var agent = TestAgent.Create();
        var requests = new List<AgentExitReason>();
        agent.ShutdownRequested += (_, r) => { lock (requests) requests.Add(r); };
        agent.RequestQuit();
        agent.RequestQuit();
        Assert.True(await TestAgent.Eventually(() => { lock (requests) return requests.Count == 1; }));
        Assert.Equal(AgentExitReason.Quit, requests[0]);

        // NullElevationService cannot relaunch: a failure, no shutdown.
        var elevation = await agent.ElevateAsync(showUi: false);
        Assert.False(elevation.Ok);
        await agent.ShutdownAsync(AgentExitReason.Quit);
    }
}
