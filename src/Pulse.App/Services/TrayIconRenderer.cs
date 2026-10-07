using Avalonia.Controls;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Pulse.App.Services;

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

/// <summary>
/// Renders the dynamic tray icon with SkiaSharp (no window needed, any thread) and wraps it as a <see cref="WindowIcon"/>.
/// Falls back to the static battery glyph, and finally to a 1-pixel icon, if rendering fails.
/// </summary>
public sealed class TrayIconRenderer
{
    public const int DefaultSize = 32;

    private static readonly SKColor Green = new(0x34, 0xC7, 0x59);
    private static readonly SKColor Orange = new(0xFF, 0x9F, 0x0A);
    private static readonly SKColor Red = new(0xFF, 0x3B, 0x30);
    private static readonly SKColor Blue = new(0x0A, 0x84, 0xFF);
    private static readonly SKColor Grey = new(0x8E, 0x8E, 0x93);
    private static readonly SKColor Track = new(0x80, 0x80, 0x80, 0x59);

    private readonly ILogger<TrayIconRenderer> _log;
    private readonly Lazy<SKTypeface> _typeface;

    public TrayIconRenderer(ILogger<TrayIconRenderer> log)
    {
        _log = log;
        _typeface = new Lazy<SKTypeface>(LoadTypeface);
    }

    /// <summary>Decides what to draw for the given summary and mode (pure function, any thread).</summary>
    public static TrayIconContent Build(TraySummary summary, TrayIconMode mode)
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

    /// <summary>Renders the content to a WindowIcon (PNG in memory). Never throws.</summary>
    public WindowIcon CreateIcon(TrayIconContent content, int size = DefaultSize)
    {
        try
        {
            return new WindowIcon(new MemoryStream(RenderPng(content, size)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tray icon rendering failed; using static glyph");
            try
            {
                return new WindowIcon(new MemoryStream(RenderPng(TrayIconContent.Static, size)));
            }
            catch (Exception ex2)
            {
                _log.LogError(ex2, "Static tray glyph rendering failed; using blank icon");
                return new WindowIcon(new MemoryStream(BlankPng()));
            }
        }
    }

    /// <summary>Renders a square PNG of the given pixel size.</summary>
    public byte[] RenderPng(TrayIconContent content, int size = DefaultSize)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (content.Style == TrayGlyphStyle.Ring && content.Text is not null)
            {
                DrawRing(canvas, size, content);
            }
            else
            {
                DrawBattery(canvas, size, content.Tone);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void DrawRing(SKCanvas canvas, int size, TrayIconContent content)
    {
        var color = ToColor(content.Tone);
        var stroke = size * 0.125f;
        var inset = stroke / 2f + size * 0.03f;
        var rect = new SKRect(inset, inset, size - inset, size - inset);

        using var track = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = Track, IsAntialias = true };
        canvas.DrawOval(rect, track);

        var fraction = Math.Clamp(content.Fraction ?? 1, 0, 1);
        if (fraction > 0)
        {
            using var progress = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            if (fraction >= 0.999)
            {
                canvas.DrawOval(rect, progress);
            }
            else
            {
                canvas.DrawArc(rect, -90, (float)(360 * fraction), false, progress);
            }
        }

        var text = content.Text!;
        var textSize = text.Length >= 3 ? size * 0.36f : size * 0.5f;
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = textSize,
            TextAlign = SKTextAlign.Center,
            Typeface = _typeface.Value,
            SubpixelText = true,
        };
        var metrics = paint.FontMetrics;
        var baseline = size / 2f - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(text, size / 2f, baseline, paint);
    }

    private static void DrawBattery(SKCanvas canvas, int size, TrayTone tone)
    {
        var color = tone == TrayTone.Neutral ? Grey : ToColor(tone);
        var stroke = size * 0.08f;
        var body = new SKRect(size * 0.09f, size * 0.28f, size * 0.80f, size * 0.72f);
        var radius = size * 0.1f;

        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color, IsAntialias = true };
        canvas.DrawRoundRect(body, radius, radius, outline);

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = true };
        var nub = new SKRect(size * 0.82f, size * 0.40f, size * 0.93f, size * 0.60f);
        canvas.DrawRoundRect(nub, size * 0.03f, size * 0.03f, fill);

        // Bolt inside the body.
        using var bolt = new SKPath();
        var cx = body.MidX;
        var cy = body.MidY;
        var h = body.Height * 0.62f;
        var w = h * 0.55f;
        bolt.MoveTo(cx + w * 0.15f, cy - h / 2);
        bolt.LineTo(cx - w / 2, cy + h * 0.08f);
        bolt.LineTo(cx - w * 0.05f, cy + h * 0.08f);
        bolt.LineTo(cx - w * 0.15f, cy + h / 2);
        bolt.LineTo(cx + w / 2, cy - h * 0.08f);
        bolt.LineTo(cx + w * 0.05f, cy - h * 0.08f);
        bolt.Close();
        canvas.DrawPath(bolt, fill);
    }

    private static TrayIconContent? Temperature(double? celsius) =>
        celsius is { } t ? new TrayIconContent(TrayGlyphStyle.Ring, NumberText(t), Math.Clamp(t, 0, 100) / 100.0, TemperatureTone(t)) : null;

    private static TrayIconContent? Load(double? percent) =>
        percent is { } p ? new TrayIconContent(TrayGlyphStyle.Ring, NumberText(p), Math.Clamp(p, 0, 100) / 100.0, TemperatureTone(p)) : null;

    private static string NumberText(double value) => Math.Round(Math.Clamp(value, 0, 999)).ToString("0");

    private static SKColor ToColor(TrayTone tone) => tone switch
    {
        TrayTone.Good => Green,
        TrayTone.Warning => Orange,
        TrayTone.Danger => Red,
        TrayTone.Charging => Blue,
        _ => Grey,
    };

    private SKTypeface LoadTypeface()
    {
        foreach (var family in new[] { "Segoe UI", "Inter", "Helvetica Neue", "Arial" })
        {
            try
            {
                var tf = SKTypeface.FromFamilyName(family, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
                if (tf is not null) return tf;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Typeface {Family} unavailable", family);
            }
        }
        return SKTypeface.Default;
    }

    private static byte[] BlankPng()
    {
        using var bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
