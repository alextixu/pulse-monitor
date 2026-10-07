using System.Globalization;
using Avalonia.Data.Converters;
using Pulse.App.Localization;

namespace Pulse.App.Converters;

/// <summary>Any Core enum → 繁體中文 display name via <see cref="Strings.ForEnum"/>.</summary>
public sealed class EnumToDisplayConverter : IValueConverter
{
    public static readonly EnumToDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Enum e ? Strings.ForEnum(e) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>int?/double? → "87%" or "—" when null.</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public static readonly PercentTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Strings.Percent(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>double? → "61°C" or "—" when null.</summary>
public sealed class CelsiusTextConverter : IValueConverter
{
    public static readonly CelsiusTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Strings.Celsius(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>double? → "1 250 RPM" or "—".</summary>
public sealed class RpmTextConverter : IValueConverter
{
    public static readonly RpmTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Strings.Rpm(PercentToBrushConverter.ToDouble(value));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a value with a .NET format string given as ConverterParameter (e.g. "{0:0.0} GHz"); null → "—".</summary>
public sealed class FormatConverter : IValueConverter
{
    public static readonly FormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Strings.NotAvailable;
        return parameter is string format ? string.Format(culture, format, value) : value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>DateTimeOffset? → "更新於 HH:mm:ss"; null → empty string.</summary>
public sealed class UpdatedAtTextConverter : IValueConverter
{
    public static readonly UpdatedAtTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset t => string.Format(Strings.StatusUpdatedFormat, t.ToString("HH:mm:ss", culture)),
        DateTime t => string.Format(Strings.StatusUpdatedFormat, t.ToString("HH:mm:ss", culture)),
        _ => string.Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
