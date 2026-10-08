using Pulse.Core.Models;

namespace Pulse.Core.FanControl;

/// <summary>Why a controllable fan is not part of the Quiet / Synced group.</summary>
public enum FanExclusion
{
    None,
    /// <summary>The user switched "納入群組控制" off.</summary>
    User,
    /// <summary>Name contains "pump" / "泵" / "AIO".</summary>
    PumpName,
    /// <summary>First seen at ≥ 2800 RPM, or flat out (≥ 95 % duty and ≥ 1800 RPM): pump headers usually do.</summary>
    PumpLike,
}

/// <summary>Whether a fan header is shown at all (unconnected Super I/O headers report 0 / no RPM forever).</summary>
public enum FanPresence
{
    /// <summary>Shown and eligible for group control.</summary>
    Visible,
    /// <summary>Still inside the grace period without any RPM: shown, but never written by a group mode.</summary>
    Pending,
    /// <summary>No RPM in any sample after the grace period (and not a GPU fan): hidden, never written.</summary>
    Hidden,
}

/// <summary>What the policy needs to remember about the first time this session saw a fan.</summary>
public readonly record struct FanFirstSight(double? Percent, double? Rpm);

/// <summary>Group membership rules for Quiet / Synced.</summary>
public static class FanGroupPolicy
{
    public const double PumpDutyPercent = 95;
    public const double PumpRpm = 1800;

    /// <summary>GPU fans: the hardware group label is "顯示卡" (LHM) or "GPU" (older demo data).</summary>
    public static bool IsGpuGroup(string? group)
        => group is not null && (group == "顯示卡" || group.Equals("GPU", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikePumpName(string? name)
        => !string.IsNullOrEmpty(name)
           && (name.Contains("pump", StringComparison.OrdinalIgnoreCase)
               || name.Contains('泵')
               || name.Contains("AIO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Case fans (120/140 mm) top out around 1500–2000 RPM and 92 mm CPU fans around 2500, while AIO pumps report
    /// 2500–4500 RPM even at partial duty. Seen on the test machine: two Z790 headers at 3971 RPM / 61 % and 3176 RPM / 36 %.
    /// </summary>
    public const double PumpHighRpm = 2800;

    public static bool LooksLikePumpDuty(FanFirstSight? first)
        => first is { Rpm: { } r } && (r >= PumpHighRpm || first.Value.Percent is { } p && p >= PumpDutyPercent && r >= PumpRpm);

    /// <summary>
    /// Membership of a controllable fan. Explicit choices win (exclude over include); otherwise the pump heuristic
    /// (name, or flat-out duty when first seen) excludes it.
    /// </summary>
    public static FanExclusion Evaluate(FanInfo fan, FanFirstSight? firstSight, IReadOnlySet<string> excludedIds, IReadOnlySet<string> includedIds)
    {
        if (excludedIds.Contains(fan.Id)) return FanExclusion.User;
        if (includedIds.Contains(fan.Id)) return FanExclusion.None;
        if (LooksLikePumpName(fan.Name)) return FanExclusion.PumpName;
        // GPU fans legitimately reach ~3000 RPM at full speed and are never pumps.
        if (!IsGpuGroup(fan.Group) && LooksLikePumpDuty(firstSight)) return FanExclusion.PumpLike;
        return FanExclusion.None;
    }
}

/// <summary>
/// Session memory per fan: the first sample (for the pump heuristic) and whether it ever spun (for hiding
/// unconnected headers). Not thread-safe: feed it from one thread (the UI thread in the app).
/// </summary>
public sealed class FanPresenceTracker
{
    /// <summary>Samples without RPM before a non-GPU header is declared unconnected.</summary>
    public const int GraceSamples = 3;

    private readonly Dictionary<string, Entry> _fans = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public FanFirstSight First;
        public int Samples;
        public bool EverSpun;
        public bool IsGpu;
    }

    /// <summary>Records one hardware sample (every fan in it).</summary>
    public void Observe(IReadOnlyList<FanInfo> fans)
    {
        foreach (var fan in fans)
        {
            if (!_fans.TryGetValue(fan.Id, out var e))
            {
                e = new Entry { First = new FanFirstSight(fan.Percent, fan.Rpm) };
                _fans[fan.Id] = e;
            }
            e.Samples++;
            e.IsGpu = FanGroupPolicy.IsGpuGroup(fan.Group);
            if (fan.Rpm is > 0) e.EverSpun = true;
        }
    }

    public FanFirstSight? FirstSight(string fanId) => _fans.TryGetValue(fanId, out var e) ? e.First : null;

    /// <summary>Visible once it reported RPM &gt; 0 (sticky for the session) or when it is a GPU fan (zero-RPM idle is normal there).</summary>
    public FanPresence PresenceOf(string fanId)
    {
        if (!_fans.TryGetValue(fanId, out var e)) return FanPresence.Pending;
        if (e.EverSpun || e.IsGpu) return FanPresence.Visible;
        return e.Samples < GraceSamples ? FanPresence.Pending : FanPresence.Hidden;
    }

    /// <summary>Immutable copy for another thread (the fan-mode controller).</summary>
    public FanPresenceSnapshot Capture()
    {
        var presence = new Dictionary<string, FanPresence>(_fans.Count, StringComparer.Ordinal);
        var first = new Dictionary<string, FanFirstSight>(_fans.Count, StringComparer.Ordinal);
        foreach (var (id, e) in _fans)
        {
            presence[id] = PresenceOf(id);
            first[id] = e.First;
        }
        return new FanPresenceSnapshot(presence, first);
    }
}

/// <summary>Point-in-time copy of <see cref="FanPresenceTracker"/>.</summary>
public sealed class FanPresenceSnapshot
{
    public FanPresenceSnapshot(IReadOnlyDictionary<string, FanPresence> presence, IReadOnlyDictionary<string, FanFirstSight> firstSight)
    {
        Presence = presence;
        FirstSights = firstSight;
    }

    /// <summary>Everything visible: used when no tracker is supplied.</summary>
    public static FanPresenceSnapshot AllVisible { get; } = new(new Dictionary<string, FanPresence>(), new Dictionary<string, FanFirstSight>());

    public IReadOnlyDictionary<string, FanPresence> Presence { get; }
    public IReadOnlyDictionary<string, FanFirstSight> FirstSights { get; }

    public FanPresence PresenceOf(string id) => Presence.TryGetValue(id, out var p) ? p : FanPresence.Visible;
    public FanFirstSight? FirstSight(string id) => FirstSights.TryGetValue(id, out var f) ? f : null;
}
