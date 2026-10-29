namespace Pulse.Core.Models;

/// <summary>Contents of state.json (see <see cref="Abstractions.IStateStore"/>). Immutable; the agent replaces it as a whole.</summary>
public sealed record PulseState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Last known reading of every battery device, by <see cref="BatteryDevice.Id"/>.</summary>
    public IReadOnlyList<RememberedBattery> Batteries { get; init; } = Array.Empty<RememberedBattery>();

    /// <summary>Lighting the user applied, by <see cref="RgbDevice.Key"/>.</summary>
    public IReadOnlyDictionary<string, LightingIntent> Lighting { get; init; } = new Dictionary<string, LightingIntent>();

    /// <summary>
    /// Fan first sights of the boot identified by <see cref="FanBootTime"/> (same identity and 2-minute tolerance as
    /// fan-recovery.json). Discarded after a reboot, when the firmware curves are the ground truth again.
    /// </summary>
    public DateTimeOffset? FanBootTime { get; init; }
    public IReadOnlyList<FanMemory> Fans { get; init; } = Array.Empty<FanMemory>();

    /// <summary>Devices whose low-battery alert was already shown (cleared when they charge or rise 5 % above the threshold).</summary>
    public IReadOnlyList<string> LowBatteryNotified { get; init; } = Array.Empty<string>();
}

/// <summary>A battery device as last reported, plus when it was last connected and last reported at all.</summary>
public sealed record RememberedBattery
{
    public required BatteryDevice Device { get; init; }
    /// <summary>Drives the 「上次 HH:mm」 detail; only updated by a connected report.</summary>
    public DateTimeOffset? LastConnectedAt { get; init; }
    /// <summary>Drives the 30-day expiry.</summary>
    public DateTimeOffset LastReportedAt { get; init; }
}

public enum LightingIntentKind
{
    Off,
    Color,
    Mode,
    Default,
    Zones,
}

/// <summary>What the user last did to one RGB device, re-applied once after the agent starts.</summary>
public sealed record LightingIntent
{
    public required LightingIntentKind Kind { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    /// <summary>Kind Color: the colour; Kind Mode: the mode colour (when the mode takes one).</summary>
    public string? Color { get; init; }
    public string? ModeName { get; init; }
    public int? Speed { get; init; }
    public int? Brightness { get; init; }
    /// <summary>Kind Zones: zone index → "#RRGGBB".</summary>
    public IReadOnlyDictionary<int, string>? Zones { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Persisted <see cref="FanControl.FanPresenceTracker"/> entry.</summary>
public sealed record FanMemory(string FanId, double? FirstPercent, double? FirstRpm, bool EverSpun);
