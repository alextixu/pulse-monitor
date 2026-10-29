using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pulse.Core.Tray;

namespace Pulse.Agent.Tray;

/// <summary>
/// Draws the tray glyph with plain GDI (no System.Drawing / GDI+ / Skia in the resident process; spikes/tray run 3): the
/// ring, arc and battery shapes are computed per pixel with analytic anti-aliasing, the digits come from DrawTextW
/// (Segoe UI Bold, ANTIALIASED_QUALITY) rendered white-on-black into a scratch DIB and used as coverage. Geometry and colours
/// are those of the old Skia renderer (TrayIconRenderer.cs, services map "mustPreserve"): ring stroke 0.125·S inset
/// stroke/2 + 0.03·S, arc from 12 o'clock clockwise with round caps (full ring at ≥ 0.999), digits 0.5·S (0.36·S for three
/// characters) in the tone colour; battery body / nub / bolt in the tone colour (grey when neutral). Rendered at 32 px and
/// handed to the shell, which scales it. Never throws: content → static glyph → blank icon → zero.
/// </summary>
internal sealed unsafe class GdiTrayIconRenderer
{
    public const int Size = 32;

    private readonly ILogger _log;
    private int _failureLogged;

    public GdiTrayIconRenderer(ILogger log) => _log = log;

    /// <summary>A new HICON (the caller owns it: DestroyIcon when replaced), or zero when even the blank fallback failed.</summary>
    public IntPtr Render(TrayIconContent content)
    {
        try
        {
            return CreateIcon(content);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0) _log.LogWarning(ex, "Tray icon rendering failed for {Content}; using the static glyph", content);
        }

        if (content != TrayIconContent.Static)
        {
            try
            {
                return CreateIcon(TrayIconContent.Static);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Static tray glyph failed; using a blank icon");
            }
        }

        try
        {
            uint* blank = stackalloc uint[Size * Size];
            new Span<uint>(blank, Size * Size).Clear();
            return CreateIconFromPixels(blank);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Blank tray icon failed");
            return IntPtr.Zero;
        }
    }

    private IntPtr CreateIcon(TrayIconContent content)
    {
        uint* pixels = stackalloc uint[Size * Size];
        RenderPixels(content, pixels);
        return CreateIconFromPixels(pixels);
    }

    /// <summary>Fills <paramref name="dst"/> (Size×Size, top-down, straight-alpha BGRA as CreateIconIndirect expects).</summary>
    internal static void RenderPixels(TrayIconContent content, uint* dst)
    {
        if (content.Style == TrayGlyphStyle.Battery) RenderBattery(content.Tone, dst);
        else RenderRing(content, dst);
    }

    // ───────────── ring + number ─────────────

    private static void RenderRing(TrayIconContent content, uint* dst)
    {
        const double s = Size;
        var stroke = 0.125 * s;
        var inset = stroke / 2 + 0.03 * s;
        var c = s / 2;
        var rMid = s / 2 - inset;
        double rOut = rMid + stroke / 2, rIn = rMid - stroke / 2;

        var tone = TrayContent.ColorOf(content.Tone);
        var track = TrayContent.Track;
        var trackAlpha = track.A / 255.0;

        var fraction = Math.Clamp(content.Fraction ?? 0, 0, 1);
        var hasArc = fraction > 0;
        var full = fraction >= 0.999;
        var sweep = 360.0 * fraction;
        var a0 = -90 * Math.PI / 180;
        var a1 = (-90 + sweep) * Math.PI / 180;
        double cap0X = c + rMid * Math.Cos(a0), cap0Y = c + rMid * Math.Sin(a0);
        double cap1X = c + rMid * Math.Cos(a1), cap1Y = c + rMid * Math.Sin(a1);

        byte* textMask = stackalloc byte[Size * Size];
        new Span<byte>(textMask, Size * Size).Clear();
        if (!string.IsNullOrEmpty(content.Text)) RenderTextCoverage(content.Text, textMask);

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            double dx = px - c, dy = py - c;
            var d = Math.Sqrt(dx * dx + dy * dy);
            var ring = Clamp01(Math.Min(d - rIn, rOut - d) + 0.5); // 1 px analytic AA on both edges

            var arc = 0.0;
            if (hasArc)
            {
                if (full)
                {
                    arc = ring;
                }
                else
                {
                    var theta = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360; // 0 = 12 o'clock, clockwise
                    arc = theta <= sweep ? ring : 0;
                    var capA = Clamp01(stroke / 2 - Distance(px, py, cap0X, cap0Y) + 0.5);
                    var capB = Clamp01(stroke / 2 - Distance(px, py, cap1X, cap1Y) + 0.5);
                    arc = Math.Max(arc, Math.Max(capA, capB));
                }
            }

            var textCoverage = textMask[y * Size + x] / 255.0;

            // Premultiplied "over": track → arc → digits.
            double pa = 0, pr = 0, pg = 0, pb = 0;
            Over(ref pa, ref pr, ref pg, ref pb, track.R, track.G, track.B, trackAlpha * ring);
            Over(ref pa, ref pr, ref pg, ref pb, tone.R, tone.G, tone.B, arc);
            Over(ref pa, ref pr, ref pg, ref pb, tone.R, tone.G, tone.B, textCoverage);
            dst[y * Size + x] = Straight(pa, pr, pg, pb);
        }
    }

    /// <summary>GDI writes no alpha, so the digits are drawn white on black and the brightness is used as coverage.</summary>
    private static void RenderTextCoverage(string text, byte* coverage)
    {
        var bmi = NewBitmapInfo();
        var hdc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
        IntPtr bitmap = IntPtr.Zero, font = IntPtr.Zero, oldBitmap = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            void* bits;
            bitmap = Native.CreateDIBSection(hdc, &bmi, Native.DIB_RGB_COLORS, &bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException($"CreateDIBSection failed ({Marshal.GetLastPInvokeError()})");
            oldBitmap = Native.SelectObject(hdc, bitmap);
            new Span<uint>(bits, Size * Size).Clear();

            var fontPx = (int)Math.Round(Size * (text.Length >= 3 ? 0.36 : 0.5));
            font = Native.CreateFontW(-fontPx, 0, 0, 0, Native.FW_BOLD, 0, 0, 0, Native.DEFAULT_CHARSET,
                Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS, Native.ANTIALIASED_QUALITY, Native.DEFAULT_PITCH, "Segoe UI");
            if (font == IntPtr.Zero) throw new InvalidOperationException("CreateFontW failed");
            oldFont = Native.SelectObject(hdc, font);
            Native.SetTextColor(hdc, 0x00FFFFFF);
            Native.SetBkMode(hdc, Native.TRANSPARENT);
            var rect = new Native.RECT { left = 0, top = 0, right = Size, bottom = Size };
            fixed (char* chars = text)
            {
                Native.DrawTextW(hdc, chars, text.Length, &rect, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOCLIP);
            }
            Native.GdiFlush();

            var src = (uint*)bits;
            for (var i = 0; i < Size * Size; i++)
            {
                var p = src[i];
                coverage[i] = (byte)Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
            }
        }
        finally
        {
            if (oldFont != IntPtr.Zero) Native.SelectObject(hdc, oldFont);
            if (oldBitmap != IntPtr.Zero) Native.SelectObject(hdc, oldBitmap);
            if (font != IntPtr.Zero) Native.DeleteObject(font);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            Native.DeleteDC(hdc);
        }
    }

    // ───────────── battery glyph ─────────────

    private static void RenderBattery(TrayTone toneKind, uint* dst)
    {
        const double s = Size;
        var tone = toneKind == TrayTone.Neutral ? TrayContent.Grey : TrayContent.ColorOf(toneKind);
        var stroke = 0.08 * s;

        double bodyL = 0.09 * s, bodyT = 0.28 * s, bodyR = 0.80 * s, bodyB = 0.72 * s, bodyRadius = 0.1 * s;
        double nubL = 0.82 * s, nubT = 0.40 * s, nubR = 0.93 * s, nubB = 0.60 * s, nubRadius = 0.03 * s;

        double cx = (bodyL + bodyR) / 2, cy = (bodyT + bodyB) / 2;
        var h = 0.62 * (bodyB - bodyT);
        var w = 0.55 * h;
        double* bolt = stackalloc double[12]
        {
            cx + 0.15 * w, cy - h / 2,
            cx - w / 2, cy + 0.08 * h,
            cx - 0.05 * w, cy + 0.08 * h,
            cx - 0.15 * w, cy + h / 2,
            cx + w / 2, cy - 0.08 * h,
            cx + 0.05 * w, cy - 0.08 * h,
        };

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            var body = Clamp01(stroke / 2 - Math.Abs(RoundRectDistance(px, py, bodyL, bodyT, bodyR, bodyB, bodyRadius)) + 0.5);
            var nub = Clamp01(0.5 - RoundRectDistance(px, py, nubL, nubT, nubR, nubB, nubRadius));
            var boltCoverage = Clamp01(0.5 - PolygonDistance(px, py, bolt, 6));
            var coverage = Math.Max(body, Math.Max(nub, boltCoverage));

            double pa = 0, pr = 0, pg = 0, pb = 0;
            Over(ref pa, ref pr, ref pg, ref pb, tone.R, tone.G, tone.B, coverage);
            dst[y * Size + x] = Straight(pa, pr, pg, pb);
        }
    }

    /// <summary>Signed distance to a rounded rectangle (negative inside).</summary>
    private static double RoundRectDistance(double px, double py, double left, double top, double right, double bottom, double radius)
    {
        double cx = (left + right) / 2, cy = (top + bottom) / 2;
        double hx = (right - left) / 2, hy = (bottom - top) / 2;
        var qx = Math.Abs(px - cx) - hx + radius;
        var qy = Math.Abs(py - cy) - hy + radius;
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    /// <summary>Signed distance to a closed polygon given as x,y pairs (negative inside; even-odd rule).</summary>
    private static double PolygonDistance(double px, double py, double* v, int count)
    {
        var dx0 = px - v[0];
        var dy0 = py - v[1];
        var best = dx0 * dx0 + dy0 * dy0;
        var sign = 1.0;
        for (int i = 0, j = count - 1; i < count; j = i, i++)
        {
            double vix = v[2 * i], viy = v[2 * i + 1], vjx = v[2 * j], vjy = v[2 * j + 1];
            double ex = vjx - vix, ey = vjy - viy;
            double wx = px - vix, wy = py - viy;
            var t = Math.Clamp((wx * ex + wy * ey) / (ex * ex + ey * ey), 0, 1);
            double bx = wx - ex * t, by = wy - ey * t;
            best = Math.Min(best, bx * bx + by * by);

            var c1 = py >= viy;
            var c2 = py < vjy;
            var c3 = ex * wy > ey * wx;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) sign = -sign;
        }
        return sign * Math.Sqrt(best);
    }

    // ───────────── helpers ─────────────

    private static IntPtr CreateIconFromPixels(uint* pixels)
    {
        var bmi = NewBitmapInfo();
        void* bits;
        var color = Native.CreateDIBSection(IntPtr.Zero, &bmi, Native.DIB_RGB_COLORS, &bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) throw new InvalidOperationException($"CreateDIBSection failed ({Marshal.GetLastPInvokeError()})");
        IntPtr mask = IntPtr.Zero;
        try
        {
            Buffer.MemoryCopy(pixels, bits, Size * Size * 4, Size * Size * 4);

            // 1 bpp AND mask, all zero (the colour bitmap's alpha decides). CreateBitmap with null bits is uninitialised.
            const int maskStride = (Size + 15) / 16 * 2;
            byte* maskBits = stackalloc byte[maskStride * Size];
            new Span<byte>(maskBits, maskStride * Size).Clear();
            mask = Native.CreateBitmap(Size, Size, 1, 1, maskBits);
            if (mask == IntPtr.Zero) throw new InvalidOperationException("CreateBitmap failed");

            var info = new Native.ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
            var icon = Native.CreateIconIndirect(&info); // copies both bitmaps
            if (icon == IntPtr.Zero) throw new InvalidOperationException($"CreateIconIndirect failed ({Marshal.GetLastPInvokeError()})");
            return icon;
        }
        finally
        {
            if (mask != IntPtr.Zero) Native.DeleteObject(mask);
            Native.DeleteObject(color);
        }
    }

    private static Native.BITMAPINFO NewBitmapInfo()
    {
        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = Size;
        bmi.bmiHeader.biHeight = -Size; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Native.BI_RGB;
        return bmi;
    }

    private static void Over(ref double pa, ref double pr, ref double pg, ref double pb, byte r, byte g, byte b, double a)
    {
        if (a <= 0) return;
        var k = 1 - a;
        pr = pr * k + r * a;
        pg = pg * k + g * a;
        pb = pb * k + b * a;
        pa = pa * k + a;
    }

    /// <summary>Premultiplied accumulator → straight BGRA (a 32 bpp icon's colour bitmap is not premultiplied).</summary>
    private static uint Straight(double pa, double pr, double pg, double pb)
    {
        if (pa <= 0) return 0;
        var a = (uint)Math.Round(Math.Min(1, pa) * 255);
        var r = (uint)Math.Round(Math.Min(255, pr / pa));
        var g = (uint)Math.Round(Math.Min(255, pg / pa));
        var b = (uint)Math.Round(Math.Min(255, pb / pa));
        return a << 24 | r << 16 | g << 8 | b;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static double Distance(double x0, double y0, double x1, double y1)
    {
        double dx = x0 - x1, dy = y0 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
