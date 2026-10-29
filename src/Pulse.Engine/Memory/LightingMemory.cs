using System.Text.RegularExpressions;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Engine.Memory;

/// <summary>
/// What the user last did to each RGB device (by <see cref="RgbDevice.Key"/>), re-applied once after the agent starts.
/// A zone paint turns the intent into per-zone colours (the zones not painted keep what the previous intent implied:
/// its colour, or black after Off). Devices whose key carries a duplicate suffix ("#2", "#3" …) are not remembered:
/// their identity depends on enumeration order, so the intent could land on the twin. Not thread-safe (the RGB
/// service serialises access).
/// </summary>
internal sealed partial class LightingMemory
{
    private static readonly string Black = new RgbColor(0, 0, 0).ToHex();

    private readonly Dictionary<string, LightingIntent> _intents;

    public LightingMemory(IReadOnlyDictionary<string, LightingIntent>? stored)
    {
        _intents = new Dictionary<string, LightingIntent>(StringComparer.Ordinal);
        foreach (var (key, intent) in stored ?? new Dictionary<string, LightingIntent>())
        {
            if (IsRememberable(key) && intent is not null) _intents[key] = intent;
        }
    }

    public int Count => _intents.Count;

    public LightingIntent? Get(string key) => _intents.TryGetValue(key, out var intent) ? intent : null;

    public IReadOnlyDictionary<string, LightingIntent> Export() => new Dictionary<string, LightingIntent>(_intents, StringComparer.Ordinal);

    /// <summary>A duplicate key ends in "#n" (n ≥ 2); every HID location contains '#' too, so only the suffix counts.</summary>
    public static bool IsRememberable(string? key) => !string.IsNullOrEmpty(key) && !DuplicateSuffix().IsMatch(key);

    [GeneratedRegex(@"#\d+$")]
    private static partial Regex DuplicateSuffix();

    /// <summary>
    /// Records the intent of a command that succeeded. <paramref name="targets"/> are the devices it addressed (one, or
    /// every device for a command without a key). Returns true when the memory changed.
    /// </summary>
    public bool Record(RgbCommand command, IReadOnlyList<RgbDevice> targets, DateTimeOffset now)
    {
        var changed = false;
        foreach (var device in targets)
        {
            if (!IsRememberable(device.Key)) continue;
            var previous = Get(device.Key);
            var next = Next(command, device, previous, now);
            if (next is null)
            {
                if (command.Kind == RgbCommandKind.Forget) changed |= _intents.Remove(device.Key);
                continue;
            }
            if (ReferenceEquals(next, previous)) continue;
            _intents[device.Key] = next;
            changed = true;
        }
        return changed;
    }

    /// <summary>"不要記住這個裝置" for a key that is no longer listed (or every key).</summary>
    public bool Forget(string? key)
    {
        if (key is null)
        {
            var any = _intents.Count > 0;
            _intents.Clear();
            return any;
        }
        return _intents.Remove(key);
    }

    private static LightingIntent? Next(RgbCommand command, RgbDevice device, LightingIntent? previous, DateTimeOffset now)
    {
        LightingIntent Make(LightingIntentKind kind) => new() { Kind = kind, DeviceName = device.Name, UpdatedAt = now };

        switch (command.Kind)
        {
            case RgbCommandKind.SetColor when command.Color is { } color:
                return Make(LightingIntentKind.Color) with { Color = color.ToHex() };

            case RgbCommandKind.SetZoneColor when command.Color is { } zoneColor && command.ZoneIndex is { } zone:
            {
                var zones = new Dictionary<int, string>();
                // Expand what the device showed before so a restore paints every zone, not only the last one touched.
                var fill = previous?.Kind switch
                {
                    LightingIntentKind.Color => previous.Color,
                    LightingIntentKind.Off => Black,
                    _ => null,
                };
                if (previous is { Kind: LightingIntentKind.Zones, Zones: { } old })
                {
                    foreach (var (i, c) in old) zones[i] = c;
                }
                else if (fill is not null)
                {
                    foreach (var z in device.Zones) zones[z.Index] = fill;
                }
                zones[zone] = zoneColor.ToHex();
                return Make(LightingIntentKind.Zones) with { Zones = zones };
            }

            case RgbCommandKind.SetMode:
            {
                var mode = ResolveMode(device, command.ModeName, command.ModeIndex);
                if (mode is null) return previous;
                return Make(LightingIntentKind.Mode) with
                {
                    ModeName = mode.Name,
                    Color = mode.SupportsColor ? command.Color?.ToHex() : null,
                    Speed = mode.SupportsSpeed ? command.Speed : null,
                    Brightness = mode.SupportsBrightness ? command.Brightness : null,
                };
            }

            case RgbCommandKind.TurnOff:
                return Make(LightingIntentKind.Off);

            case RgbCommandKind.RestoreDefault:
                return Make(LightingIntentKind.Default);

            case RgbCommandKind.Forget:
                return null;

            default:
                // SaveAsDefault and incomplete commands change nothing about what the device shows.
                return previous;
        }
    }

    /// <summary>The mode a command or intent names: by name (case-insensitive), the index only as a fallback.</summary>
    public static RgbMode? ResolveMode(RgbDevice device, string? name, int? index)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = device.Modes.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }
        return index is { } i ? device.Modes.FirstOrDefault(m => m.Index == i) : null;
    }
}
