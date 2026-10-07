using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pulse.App.Controls;

/// <summary>How <see cref="RingGauge"/> picks its progress colour when <see cref="RingGauge.ProgressBrush"/> is not set.</summary>
public enum RingColorMode
{
    /// <summary>Always <see cref="RingGauge.ProgressBrush"/> (falls back to the accent brush).</summary>
    Fixed,
    /// <summary>Battery thresholds: ≥50 success, 20–49 warning, &lt;20 danger.</summary>
    Battery,
    /// <summary>Temperature thresholds: &lt;60 success, 60–79 warning, ≥80 danger.</summary>
    Temperature,
    /// <summary>Load thresholds: &lt;60 accent, 60–84 warning, ≥85 danger.</summary>
    Load,
}

/// <summary>
/// Circular progress ring (0..100) drawn with <see cref="DrawingContext"/>; the optional child (XAML content)
/// is centred inside the ring, e.g. <c>&lt;controls:RingGauge Value="87"&gt;&lt;TextBlock Text="87%"/&gt;&lt;/controls:RingGauge&gt;</c>.
/// No animation. Colour comes from <see cref="ProgressBrush"/> or, when that is null, from <see cref="ColorMode"/> thresholds.
/// </summary>
public class RingGauge : Decorator
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value), 0d, coerce: CoerceValue);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Thickness), 7d);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(ProgressBrush));

    public static readonly StyledProperty<RingColorMode> ColorModeProperty =
        AvaloniaProperty.Register<RingGauge, RingColorMode>(nameof(ColorMode), RingColorMode.Battery);

    /// <summary>Charging devices are drawn with the accent colour regardless of level (Battery mode only).</summary>
    public static readonly StyledProperty<bool> IsChargingProperty =
        AvaloniaProperty.Register<RingGauge, bool>(nameof(IsCharging));

    /// <summary>Angle in degrees where the arc starts; -90 = 12 o'clock.</summary>
    public static readonly StyledProperty<double> StartAngleProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(StartAngle), -90d);

    public static readonly StyledProperty<bool> RoundCapsProperty =
        AvaloniaProperty.Register<RingGauge, bool>(nameof(RoundCaps), true);

    /// <summary>True draws only the track (level unknown); the child content is still shown.</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<RingGauge, bool>(nameof(IsIndeterminate));

    static RingGauge()
    {
        AffectsRender<RingGauge>(ValueProperty, MaximumProperty, ThicknessProperty, TrackBrushProperty, ProgressBrushProperty,
            ColorModeProperty, IsChargingProperty, StartAngleProperty, RoundCapsProperty, IsIndeterminateProperty);
        AffectsMeasure<RingGauge>(ThicknessProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ProgressBrush { get => GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public RingColorMode ColorMode { get => GetValue(ColorModeProperty); set => SetValue(ColorModeProperty, value); }
    public bool IsCharging { get => GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public double StartAngle { get => GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
    public bool RoundCaps { get => GetValue(RoundCapsProperty); set => SetValue(RoundCapsProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    /// <summary>The brush actually used for the progress arc (resolved from resources when auto-coloured).</summary>
    public IBrush EffectiveProgressBrush => ResolveProgressBrush();

    private static double CoerceValue(AvaloniaObject _, double value) => double.IsNaN(value) ? 0 : Math.Max(0, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = Child;
        var ring = 2 * Thickness + 8;
        if (child is null)
        {
            return new Size(Math.Min(availableSize.Width, 48), Math.Min(availableSize.Height, 48));
        }

        child.Measure(availableSize.Deflate(new Avalonia.Thickness(ring / 2)));
        var inner = Math.Max(child.DesiredSize.Width, child.DesiredSize.Height);
        var side = inner * 1.25 + ring;
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var desired = child.DesiredSize;
            var w = Math.Min(desired.Width, finalSize.Width);
            var h = Math.Min(desired.Height, finalSize.Height);
            child.Arrange(new Rect((finalSize.Width - w) / 2, (finalSize.Height - h) / 2, w, h));
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var side = Math.Min(bounds.Width, bounds.Height);
        if (side <= 0) return;

        var thickness = Math.Clamp(Thickness, 1, side / 2);
        var radius = side / 2 - thickness / 2;
        if (radius <= 0) return;
        var center = new Point(bounds.Width / 2, bounds.Height / 2);

        var track = TrackBrush ?? FindBrush("BccTrackBrush") ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        if (IsIndeterminate) return;

        var max = Maximum <= 0 ? 100 : Maximum;
        var fraction = Math.Clamp(Value / max, 0, 1);
        if (fraction <= 0) return;

        var pen = new Pen(ResolveProgressBrush(), thickness)
        {
            LineCap = RoundCaps ? PenLineCap.Round : PenLineCap.Flat,
        };

        if (fraction >= 0.9995)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var startRad = StartAngle * Math.PI / 180;
        var sweep = fraction * 2 * Math.PI;
        var endRad = startRad + sweep;
        var start = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var end = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private IBrush ResolveProgressBrush()
    {
        if (ProgressBrush is { } explicitBrush) return explicitBrush;

        var tone = ColorMode switch
        {
            RingColorMode.Battery => BccTones.ForBattery(Value, IsCharging),
            RingColorMode.Temperature => BccTones.ForTemperature(Value),
            RingColorMode.Load => BccTones.ForLoad(Value),
            _ => BccTone.Accent,
        };
        return FindBrush(BccTones.BrushKey(tone)) ?? FindBrush("BccAccentBrush") ?? Brushes.DodgerBlue;
    }

    private IBrush? FindBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
}
