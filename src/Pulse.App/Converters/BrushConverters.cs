using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Pulse.App.Controls;
using Pulse.Core.Models;

namespace Pulse.App.Converters;

/// <summary>Battery percent (int?/double?) → brush: ≥50 success, 20–49 warning, &lt;20 danger, null → neutral. Parameter "tint" → tint brush.</summary>
public sealed class PercentToBrushConverter : IValueConverter
{
    public static readonly PercentToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ToneToBrushConverter.Resolve(BccTones.ForBattery(ToDouble(value)), parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => null,
    };
}

/// <summary>Battery percent → <see cref="BccTone"/> (same thresholds as <see cref="PercentToBrushConverter"/>).</summary>
public sealed class PercentToToneConverter : IValueConverter
{
    public static readonly PercentToToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BccTones.ForBattery(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Temperature °C (double?) → brush: &lt;60 success, 60–79 warning, ≥80 danger, null → neutral. Parameter "tint" → tint brush.</summary>
public sealed class TemperatureToBrushConverter : IValueConverter
{
    public static readonly TemperatureToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ToneToBrushConverter.Resolve(BccTones.ForTemperature(PercentToBrushConverter.ToDouble(value)), parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Temperature °C → <see cref="BccTone"/>.</summary>
public sealed class TemperatureToToneConverter : IValueConverter
{
    public static readonly TemperatureToToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BccTones.ForTemperature(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Load percent → <see cref="BccTone"/> (&lt;60 accent, 60–84 warning, ≥85 danger).</summary>
public sealed class LoadToToneConverter : IValueConverter
{
    public static readonly LoadToToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BccTones.ForLoad(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="BccTone"/> → solid brush (BccXxxBrush); ConverterParameter "tint" → BccXxxTintBrush.</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public static readonly ToneToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Resolve(value is BccTone tone ? tone : BccTone.Neutral, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static IBrush Resolve(BccTone tone, object? parameter)
    {
        var tint = parameter is string s && s.Equals("tint", StringComparison.OrdinalIgnoreCase);
        var key = tint ? BccTones.TintBrushKey(tone) : BccTones.BrushKey(tone);
        return ResourceLookup.Brush(key, Color.FromRgb(0x8E, 0x8E, 0x93));
    }
}

/// <summary><see cref="HardwareMonitorState"/> / <see cref="RgbConnectionState"/> / <see cref="BatteryStatus"/> → <see cref="BccTone"/>.</summary>
public sealed class StateToToneConverter : IValueConverter
{
    public static readonly StateToToneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HardwareMonitorState h => BccTones.ForHardwareState(h),
        RgbConnectionState r => BccTones.ForRgbState(r),
        BatteryStatus b => BccTones.ForBatteryStatus(b, null),
        bool ok => ok ? BccTone.Success : BccTone.Neutral,
        _ => BccTone.Neutral,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Core <see cref="RgbColor"/> (or Avalonia <see cref="Color"/> / "#RRGGBB" string) → <see cref="SolidColorBrush"/>.</summary>
public sealed class RgbColorToBrushConverter : IValueConverter
{
    public static readonly RgbColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RgbColor c => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
        Color color => new SolidColorBrush(color),
        string hex when Color.TryParse(hex, out var parsed) => new SolidColorBrush(parsed),
        _ => Brushes.Transparent,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ISolidColorBrush b) return new RgbColor(b.Color.R, b.Color.G, b.Color.B);
        throw new NotSupportedException();
    }
}

/// <summary>Core <see cref="RgbColor"/> ↔ Avalonia <see cref="Color"/> (for ColorView / ColorPicker bindings).</summary>
public sealed class RgbColorToColorConverter : IValueConverter
{
    public static readonly RgbColorToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RgbColor c ? Color.FromRgb(c.R, c.G, c.B) : Colors.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color color ? new RgbColor(color.R, color.G, color.B) : default(RgbColor);
}
