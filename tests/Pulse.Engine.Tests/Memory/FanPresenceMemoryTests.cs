using Pulse.Core.FanControl;
using Pulse.Core.Models;
using Pulse.Engine.Memory;
using Pulse.Engine.Tests.Fakes;

namespace Pulse.Engine.Tests.Memory;

public class FanPresenceMemoryTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> NoOrphans = new HashSet<string>();

    private static PulseState Stored(DateTimeOffset? boot) => new()
    {
        FanBootTime = boot,
        Fans = new[] { new FanMemory("pump", 100, 3900, true), new FanMemory("hdr6", 0, 0, false) },
    };

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(-90, true)]
    [InlineData(180, false)]
    public void SeedsOnlyFromTheSameBoot(int driftSeconds, bool seeded)
    {
        var tracker = new FanPresenceTracker();
        var memory = new FanPresenceMemory(Boot);
        var count = memory.Seed(tracker, Stored(Boot.AddSeconds(driftSeconds)));

        Assert.Equal(seeded ? 2 : 0, count);
        Assert.Equal(seeded ? new FanFirstSight(100, 3900) : (FanFirstSight?)null, tracker.FirstSight("pump"));
    }

    [Fact]
    public void WithoutABootTimeNothingIsSeeded()
    {
        var tracker = new FanPresenceTracker();
        Assert.Equal(0, new FanPresenceMemory(Boot).Seed(tracker, Stored(null)));
    }

    [Fact]
    public void ExportsAfterFivePollsThenOnlyOnChange()
    {
        var tracker = new FanPresenceTracker();
        var memory = new FanPresenceMemory(Boot);
        var fans = new[] { FakeHardwareMonitor.Fan("cpu", rpm: 0) };

        for (var i = 1; i < FanPresenceMemory.ExportAfterPolls; i++)
        {
            Assert.Null(Poll(tracker, memory, fans));
        }
        var first = Poll(tracker, memory, fans);
        Assert.NotNull(first);
        Assert.False(Assert.Single(first!).EverSpun);

        Assert.Null(Poll(tracker, memory, fans));

        // The fan spins up: that is a change worth persisting.
        var spun = Poll(tracker, memory, new[] { FakeHardwareMonitor.Fan("cpu", rpm: 800) });
        Assert.True(Assert.Single(spun!).EverSpun);
    }

    [Fact]
    public void NeverRecordsAFanThatWasManualOrOrphanedWhenFirstSeen()
    {
        var tracker = new FanPresenceTracker();
        var memory = new FanPresenceMemory(Boot);
        var orphaned = new HashSet<string> { "orphan" };
        var fans = new[]
        {
            FakeHardwareMonitor.Fan("clean"),
            FakeHardwareMonitor.Fan("stuck", manual: true, percent: 35),
            FakeHardwareMonitor.Fan("orphan"),
        };

        IReadOnlyList<FanMemory>? export = null;
        for (var i = 0; i < FanPresenceMemory.ExportAfterPolls; i++) export = Poll(tracker, memory, fans, orphaned);
        Assert.Equal(new[] { "clean" }, export!.Select(f => f.FanId));

        // Later the stuck fan is handed back: its first sight is still the forced duty, so it stays unrecorded.
        var later = new[] { FakeHardwareMonitor.Fan("clean"), FakeHardwareMonitor.Fan("stuck"), FakeHardwareMonitor.Fan("orphan") };
        Assert.Null(Poll(tracker, memory, later, orphaned));
    }

    [Fact]
    public void ANewFanIsOnlyRecordedWhileNobodyForcesItsDuty()
    {
        var tracker = new FanPresenceTracker();
        var memory = new FanPresenceMemory(Boot);
        for (var i = 1; i < FanPresenceMemory.ExportAfterPolls; i++) Poll(tracker, memory, new[] { FakeHardwareMonitor.Fan("cpu") });

        // Manual (a per-fan card) at the first export: not recorded yet …
        Assert.Empty(Poll(tracker, memory, new[] { FakeHardwareMonitor.Fan("cpu", manual: true) })!);
        // … and recorded once it is back with the firmware (its first sight was clean).
        Assert.Equal("cpu", Assert.Single(Poll(tracker, memory, new[] { FakeHardwareMonitor.Fan("cpu") })!).FanId);
        // Once recorded it stays, manual or not.
        Assert.Null(Poll(tracker, memory, new[] { FakeHardwareMonitor.Fan("cpu", manual: true) }));
    }

    [Fact]
    public void SeededFansStayRecordedEvenWhenManualNow()
    {
        var tracker = new FanPresenceTracker();
        var memory = new FanPresenceMemory(Boot);
        memory.Seed(tracker, Stored(Boot));
        var fans = new[] { FakeHardwareMonitor.Fan("pump", manual: true), FakeHardwareMonitor.Fan("hdr6", rpm: 0) };

        IReadOnlyList<FanMemory>? export = null;
        for (var i = 0; i < FanPresenceMemory.ExportAfterPolls; i++) export ??= Poll(tracker, memory, fans);
        // Same entries as stored: nothing to write.
        Assert.Null(export);
    }

    private static IReadOnlyList<FanMemory>? Poll(FanPresenceTracker tracker, FanPresenceMemory memory, IReadOnlyList<FanInfo> fans, IReadOnlySet<string>? orphaned = null)
    {
        memory.BeforeObserve(tracker, fans, orphaned ?? NoOrphans);
        tracker.Observe(fans);
        return memory.AfterObserve(tracker, fans, orphaned ?? NoOrphans);
    }
}
