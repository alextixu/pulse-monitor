using System.Globalization;
using Avalonia.Data.Converters;

namespace Pulse.App.Converters;

/// <summary>bool → opacity: true → 1.0, false → 0.55 (or the ConverterParameter, e.g. "0.4").</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dim = 0.55;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) dim = parsed;
        return value is true ? 1.0 : dim;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null / empty string / empty collection → false (hidden); anything else → true. ConverterParameter "invert" flips the result. Bind to IsVisible.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public static readonly NullToVisibleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        var invert = parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !visible : visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Equality test: returns true when the value equals the ConverterParameter (enum names compared by string). Useful for IsVisible / IsChecked on enums.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return parameter is null;
        if (parameter is null) return false;
        if (value is Enum && parameter is string name) return string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
        return value.Equals(parameter) || string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null && targetType.IsEnum && parameter is string name && Enum.TryParse(targetType, name, true, out var parsed))
        {
            return parsed;
        }
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>double (0..100) → fraction 0..1, or with ConverterParameter "width:200" → pixels (value/100*200). Used for rounded progress bars.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = Math.Clamp(PercentToBrushConverter.ToDouble(value) ?? 0, 0, 100);
        if (parameter is string s && s.StartsWith("width:", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(s[6..], NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
        {
            return pct / 100 * width;
        }
        return pct / 100;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
