using System.Text.RegularExpressions;
using Pulse.Core.Models;

namespace Pulse.Rgb;

/// <summary>What the identity rules need to know about one OpenRGB device (taken from the SDK's controller data).</summary>
internal readonly record struct RgbDeviceDescriptor(string? Name, string? Location, string? Serial, int Type, int LedCount);

/// <summary>How a stored default was matched to a device whose current key it was not saved under.</summary>
internal enum RgbDefaultMatch
{
    /// <summary>The stored key normalises to the device's key (saved by a build that kept the HID instance segment).</summary>
    NormalizedKey,

    /// <summary>The only unclaimed stored entry with the device's name, type and LED count.</summary>
    NameTypeLedCount,
}

/// <summary>One stored default to re-key: <see cref="Entry"/> is the snapshot already rewritten under <see cref="ToKey"/>.</summary>
internal sealed record RgbDefaultMove(string FromKey, string ToKey, RgbDeviceDefault Entry, RgbDefaultMatch Match);

/// <summary>
/// Result of <see cref="RgbDeviceIdentity.PlanMigration"/>: entries to move to a new key and stale entries that describe a
/// device which already has an entry under its current key (left over from the old, instance-specific keys).
/// </summary>
internal sealed record RgbDefaultsMigration(IReadOnlyList<RgbDefaultMove> Moves, IReadOnlyList<string> Superseded)
{
    public static readonly RgbDefaultsMigration None = new(Array.Empty<RgbDefaultMove>(), Array.Empty<string>());

    public bool IsEmpty => Moves.Count == 0 && Superseded.Count == 0;
}

/// <summary>
/// Pure device-identity rules of the OpenRGB backend: the stable key of a device, the migration of defaults saved under an
/// older key, and the guard against capturing a switched-off device as its "first seen" default.
/// </summary>
internal static partial class RgbDeviceIdentity
{
    private static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>
    /// The PnP instance segment of a Windows HID device path, e.g. <c>#8&amp;1b8da785&amp;0&amp;0000</c> in
    /// <c>HID: \\?\HID#VID_0B05&amp;PID_193E&amp;MI_01#8&amp;1b8da785&amp;0&amp;0000#{4d1e55b2-…}</c>. Windows derives it from the
    /// parent's position, so the same keyboard gets a new one on another USB port or receiver; the hardware-id segment
    /// before it (VID / PID / MI / Col, or the Bluetooth address) is what identifies the device. Only a segment shaped like a
    /// generated instance id ("depth&amp;hash&amp;n&amp;uniq") directly after a HID hardware id is removed.
    /// </summary>
    [GeneratedRegex(@"(?<=\bHID#[^#|]+)#[0-9A-F]+&[0-9A-F]+&[0-9A-F]+&[0-9A-F]+(?=#|\||$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HidInstanceSegment();

    /// <summary>The device type embedded in a fallback key ("Name|type5|68 LEDs", optionally with a "#n" suffix).</summary>
    [GeneratedRegex(@"\|type(?<type>-?\d+)\|\d+ LEDs(?:#\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackKeyType();

    /// <summary>Trimmed location with the Windows HID instance segment removed; any other location is only trimmed.</summary>
    public static string NormalizeLocation(string? location)
    {
        var trimmed = location?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? trimmed : HidInstanceSegment().Replace(trimmed, string.Empty);
    }

    /// <summary>
    /// A key saved by an older build in today's form: the HID instance segment is removed, everything else (name, serial,
    /// "#n" suffix) is kept, so a key that never had one comes back unchanged.
    /// </summary>
    public static string NormalizeKey(string key) => string.IsNullOrEmpty(key) ? string.Empty : HidInstanceSegment().Replace(key, string.Empty);

    /// <summary>
    /// "Name|Location|Serial" (location normalised) when OpenRGB reports a location or serial, else
    /// "Name|type{n}|{leds} LEDs".
    /// </summary>
    public static string BaseKey(in RgbDeviceDescriptor device)
    {
        var name = device.Name?.Trim() ?? string.Empty;
        var location = NormalizeLocation(device.Location);
        var serial = device.Serial?.Trim() ?? string.Empty;
        return location.Length > 0 || serial.Length > 0
            ? $"{name}|{location}|{serial}"
            : $"{name}|type{device.Type}|{device.LedCount} LEDs";
    }

    /// <summary>
    /// Keys of one enumeration (index = OpenRGB device index); identical base keys get "#2", "#3", … in enumeration order.
    /// Such suffixed keys depend on the enumeration order and are therefore not a stable identity.
    /// </summary>
    public static string[] ComputeKeys(IReadOnlyList<RgbDeviceDescriptor> devices)
    {
        var keys = new string[devices.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < devices.Count; i++)
        {
            var key = BaseKey(devices[i]);
            var n = seen.GetValueOrDefault(key);
            seen[key] = n + 1;
            keys[i] = n == 0 ? key : $"{key}#{n + 1}";
        }

        return keys;
    }

    /// <summary>
    /// True when the device currently looks switched off: its active mode is "Off", or a per-LED mode with every LED black.
    /// That is usually a state someone applied (Pulse's own "off", or the black an SDK client left behind) rather than the
    /// device's own default, so it is not captured as the first-seen default.
    /// </summary>
    public static bool LooksOff(RgbDevice device)
    {
        if (device.ActiveModeIndex < 0 || device.ActiveModeIndex >= device.Modes.Count) return false;
        var mode = device.Modes[device.ActiveModeIndex];
        if (RgbModeRules.IsOff(mode)) return true;
        return mode.IsPerLed && device.Colors.Count > 0 && device.Colors.All(c => c == Black);
    }

    /// <summary>Same rule as <see cref="LooksOff(RgbDevice)"/> for a stored snapshot (an unreadable colour counts as lit).</summary>
    public static bool LooksOff(RgbDeviceDefault snapshot)
    {
        if ((snapshot.ModeName ?? string.Empty).Trim().Equals("Off", StringComparison.OrdinalIgnoreCase)) return true;
        return snapshot.ModeIsPerLed && snapshot.LedColors is { Count: > 0 } leds && leds.All(IsBlackHex);
    }

    /// <summary>
    /// Matches stored defaults to the devices of one enumeration whose key has no entry yet.
    /// <list type="number">
    /// <item>Normalised key: entries whose key normalises (case-insensitively) to the device's key. One candidate is used
    /// as is; among several (the same device seen under different instance segments) the ones that look switched off are
    /// dropped, then the earliest capture wins — it is the real "first seen".</item>
    /// <item>Name + type + LED count, for devices still without an entry: an unclaimed entry with the same (trimmed) name,
    /// the same type (unknown for entries saved before the type was stored, which then matches any) and as many LED
    /// colours as the device has LEDs — used only when it is the device's single candidate and no other device wants it.</item>
    /// </list>
    /// Entries that normalise to a device key which ends up with an entry, without being the one used, are superseded.
    /// Entries of devices that are not present are never touched.
    /// </summary>
    /// <param name="keys">Keys of the enumeration (<see cref="ComputeKeys"/>), parallel to <paramref name="devices"/>.</param>
    /// <param name="devices">The enumerated devices.</param>
    /// <param name="stored">Every stored snapshot.</param>
    public static RgbDefaultsMigration PlanMigration(IReadOnlyList<string> keys, IReadOnlyList<RgbDeviceDescriptor> devices, IReadOnlyCollection<RgbDeviceDefault> stored)
    {
        if (stored.Count == 0 || keys.Count == 0) return RgbDefaultsMigration.None;

        var storedKeys = new HashSet<string>(stored.Select(e => e.Key), StringComparer.Ordinal);
        var presentExact = new HashSet<string>(keys, StringComparer.Ordinal);

        // Device slot per key, case-insensitively: Windows device paths are case-insensitive and hidapi versions differ.
        var slotByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Count; i++) slotByKey.TryAdd(keys[i], i);

        // Entries not saved under a present key, grouped by the present device their key normalises to (if any).
        var shadows = new Dictionary<int, List<RgbDeviceDefault>>();
        var pool = new List<RgbDeviceDefault>();
        foreach (var entry in stored)
        {
            if (presentExact.Contains(entry.Key)) continue;
            if (slotByKey.TryGetValue(NormalizeKey(entry.Key), out var slot))
            {
                if (!shadows.TryGetValue(slot, out var list)) shadows[slot] = list = new List<RgbDeviceDefault>();
                list.Add(entry);
            }
            else
            {
                pool.Add(entry);
            }
        }

        var moves = new List<RgbDefaultMove>();
        var superseded = new List<string>();
        var resolved = new HashSet<int>();

        // 1. Normalised key.
        foreach (var (slot, candidates) in shadows.OrderBy(p => p.Key))
        {
            if (storedKeys.Contains(keys[slot]))
            {
                // The device already has an entry under its current key; the old-form ones are leftovers.
                superseded.AddRange(candidates.Select(c => c.Key));
                continue;
            }

            var chosen = candidates.Count == 1 ? candidates[0] : ChooseFirstSeen(candidates);
            moves.Add(Move(chosen, keys[slot], devices[slot], RgbDefaultMatch.NormalizedKey));
            resolved.Add(slot);
            superseded.AddRange(candidates.Where(c => !ReferenceEquals(c, chosen)).Select(c => c.Key));
        }

        // 2. Name + type + LED count, unique in both directions.
        var wanted = new Dictionary<int, List<RgbDeviceDefault>>();
        for (var slot = 0; slot < keys.Count && slot < devices.Count; slot++)
        {
            if (resolved.Contains(slot) || storedKeys.Contains(keys[slot])) continue;
            var device = devices[slot];
            var candidates = pool.Where(e => SameDevice(e, device)).ToList();
            if (candidates.Count > 0) wanted[slot] = candidates;
        }

        foreach (var (slot, candidates) in wanted.OrderBy(p => p.Key))
        {
            if (candidates.Count != 1) continue;
            var entry = candidates[0];
            if (wanted.Any(p => p.Key != slot && p.Value.Any(e => ReferenceEquals(e, entry)))) continue;
            moves.Add(Move(entry, keys[slot], devices[slot], RgbDefaultMatch.NameTypeLedCount));
        }

        return moves.Count == 0 && superseded.Count == 0 ? RgbDefaultsMigration.None : new RgbDefaultsMigration(moves, superseded);
    }

    private static RgbDefaultMove Move(RgbDeviceDefault entry, string key, in RgbDeviceDescriptor device, RgbDefaultMatch match)
        => new(entry.Key, key, entry with { Key = key, DeviceType = entry.DeviceType ?? device.Type }, match);

    /// <summary>Among several snapshots of one device: drop the switched-off ones (unless all are), then the earliest capture.</summary>
    private static RgbDeviceDefault ChooseFirstSeen(List<RgbDeviceDefault> candidates)
    {
        var lit = candidates.Where(c => !LooksOff(c)).ToList();
        return (lit.Count > 0 ? lit : candidates)
            .OrderBy(c => c.CapturedAt)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .First();
    }

    private static bool SameDevice(RgbDeviceDefault entry, in RgbDeviceDescriptor device)
    {
        var name = (entry.DeviceName ?? string.Empty).Trim();
        if (name.Length == 0 || !name.Equals(device.Name?.Trim() ?? string.Empty, StringComparison.Ordinal)) return false;
        if ((entry.DeviceType ?? TypeFromKey(entry.Key)) is { } type && type != device.Type) return false;
        return (entry.LedColors?.Count ?? 0) == device.LedCount;
    }

    /// <summary>The type written into a fallback key, or null for a location / serial key.</summary>
    internal static int? TypeFromKey(string key)
    {
        var match = FallbackKeyType().Match(key ?? string.Empty);
        return match.Success && int.TryParse(match.Groups["type"].ValueSpan, out var type) ? type : null;
    }

    private static bool IsBlackHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            return RgbColor.FromHex(hex.Trim()) == Black;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
