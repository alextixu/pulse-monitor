using Avalonia;
using Avalonia.Platform;

namespace Pulse.App.Services;

/// <summary>
/// Computes where the popover goes: bottom-right of the primary screen's working area (Windows / Linux, next to the tray)
/// or top-right under the menu bar (macOS). All inputs in DIPs, output in physical pixels.
/// </summary>
public static class PopoverPlacement
{
    /// <summary>Gap between the window and the working-area edges, in DIPs.</summary>
    public const double EdgeMargin = 12;

    public sealed record Result(PixelPoint Position, double MaxHeightDips);

    public static Result Compute(Screen screen, Size windowSizeDips) =>
        Compute(screen.WorkingArea, screen.Scaling, windowSizeDips, OperatingSystem.IsMacOS());

    public static Result Compute(PixelRect workingArea, double scaling, Size windowSizeDips, bool anchorTop)
    {
        if (scaling <= 0 || double.IsNaN(scaling)) scaling = 1;

        var marginPx = EdgeMargin * scaling;
        var widthPx = windowSizeDips.Width * scaling;
        var heightPx = windowSizeDips.Height * scaling;

        var maxHeightDips = Math.Max(200, workingArea.Height / scaling - 2 * EdgeMargin);

        var x = workingArea.Right - marginPx - widthPx;
        var y = anchorTop
            ? workingArea.Y + marginPx
            : workingArea.Bottom - marginPx - heightPx;

        x = Math.Max(workingArea.X, x);
        y = Math.Max(workingArea.Y, y);

        return new Result(new PixelPoint((int)Math.Round(x), (int)Math.Round(y)), maxHeightDips);
    }
}
