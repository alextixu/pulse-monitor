using Microsoft.Extensions.Logging;
using Pulse.Core.Abstractions;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Core.Tray;
using Pulse.Engine;

namespace Pulse.Agent.Tray;

/// <summary>
/// Keeps the tray icon in step with the engine and turns tray input into agent / popover actions.
/// Engine events (any thread) only arm a 400 ms coalescing timer; the update itself runs on the window thread, rebuilds the
/// content with the pure rules in Pulse.Core (<see cref="TrayContent"/>) and touches the shell only when the glyph or the
/// tooltip actually changed. Low-battery alerts raised while no popover is visible become a balloon.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(400);

    private const nuint CmdOpen = 1, CmdRefresh = 2, CmdElevate = 3, CmdSettings = 4, CmdQuit = 5;
    public const string SettingsTab = "settings";

    private readonly TrayWindow _window;
    private readonly NotifyIcon _icon;
    private readonly GdiTrayIconRenderer _renderer;
    private readonly AgentCore _agent;
    private readonly UiLauncher _launcher;
    private readonly Action _quitFallback;
    private readonly ILogger _log;
    private readonly Timer _timer;
    private TrayIconContent? _lastContent;
    private string? _lastTooltip;
    private int _updateArmed;
    private bool _attached;
    private bool _menuOpen;
    private volatile bool _shuttingDown;
    private volatile bool _disposed;

    /// <param name="quitFallback">Used when the engine cannot take the quit request (it must always be possible to quit).</param>
    public TrayController(TrayWindow window, NotifyIcon icon, GdiTrayIconRenderer renderer, AgentCore agent, UiLauncher launcher,
        Action quitFallback, ILogger log)
    {
        _window = window;
        _icon = icon;
        _renderer = renderer;
        _agent = agent;
        _launcher = launcher;
        _quitFallback = quitFallback;
        _log = log;
        _timer = new Timer(_ => OnCoalesceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        _window.TrayCallback = OnTrayCallback;
        _window.TaskbarCreated = OnTaskbarCreated;
    }

    /// <summary>Subscribes to the engine; call before <see cref="AgentCore.StartAsync"/> so no early change is missed.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _agent.SummaryChanged += OnSummaryChanged;
        _agent.SettingsChanged += OnSettingsChanged;
        _agent.NotificationRequested += OnNotificationRequested;
    }

    /// <summary>First real update once the engine has started (window thread).</summary>
    public void Start() => UpdateNow();

    /// <summary>The agent is going down: ignore clicks, the menu and balloons from now on.</summary>
    public void BeginShutdown() => _shuttingDown = true;

    // ───────────── engine → tray ─────────────

    private void OnSummaryChanged(object? sender, TraySummary e) => ScheduleUpdate();

    private void OnSettingsChanged(object? sender, SettingsState e)
    {
        // HiddenDeviceIds changes reach us as SummaryChanged (the engine re-derives the summary); the mode is a pure tray input.
        if (e.Changed is null || e.Changed == nameof(AppSettings.TrayIconMode)) ScheduleUpdate();
    }

    private void OnNotificationRequested(object? sender, LowBatteryToast toast)
    {
        if (_shuttingDown || _disposed) return;
        _window.Post(() =>
        {
            if (_shuttingDown || _disposed) return;
            if (_icon.ShowBalloon(toast.Title, toast.Message)) _log.LogInformation("Low-battery notification shown for {Device}", toast.Device.Name);
        });
    }

    private void ScheduleUpdate()
    {
        if (_disposed) return;
        // Coalesce rather than debounce: a change arms the timer once, later changes inside the window ride along, so a
        // steady 1 s stream of summaries still updates the icon within 400 ms.
        if (Interlocked.Exchange(ref _updateArmed, 1) == 0)
        {
            try { _timer.Change(Coalesce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    private void OnCoalesceElapsed()
    {
        Volatile.Write(ref _updateArmed, 0);
        if (!_disposed) _window.Post(UpdateNow);
    }

    /// <summary>Rebuilds icon content and tooltip from the engine's current summary and mode (window thread).</summary>
    private void UpdateNow()
    {
        if (_disposed) return;

        TraySummary summary;
        TrayIconMode mode;
        try
        {
            summary = _agent.Summary;
            mode = _agent.Settings.Settings.TrayIconMode;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tray summary unavailable");
            return;
        }

        var content = TrayContent.Build(summary, mode);
        var tooltip = TrayContent.BuildTooltip(summary);

        var newIcon = IntPtr.Zero;
        if (content != _lastContent)
        {
            newIcon = _renderer.Render(content);
        }
        var newTooltip = tooltip != _lastTooltip ? tooltip : null;
        if (newIcon == IntPtr.Zero && newTooltip is null) return;

        _icon.Update(newIcon, newTooltip);
        // A failed render (zero) keeps the old glyph and is retried on the next change.
        if (newIcon != IntPtr.Zero) _lastContent = content;
        if (newTooltip is not null) _lastTooltip = newTooltip;
    }

    private void OnTaskbarCreated()
    {
        _icon.Readd();
    }

    // ───────────── tray input ─────────────

    private void OnTrayCallback(uint trayEvent, int x, int y)
    {
        if (_shuttingDown || _disposed) return;
        switch (trayEvent)
        {
            // VERSION_4 sends both WM_LBUTTONUP and NIN_SELECT for one left click: act on NIN_SELECT only.
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                _launcher.OnTrayClick();
                break;
            case Native.WM_CONTEXTMENU: // right-button release, Shift+F10 or the Apps key
                ShowMenu(x, y);
                break;
            case Native.NIN_BALLOONUSERCLICK: // the low-battery notification was clicked
                _launcher.Show(toggle: false, tab: null);
                break;
        }
    }

    /// <summary>
    /// 開啟 / 重新整理 / [以系統管理員身分重新啟動] / — / 設定 / — / 結束 — the same items as the old tray menu. Posted actions
    /// (icon updates) keep running inside TrackPopupMenuEx's modal loop.
    /// </summary>
    private void ShowMenu(int x, int y)
    {
        if (_menuOpen) return;

        var showElevate = false;
        try
        {
            var info = _agent.Info;
            showElevate = !info.IsElevated && info.CanElevate;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Agent info unavailable for the tray menu");
        }

        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            _log.LogWarning("CreatePopupMenu failed");
            return;
        }

        int command;
        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, CmdOpen, Strings.MenuOpen);
            Native.AppendMenuW(menu, Native.MF_STRING, CmdRefresh, Strings.MenuRefresh);
            if (showElevate) Native.AppendMenuW(menu, Native.MF_STRING, CmdElevate, Strings.MenuElevate);
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CmdSettings, Strings.MenuSettings);
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CmdQuit, Strings.MenuQuit);

            // A menu owned by a hidden window only dismisses on an outside click when that window is foreground first, and
            // closes promptly only after a task switch afterwards (WM_NULL) — the classic KB135788 dance.
            Native.SetForegroundWindow(_window.Handle);
            _menuOpen = true;
            command = Native.TrackPopupMenuEx(menu,
                Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_BOTTOMALIGN | Native.TPM_RIGHTALIGN,
                x, y, _window.Handle, IntPtr.Zero);
        }
        finally
        {
            _menuOpen = false;
            Native.PostMessageW(_window.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(menu);
        }

        if (command != 0 && !_shuttingDown) OnCommand((nuint)command);
    }

    private void OnCommand(nuint command)
    {
        switch (command)
        {
            case CmdOpen:
                _launcher.Show(toggle: false, tab: null);
                break;
            case CmdRefresh:
                Observe(() => _agent.RefreshNowAsync(), "refresh");
                break;
            case CmdElevate:
                Observe(() => _agent.ElevateAsync(showUi: false), "elevate");
                break;
            case CmdSettings:
                _launcher.Show(toggle: false, tab: SettingsTab);
                break;
            case CmdQuit:
                _log.LogInformation("Quit chosen from the tray menu");
                try
                {
                    _agent.RequestQuit();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "The engine did not take the quit request; shutting down directly");
                    _quitFallback();
                }
                break;
        }
    }

    /// <summary>Fire-and-forget for a tray command; failures end up in the log, never as an unobserved task.</summary>
    private void Observe(Func<Task<CommandResult>> start, string what)
    {
        Task<CommandResult> task;
        try
        {
            task = start();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tray {Command} failed", what);
            return;
        }

        task.ContinueWith(t =>
        {
            if (t.IsFaulted) _log.LogWarning(t.Exception?.GetBaseException(), "Tray {Command} failed", what);
            else if (t.IsCompletedSuccessfully && !t.Result.Ok) _log.LogInformation("Tray {Command}: {Error}", what, t.Result.Error);
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached)
        {
            _agent.SummaryChanged -= OnSummaryChanged;
            _agent.SettingsChanged -= OnSettingsChanged;
            _agent.NotificationRequested -= OnNotificationRequested;
        }
        _window.TrayCallback = null;
        _window.TaskbarCreated = null;
        _timer.Dispose();
    }
}
