using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pulse.App.ViewModels;
using Pulse.App.Views;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// "--screenshot &lt;dir&gt;" implementation: shows the popover, switches theme + tab, renders the window root with
/// <see cref="RenderTargetBitmap"/> to &lt;dir&gt;\tab-&lt;name&gt;.png (light) and tab-&lt;name&gt;-dark.png.
/// The fans tab is additionally captured per fan mode: tab-fans-quiet / -synced / -auto[-dark][-full].png
/// (the demo settings store keeps those mode switches in memory).
/// </summary>
public static class ScreenshotService
{
    public static async Task<IReadOnlyList<string>> CaptureAsync(PopoverWindow window, MainViewModel viewModel, string directory, ILogger log)
    {
        Directory.CreateDirectory(directory);
        var files = new List<string>();

        window.HideOnDeactivate = false;
        window.SetScreenshotBackdrop(true);
        window.ShowPopover();
        await SettleAsync(250);

        // Let the fan presence tracker see enough samples to hide the demo's empty headers.
        var fansTab = viewModel.FansTab;
        for (var waited = 0; waited < 8000 && !fansTab.HasHiddenFans; waited += 250) await Task.Delay(250);

        // The regular per-tab shots show the fans tab in 個別 mode.
        fansTab.Mode = FanMode.Individual;

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            App.ApplyTheme(theme);
            await SettleAsync(150);

            foreach (var tab in Enum.GetValues<MainTab>())
            {
                viewModel.SelectedTab = tab;
                await SettleAsync(200);

                var suffix = theme == AppTheme.Dark ? "-dark" : string.Empty;
                var path = Path.Combine(directory, $"tab-{tab.ToString().ToLowerInvariant()}{suffix}.png");
                try
                {
                    // Render the window itself so adorner-layer content (inline toasts) is captured too.
                    Render(window, window.RenderScaling, path);
                    files.Add(path);
                    log.LogInformation("Screenshot written: {Path}", path);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Screenshot failed for {Tab} ({Theme})", tab, theme);
                }

                // Full-height variant: the tab content is laid out at its full extent inside the scroll viewer.
                var fullPath = Path.Combine(directory, $"tab-{tab.ToString().ToLowerInvariant()}{suffix}-full.png");
                try
                {
                    Render(window.TabContentRoot, window.RenderScaling, fullPath);
                    files.Add(fullPath);
                    log.LogInformation("Screenshot written: {Path}", fullPath);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Full-height screenshot failed for {Tab} ({Theme})", tab, theme);
                }

                // Low-battery toasts are captured once (first tab); dismiss them so they do not cover the other tabs.
                window.CloseToasts();
            }
        }

        // Fan-mode variants (the demo GPU is still in its hot phase, so Quiet shows the handoff).
        foreach (var mode in new[] { FanMode.Quiet, FanMode.Synced, FanMode.Auto })
        {
            fansTab.Mode = mode;
            // One evaluation plus a fresh hardware poll, so the duty pills reflect the applied mode.
            await SettleAsync(2300);
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                App.ApplyTheme(theme);
                viewModel.SelectedTab = MainTab.Fans;
                await SettleAsync(250);
                var name = $"tab-fans-{mode.ToString().ToLowerInvariant()}{(theme == AppTheme.Dark ? "-dark" : string.Empty)}";
                TryRender(window, window.RenderScaling, Path.Combine(directory, name + ".png"), files, log);
                TryRender(window.TabContentRoot, window.RenderScaling, Path.Combine(directory, name + "-full.png"), files, log);
                window.CloseToasts();
            }
        }

        fansTab.Mode = FanMode.Individual;

        return files;
    }

    private static void TryRender(Control root, double scaling, string path, List<string> files, ILogger log)
    {
        try
        {
            Render(root, scaling, path);
            files.Add(path);
            log.LogInformation("Screenshot written: {Path}", path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Screenshot failed: {Path}", path);
        }
    }

    private static async Task SettleAsync(int milliseconds)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static void Render(Control root, double scaling, string path)
    {
        var size = root.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) throw new InvalidOperationException("Window has no layout yet.");

        var pixelSize = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(root);
        bitmap.Save(path);
    }
}
