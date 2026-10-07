namespace Pulse.Core.Models;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) throw new FormatException($"Expected RRGGBB, got '{hex}'.");
        return new RgbColor(
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();
}

public sealed record RgbMode
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public bool SupportsColor { get; init; }
    public bool SupportsSpeed { get; init; }
    public bool SupportsBrightness { get; init; }
    /// <summary>True for "Direct"/"Custom" style modes where every LED can be set individually.</summary>
    public bool IsPerLed { get; init; }
    public int? MinSpeed { get; init; }
    public int? MaxSpeed { get; init; }
    public int? MinBrightness { get; init; }
    public int? MaxBrightness { get; init; }
}

public sealed record RgbZone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public int LedCount { get; init; }
}

public sealed record RgbDevice
{
    /// <summary>Controller index as assigned by the RGB backend (OpenRGB device index).</summary>
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>Backend type string, e.g. "Motherboard", "Mouse", "Keyboard", "DRAM", "GPU", "Cooler", "LED Strip".</summary>
    public required string Type { get; init; }
    public string? Vendor { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<RgbZone> Zones { get; init; } = Array.Empty<RgbZone>();
    public IReadOnlyList<RgbMode> Modes { get; init; } = Array.Empty<RgbMode>();
    public int ActiveModeIndex { get; init; }
    public int LedCount { get; init; }
    /// <summary>Current colour of each LED (may be empty if the backend does not report it).</summary>
    public IReadOnlyList<RgbColor> Colors { get; init; } = Array.Empty<RgbColor>();
}

public enum RgbConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

public sealed record RgbStatus
{
    public RgbConnectionState State { get; init; } = RgbConnectionState.Disconnected;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 6742;
    /// <summary>Human readable hint (e.g. "OpenRGB SDK server not reachable – enable it in OpenRGB › Settings › SDK Server").</summary>
    public string? Message { get; init; }
    public string? ServerVersion { get; init; }
}
