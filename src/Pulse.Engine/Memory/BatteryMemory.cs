using Pulse.Core.Localization;
using Pulse.Core.Models;

namespace Pulse.Engine.Memory;

/// <summary>
/// Remembers every battery device that was ever reported (state.json) and merges each poll into that memory, so the list
/// neither forgets a device that is switched off nor flaps when a provider misses a poll:
/// <list type="bullet">
/// <item>A connected report replaces the remembered device and stamps <see cref="RememberedBattery.LastConnectedAt"/>.</item>
/// <item>A disconnected report keeps the remembered Percent / voltage when it has none of its own.</item>
/// <item>A device that was shown connected and then goes missing (or reports disconnected) stays shown connected with
/// Status Unknown and 「休眠中」 until it has been absent for <see cref="HysteresisPolls"/> consecutive polls and at
/// least <see cref="HysteresisTime"/> since it was last connected; then it is shown disconnected with 「上次 …」.</item>
/// <item>Devices not reported for <see cref="Expiry"/> are forgotten.</item>
/// </list>
/// Not thread-safe: owned by the engine thread.
/// </summary>
internal sealed class BatteryMemory
{
    public const int HysteresisPolls = 3;
    public static readonly TimeSpan HysteresisTime = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(30);

    /// <summary>Timestamps only count as a memory change (worth a state.json write) when they moved this far.</summary>
    private static readonly TimeSpan TimestampSaveGranularity = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required BatteryDevice Device;
        public DateTimeOffset? LastConnectedAt;
        public DateTimeOffset LastReportedAt;
        /// <summary>What the last merge showed for this device (connected, sleeping or disconnected).</summary>
        public bool ShownConnected;
        /// <summary>Consecutive polls without a connected report while <see cref="ShownConnected"/>.</summary>
        public int Misses;
        // Values as of the last export, to decide whether a write is worth it.
        public DateTimeOffset? SavedConnectedAt;
        public DateTimeOffset SavedReportedAt;
    }

    public BatteryMemory(IEnumerable<RememberedBattery>? remembered, DateTimeOffset now)
    {
        foreach (var r in remembered ?? Array.Empty<RememberedBattery>())
        {
            if (r?.Device?.Id is not { Length: > 0 } id) continue;
            if (now - r.LastReportedAt > Expiry) continue;
            _entries[id] = new Entry
            {
                Device = r.Device,
                LastConnectedAt = r.LastConnectedAt,
                LastReportedAt = r.LastReportedAt,
                SavedConnectedAt = r.LastConnectedAt,
                SavedReportedAt = r.LastReportedAt,
            };
        }
    }

    public int Count => _entries.Count;

    /// <summary>The remembered devices as they are shown before the first poll: every one disconnected.</summary>
    public IReadOnlyList<BatteryDevice> Current(DateTimeOffset now)
        => Sort(_entries.Values.Select(e => e.ShownConnected ? Sleeping(e) : Disconnected(e, null, now)));

    /// <summary>
    /// Merges one poll (all providers) into the memory and returns the list to show (sorted). <paramref name="changed"/>
    /// tells whether the persisted memory changed enough to be written.
    /// </summary>
    public IReadOnlyList<BatteryDevice> Merge(IReadOnlyList<BatteryDevice> reported, DateTimeOffset now, out bool changed)
    {
        changed = false;
        var shown = new List<BatteryDevice>(Math.Max(reported.Count, _entries.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in reported)
        {
            if (string.IsNullOrEmpty(device.Id) || !seen.Add(device.Id)) continue;

            if (!_entries.TryGetValue(device.Id, out var entry))
            {
                entry = new Entry { Device = device, LastReportedAt = now, SavedReportedAt = DateTimeOffset.MinValue };
                _entries[device.Id] = entry;
                changed = true;
            }

            if (device.IsConnected)
            {
                if (Differs(entry.Device, device)) changed = true;
                entry.Device = device;
                entry.LastConnectedAt = now;
                entry.LastReportedAt = now;
                entry.ShownConnected = true;
                entry.Misses = 0;
                shown.Add(device);
                continue;
            }

            // Disconnected report: field-wise merge, the remembered level survives a report without one.
            var merged = device with
            {
                Percent = device.Percent ?? entry.Device.Percent,
                VoltageMillivolts = device.VoltageMillivolts ?? entry.Device.VoltageMillivolts,
                LastConnectedAt = device.LastConnectedAt ?? entry.Device.LastConnectedAt,
            };
            if (Differs(entry.Device, merged)) changed = true;
            entry.Device = merged;
            entry.LastReportedAt = now;
            shown.Add(Absent(entry, device.LastConnectedAt, now));
        }

        foreach (var (id, entry) in _entries.ToList())
        {
            if (seen.Contains(id)) continue;
            if (now - entry.LastReportedAt > Expiry)
            {
                _entries.Remove(id);
                changed = true;
                continue;
            }
            shown.Add(Absent(entry, null, now));
        }

        foreach (var entry in _entries.Values)
        {
            if (Moved(entry.SavedConnectedAt, entry.LastConnectedAt) || Moved(entry.SavedReportedAt, entry.LastReportedAt)) changed = true;
        }

        return Sort(shown);
    }

    /// <summary>The memory for state.json.</summary>
    public IReadOnlyList<RememberedBattery> Export()
    {
        var list = new List<RememberedBattery>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            entry.SavedConnectedAt = entry.LastConnectedAt;
            entry.SavedReportedAt = entry.LastReportedAt;
            list.Add(new RememberedBattery { Device = entry.Device, LastConnectedAt = entry.LastConnectedAt, LastReportedAt = entry.LastReportedAt });
        }
        return list;
    }

    /// <summary>Connected first, then lowest level (unknown last), then name.</summary>
    public static IReadOnlyList<BatteryDevice> Sort(IEnumerable<BatteryDevice> devices)
        => devices
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Percent ?? int.MaxValue)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>A device without a connected report in this poll: still "sleeping" during the hysteresis, else disconnected.</summary>
    private static BatteryDevice Absent(Entry entry, DateTimeOffset? providerLastConnected, DateTimeOffset now)
    {
        if (entry.ShownConnected)
        {
            entry.Misses++;
            var since = entry.LastConnectedAt is { } at ? now - at : TimeSpan.MaxValue;
            if (entry.Misses < HysteresisPolls || since < HysteresisTime) return Sleeping(entry);
            entry.ShownConnected = false;
            entry.Misses = 0;
        }
        return Disconnected(entry, providerLastConnected, now);
    }

    private static BatteryDevice Sleeping(Entry entry) => entry.Device with
    {
        IsConnected = true,
        Status = BatteryStatus.Unknown,
        Detail = Strings.DeviceSleeping,
    };

    private static BatteryDevice Disconnected(Entry entry, DateTimeOffset? providerLastConnected, DateTimeOffset now)
    {
        // Bluetooth reports when the device was last connected even while it is off; that wins over our own stamp.
        var last = providerLastConnected ?? entry.Device.LastConnectedAt ?? entry.LastConnectedAt;
        return entry.Device with
        {
            IsConnected = false,
            Status = BatteryStatus.Unknown,
            Detail = last is { } t ? EngineStrings.LastSeen(t, now) : entry.Device.Detail,
            LastConnectedAt = last,
        };
    }

    private static bool Moved(DateTimeOffset? saved, DateTimeOffset? current)
        => saved is null ? current is not null : current is { } c && (c - saved.Value).Duration() >= TimestampSaveGranularity;

    private static bool Moved(DateTimeOffset saved, DateTimeOffset current) => (current - saved).Duration() >= TimestampSaveGranularity;

    /// <summary>Fields worth persisting (a new level, a status flip, a rename); timestamps are handled separately.</summary>
    private static bool Differs(BatteryDevice a, BatteryDevice b)
        => a.Percent != b.Percent || a.Status != b.Status || a.IsConnected != b.IsConnected || a.Name != b.Name
           || a.Kind != b.Kind || a.Connection != b.Connection || a.Detail != b.Detail || a.Source != b.Source;
}
