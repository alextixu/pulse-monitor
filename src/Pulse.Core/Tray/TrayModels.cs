using System.Text;
using Pulse.Core.Localization;
using Pulse.Core.Models;

namespace Pulse.Core.Tray;

/// <summary>The device with the lowest battery among the connected, visible ones.</summary>
public sealed record BatterySummary(string DeviceId, string Name, int Percent, bool IsCharging, DeviceKind Kind);

/// <summary>Everything the tray icon / tooltip needs, recomputed after every poll.</summary>
public sealed record TraySummary
{
    public IReadOnlyList<BatteryDevice> ConnectedDevices { get; init; } = Array.Empty<BatteryDevice>();
    public BatterySummary? Lowest { get; init; }
    public double? CpuLoadPercent { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuLoadPercent { get; init; }
    public double? GpuTempC { get; init; }

    public static TraySummary Empty { get; } = new();

    /// <summary>Value equality at tooltip precision (whole percents / degrees, device id + level + status).</summary>
    public bool Equals(TraySummary? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Lowest != other.Lowest) return false;
        if (Round(CpuLoadPercent) != Round(other.CpuLoadPercent) || Round(CpuTempC) != Round(other.CpuTempC)) return false;
        if (Round(GpuLoadPercent) != Round(other.GpuLoadPercent) || Round(GpuTempC) != Round(other.GpuTempC)) return false;
        if (ConnectedDevices.Count != other.ConnectedDevices.Count) return false;
        for (var i = 0; i < ConnectedDevices.Count; i++)
        {
            var a = ConnectedDevices[i];
            var b = other.ConnectedDevices[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Percent != b.Percent || a.Status != b.Status) return false;
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Lowest, Round(CpuLoadPercent), Round(CpuTempC), Round(GpuLoadPercent), Round(GpuTempC), ConnectedDevices.Count);

    private static int? Round(double? value) => value is { } v ? (int)Math.Round(v) : null;

    /// <summary>
    /// Derives the summary: connected visible devices, the lowest one with a level (charging = status Charging), and the
    /// first GPU's load / core temperature.
    /// </summary>
    public static TraySummary From(IReadOnlyList<BatteryDevice> visibleDevices, HardwareSnapshot hardware, out BatterySummary? lowest)
    {
        var connected = visibleDevices.Where(d => d.IsConnected).ToList();
        var lowestDevice = connected.Where(d => d.Percent is not null).MinBy(d => d.Percent);
        lowest = lowestDevice is null
            ? null
            : new BatterySummary(lowestDevice.Id, lowestDevice.Name, lowestDevice.Percent!.Value, lowestDevice.Status == BatteryStatus.Charging, lowestDevice.Kind);
        var gpu = hardware.Gpus.FirstOrDefault();
        return new TraySummary
        {
            ConnectedDevices = connected,
            Lowest = lowest,
            CpuLoadPercent = hardware.Cpu?.LoadPercent,
            CpuTempC = hardware.Cpu?.PackageTempC,
            GpuLoadPercent = gpu?.LoadPercent,
            GpuTempC = gpu?.CoreTempC,
        };
    }
}

/// <summary>Colour family of the tray glyph.</summary>
public enum TrayTone
{
    Neutral,
    Good,
    Warning,
    Danger,
    Charging,
}

public enum TrayGlyphStyle
{
    /// <summary>Progress ring with a centred number.</summary>
    Ring,
    /// <summary>Static battery glyph with a bolt (IconOnly mode / no data).</summary>
    Battery,
}

/// <summary>What the tray icon shows; value-compared so unchanged content is not re-rendered.</summary>
public sealed record TrayIconContent(TrayGlyphStyle Style, string? Text, double? Fraction, TrayTone Tone)
{
    public static TrayIconContent Static { get; } = new(TrayGlyphStyle.Battery, null, null, TrayTone.Neutral);
}

/// <summary>Pure rules deciding the tray glyph and tooltip (shared by every tray implementation).</summary>
public static class TrayContent
{
    /// <summary>Win32 NOTIFYICONDATA tooltip limit (characters).</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Glyph colours (sRGB): Good, Warning, Danger, Charging, Neutral and the ring track (with alpha).</summary>
    public static readonly (byte R, byte G, byte B) Green = (0x34, 0xC7, 0x59);
    public static readonly (byte R, byte G, byte B) Orange = (0xFF, 0x9F, 0x0A);
    public static readonly (byte R, byte G, byte B) Red = (0xFF, 0x3B, 0x30);
    public static readonly (byte R, byte G, byte B) Blue = (0x0A, 0x84, 0xFF);
    public static readonly (byte R, byte G, byte B) Grey = (0x8E, 0x8E, 0x93);
    public static readonly (byte R, byte G, byte B, byte A) Track = (0x80, 0x80, 0x80, 0x59);

    public static (byte R, byte G, byte B) ColorOf(TrayTone tone) => tone switch
    {
        TrayTone.Good => Green,
        TrayTone.Warning => Orange,
        TrayTone.Danger => Red,
        TrayTone.Charging => Blue,
        _ => Grey,
    };

    /// <summary>
    /// Decides what to draw for the given summary and mode (pure function, any thread). The ring fraction is rounded to
    /// whole percent — a 32 px ring cannot show more — so equal-looking icons compare equal.
    /// </summary>
    public static TrayIconContent Build(TraySummary summary, TrayIconMode mode)
    {
        var content = BuildCore(summary, mode);
        return content.Fraction is { } f ? content with { Fraction = Math.Round(Math.Clamp(f, 0, 1), 2) } : content;
    }

    private static TrayIconContent BuildCore(TraySummary summary, TrayIconMode mode)
    {
        switch (mode)
        {
            case TrayIconMode.IconOnly:
                return TrayIconContent.Static;

            case TrayIconMode.CpuTemperature:
                return Temperature(summary.CpuTempC) ?? Load(summary.CpuLoadPercent) ?? TrayIconContent.Static;

            case TrayIconMode.GpuTemperature:
                return Temperature(summary.GpuTempC) ?? Load(summary.GpuLoadPercent) ?? TrayIconContent.Static;

            case TrayIconMode.CpuLoad:
                return Load(summary.CpuLoadPercent) ?? TrayIconContent.Static;

            case TrayIconMode.LowestBattery:
            default:
                if (summary.Lowest is { } lowest)
                {
                    var tone = lowest.IsCharging ? TrayTone.Charging : BatteryTone(lowest.Percent);
                    return new TrayIconContent(TrayGlyphStyle.Ring, NumberText(lowest.Percent), lowest.Percent / 100.0, tone);
                }
                return Temperature(summary.CpuTempC) ?? TrayIconContent.Static;
        }
    }

    public static TrayTone BatteryTone(double percent) => percent >= 50 ? TrayTone.Good : percent >= 20 ? TrayTone.Warning : TrayTone.Danger;

    public static TrayTone TemperatureTone(double celsius) => celsius < 60 ? TrayTone.Good : celsius <= 80 ? TrayTone.Warning : TrayTone.Danger;

    private static TrayIconContent? Temperature(double? celsius) =>
        celsius is { } t ? new TrayIconContent(TrayGlyphStyle.Ring, NumberText(t), Math.Clamp(t, 0, 100) / 100.0, TemperatureTone(t)) : null;

    private static TrayIconContent? Load(double? percent) =>
        percent is { } p ? new TrayIconContent(TrayGlyphStyle.Ring, NumberText(p), Math.Clamp(p, 0, 100) / 100.0, TemperatureTone(p)) : null;

    private static string NumberText(double value) => Math.Round(Math.Clamp(value, 0, 999)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Tooltip lines: "name 87%" per connected device, "CPU 42% · 61°C", "GPU 12% · 41°C"; truncated to the Win32 limit.</summary>
    public static string BuildTooltip(TraySummary summary)
    {
        var lines = new List<string>();
        foreach (var device in summary.ConnectedDevices)
        {
            if (device.Percent is not { } pct) continue;
            lines.Add(string.Format(Strings.TooltipDeviceFormat, device.Name, pct));
        }
        if (summary.CpuLoadPercent is not null || summary.CpuTempC is not null)
        {
            lines.Add(string.Format(Strings.TooltipCpuFormat, Strings.Percent(summary.CpuLoadPercent), Strings.Celsius(summary.CpuTempC)));
        }
        if (summary.GpuLoadPercent is not null || summary.GpuTempC is not null)
        {
            lines.Add(string.Format(Strings.TooltipGpuFormat, Strings.Percent(summary.GpuLoadPercent), Strings.Celsius(summary.GpuTempC)));
        }
        if (lines.Count == 0) return Strings.AppTitle;

        // Keep whole lines while they fit, then mark the cut with an ellipsis.
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var next = sb.Length == 0 ? line : "\n" + line;
            if (sb.Length + next.Length > MaxTooltipLength - 1)
            {
                if (sb.Length == 0) sb.Append(line.AsSpan(0, MaxTooltipLength - 1));
                sb.Append('…');
                break;
            }
            sb.Append(next);
        }
        return sb.ToString();
    }
}
