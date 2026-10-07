using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pulse.App.Localization;
using Pulse.App.ViewModels;
using Pulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Owns the <see cref="TrayIcon"/>: dynamic icon (via <see cref="TrayIconRenderer"/>), multi-line tooltip and the context menu.
/// Icon/tooltip updates are debounced (400 ms) and happen on the UI thread. Left click raises <see cref="Clicked"/>.
/// </summary>
public sealed class TrayService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    /// <summary>Win32 NOTIFYICONDATA tooltip limit (characters).</summary>
    public const int MaxTooltipLength = 127;

    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly TrayIconRenderer _renderer;
    private readonly MainViewModel _viewModel;
    private readonly ILogger<TrayService> _log;
    private readonly DispatcherTimer _timer;
    private TrayIcon? _icon;
    private TrayIconContent? _lastContent;
    private string? _lastTooltip;
    private bool _disposed;

    public TrayService(MonitoringService monitoring, SettingsService settings, TrayIconRenderer renderer, MainViewModel viewModel, ILogger<TrayService> log)
    {
        _monitoring = monitoring;
        _settings = settings;
        _renderer = renderer;
        _viewModel = viewModel;
        _log = log;
        _timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, OnTimerTick);
    }

    /// <summary>Left click on the tray icon (UI thread).</summary>
    public event EventHandler? Clicked;

    public bool IsInitialized => _icon is not null;

    /// <summary>Creates the tray icon and menu. UI thread only; idempotent.</summary>
    public void Initialize()
    {
        if (_icon is not null || _disposed) return;

        var icon = new TrayIcon
        {
            ToolTipText = Strings.AppTitle,
            IsVisible = true,
            Icon = _renderer.CreateIcon(TrayIconContent.Static),
            Menu = BuildMenu(),
        };
        icon.Clicked += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        _icon = icon;

        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, new TrayIcons { icon });
        }

        _monitoring.SummaryChanged += OnSummaryChanged;
        _settings.Changed += OnSettingsChanged;
        UpdateNow();
        _log.LogInformation("Tray icon created");
    }

    /// <summary>Forces an immediate icon/tooltip refresh.</summary>
    public void UpdateNow()
    {
        if (_icon is null || _disposed) return;
        try
        {
            var summary = _monitoring.Summary;
            var content = TrayIconRenderer.Build(summary, _settings.Settings.TrayIconMode);
            if (content != _lastContent)
            {
                _icon.Icon = _renderer.CreateIcon(content);
                _lastContent = content;
            }

            var tooltip = BuildTooltip(summary);
            if (tooltip != _lastTooltip)
            {
                _icon.ToolTipText = tooltip;
                _lastTooltip = tooltip;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tray icon update failed");
        }
    }

    /// <summary>Tooltip lines: "name 87%" per connected device, "CPU 42% · 61°C", "GPU 12% · 41°C"; truncated to the Win32 limit.</summary>
    public static string BuildTooltip(TraySummary summary)
    {
        var sb = new StringBuilder();
        foreach (var device in summary.ConnectedDevices)
        {
            if (device.Percent is not { } pct) continue;
            sb.AppendLine(string.Format(Strings.TooltipDeviceFormat, device.Name, pct));
        }
        if (summary.CpuLoadPercent is not null || summary.CpuTempC is not null)
        {
            sb.AppendLine(string.Format(Strings.TooltipCpuFormat, Strings.Percent(summary.CpuLoadPercent), Strings.Celsius(summary.CpuTempC)));
        }
        if (summary.GpuLoadPercent is not null || summary.GpuTempC is not null)
        {
            sb.AppendLine(string.Format(Strings.TooltipGpuFormat, Strings.Percent(summary.GpuLoadPercent), Strings.Celsius(summary.GpuTempC)));
        }

        var text = sb.Length == 0 ? Strings.AppTitle : sb.ToString().TrimEnd();
        if (text.Length > MaxTooltipLength)
        {
            var cut = text.LastIndexOf('\n', MaxTooltipLength - 2);
            text = (cut > 0 ? text[..cut] : text[..(MaxTooltipLength - 1)]) + "…";
        }
        return text;
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var open = new NativeMenuItem(Strings.MenuOpen);
        open.Click += (_, _) => _viewModel.ShowPopoverCommand.Execute(null);
        menu.Add(open);

        var refresh = new NativeMenuItem(Strings.MenuRefresh);
        refresh.Click += (_, _) => _viewModel.RefreshCommand.Execute(null);
        menu.Add(refresh);

        if (_viewModel.ShowElevateAction)
        {
            var elevate = new NativeMenuItem(Strings.MenuElevate);
            elevate.Click += (_, _) => _viewModel.ElevateCommand.Execute(null);
            menu.Add(elevate);
        }

        menu.Add(new NativeMenuItemSeparator());

        var settings = new NativeMenuItem(Strings.MenuSettings);
        settings.Click += (_, _) => _viewModel.OpenSettingsCommand.Execute(null);
        menu.Add(settings);

        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem(Strings.MenuQuit);
        quit.Click += (_, _) => _viewModel.QuitCommand.Execute(null);
        menu.Add(quit);

        return menu;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        UpdateNow();
    }

    private void OnSummaryChanged(object? sender, TraySummary e) => ScheduleUpdate();

    private void OnSettingsChanged(object? sender, string? propertyName)
    {
        if (propertyName is null || propertyName == nameof(AppSettings.TrayIconMode) || propertyName == nameof(AppSettings.HiddenDeviceIds))
        {
            _monitoring.RecomputeSummary();
            ScheduleUpdate();
        }
    }

    private void ScheduleUpdate()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _monitoring.SummaryChanged -= OnSummaryChanged;
        _settings.Changed -= OnSettingsChanged;
        if (_icon is { } icon)
        {
            icon.IsVisible = false;
            icon.Dispose();
            _icon = null;
        }
    }
}
