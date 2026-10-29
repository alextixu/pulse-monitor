using Pulse.Core.FanControl;
using Pulse.Core.Models;
using FanMemoryEntry = Pulse.Core.Models.FanMemory;

namespace Pulse.Engine.Memory;

/// <summary>
/// Boot-scoped memory of the <see cref="FanPresenceTracker"/> (first sample per fan, whether it ever spun), so pump and
/// hidden-header decisions stay the same when the agent restarts within one boot. After a reboot the firmware curves
/// are the ground truth again and the memory is discarded. A fan that is manual or orphaned when first seen is never
/// recorded: its first sample shows a duty some process forced, not the firmware's, and would mislead the pump
/// heuristic. Fed from the hardware thread only (like the tracker).
/// </summary>
internal sealed class FanPresenceMemory
{
    /// <summary>Same tolerance as fan-recovery.json: boot times computed from the tick count drift a little.</summary>
    public static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>The first export waits for this many polls (the presence grace period is 3).</summary>
    public const int ExportAfterPolls = 5;

    private readonly HashSet<string> _tainted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _persisted = new(StringComparer.Ordinal);
    private IReadOnlyList<FanMemoryEntry>? _lastExport;
    private int _polls;

    public FanPresenceMemory(DateTimeOffset bootTime) => BootTime = bootTime;

    /// <summary>When this boot started (UtcNow − TickCount64), stored next to the entries.</summary>
    public DateTimeOffset BootTime { get; }

    public static DateTimeOffset CurrentBootTime => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static bool IsSameBoot(DateTimeOffset? stored, DateTimeOffset current)
        => stored is { } s && (s - current).Duration() < SameBootTolerance;

    /// <summary>Seeds <paramref name="tracker"/> from state.json when it was written during this boot; returns the count.</summary>
    public int Seed(FanPresenceTracker tracker, PulseState state)
    {
        if (!IsSameBoot(state.FanBootTime, BootTime)) return 0;
        var seeded = 0;
        foreach (var fan in state.Fans)
        {
            if (string.IsNullOrEmpty(fan.FanId)) continue;
            tracker.Seed(fan.FanId, new FanFirstSight(fan.FirstPercent, fan.FirstRpm), fan.EverSpun);
            _persisted.Add(fan.FanId);
            seeded++;
        }
        if (seeded > 0) _lastExport = state.Fans;
        return seeded;
    }

    /// <summary>
    /// Call before <see cref="FanPresenceTracker.Observe"/> with the same snapshot: marks fans that are seen for the first
    /// time while manual or orphaned.
    /// </summary>
    public void BeforeObserve(FanPresenceTracker tracker, IReadOnlyList<FanInfo> fans, IReadOnlySet<string> orphanedIds)
    {
        foreach (var fan in fans)
        {
            if (tracker.FirstSight(fan.Id) is not null) continue;
            if (fan.IsManual || orphanedIds.Contains(fan.Id)) _tainted.Add(fan.Id);
        }
    }

    /// <summary>
    /// Call after <see cref="FanPresenceTracker.Observe"/>: the entries to persist when an export is due (after the first
    /// <see cref="ExportAfterPolls"/> polls, then whenever they changed), else null.
    /// </summary>
    public IReadOnlyList<FanMemoryEntry>? AfterObserve(FanPresenceTracker tracker, IReadOnlyList<FanInfo> fans, IReadOnlySet<string> orphanedIds)
    {
        if (++_polls < ExportAfterPolls) return null;

        var current = new Dictionary<string, FanInfo>(fans.Count, StringComparer.Ordinal);
        foreach (var fan in fans) current[fan.Id] = fan;

        var entries = new List<FanMemoryEntry>();
        foreach (var (id, first, everSpun) in tracker.Export())
        {
            if (_tainted.Contains(id)) continue;
            // A fan not yet persisted is only recorded while nobody forces its duty (capture guard).
            var forced = current.TryGetValue(id, out var fan) && fan.IsManual || orphanedIds.Contains(id);
            if (forced && !_persisted.Contains(id)) continue;
            entries.Add(new FanMemoryEntry(id, first.Percent, first.Rpm, everSpun));
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.FanId, b.FanId));

        if (_lastExport is not null && _lastExport.Count == entries.Count && _lastExport.OrderBy(e => e.FanId, StringComparer.Ordinal).SequenceEqual(entries))
        {
            return null;
        }

        _lastExport = entries;
        foreach (var e in entries) _persisted.Add(e.FanId);
        return entries;
    }
}
