using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Pulse.Agent.Tray;

/// <summary>
/// The agent's only window: a hidden top-level window (message-only windows get no tray callbacks and no broadcasts)
/// whose <c>GetMessage</c> loop runs on the main thread. Everything that touches Shell_NotifyIcon, GDI or menus runs on that
/// thread; other threads hand work over with <see cref="Post"/>. Besides the tray callback it handles the broadcasts the
/// agent cares about: TaskbarCreated (Explorer restarted → re-add the icon), WM_QUERYENDSESSION / WM_ENDSESSION (hand the
/// fans back before logoff / shutdown), WM_DEVICECHANGE / WM_DISPLAYCHANGE (throttled device-change notification) and
/// WM_CLOSE (a polite close, e.g. <c>taskkill</c> without /f or Restart Manager, becomes a clean quit).
/// </summary>
internal sealed unsafe class TrayWindow : IDisposable
{
    /// <summary>Shell_NotifyIcon callback message (NOTIFYICONDATA.uCallbackMessage).</summary>
    public const uint TrayCallbackMessage = Native.WM_APP + 1;
    private const uint DrainMessage = Native.WM_APP + 2;
    private const uint ExitLoopMessage = Native.WM_APP + 3;

    private const string ClassName = "Pulse.Agent.TrayWindow";
    private const nuint DeviceChangeTimerId = 1;
    /// <summary>Ids from here on are handed out by <see cref="StartTimer"/>.</summary>
    private const nuint FirstUserTimerId = 16;

    private static readonly TimeSpan DeviceChangeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionEndJoin = TimeSpan.FromSeconds(6);

    /// <summary>The window procedure is a static unmanaged entry point; it routes to the one window of the process.</summary>
    private static TrayWindow? s_current;

    private readonly ILogger _log;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<nuint, Action> _timers = new();
    private readonly LeadingEdgeThrottle _deviceChanges = new(DeviceChangeInterval);
    private readonly IntPtr _instance;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private int _drainPosted;
    private nuint _nextTimerId = FirstUserTimerId;
    private bool _blockReasonSet;
    private bool _disposed;

    public TrayWindow(ILogger log)
    {
        _log = log;
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one tray window per process.");
        }

        _instance = Native.GetModuleHandleW(null);
        fixed (char* cls = ClassName)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = _instance,
                lpszClassName = (IntPtr)cls,
            };
            if (Native.RegisterClassExW(&wc) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                s_current = null;
                throw new InvalidOperationException($"RegisterClassExW failed ({error}).");
            }

            fixed (char* title = "Pulse")
            {
                // A normal top-level window that is never shown: it has no taskbar button, but unlike HWND_MESSAGE windows it
                // receives tray callbacks and system broadcasts.
                _hwnd = Native.CreateWindowExW(0, cls, title, Native.WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            }
        }

        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            UnregisterClass();
            s_current = null;
            throw new InvalidOperationException($"CreateWindowExW failed ({error}).");
        }

        _taskbarCreatedMessage = Native.RegisterWindowMessageW("TaskbarCreated");
        // An elevated agent only receives these from the medium-integrity Explorer when UIPI is told to let them through.
        AllowFromLowerIntegrity(_taskbarCreatedMessage);
        AllowFromLowerIntegrity(TrayCallbackMessage);
    }

    public IntPtr Handle => _hwnd;

    /// <summary>A tray icon callback (NOTIFYICON_VERSION_4): the event (LOWORD of lParam) and the anchor point from wParam.</summary>
    public Action<uint, int, int>? TrayCallback { get; set; }

    /// <summary>Explorer (re)created the taskbar: every icon has to be added again.</summary>
    public Action? TaskbarCreated { get; set; }

    /// <summary>Devices or displays changed; leading edge, then at most once per 2 s.</summary>
    public Action? DevicesChanged { get; set; }

    /// <summary>Logoff / shutdown is starting (WM_QUERYENDSESSION) or someone asked the window to close: start the clean shutdown.</summary>
    public Action<string>? ShutdownRequested { get; set; }

    /// <summary>The session really ends (WM_ENDSESSION TRUE): wait up to the given time for the clean shutdown to finish.</summary>
    public Func<TimeSpan, bool>? WaitForShutdown { get; set; }

    /// <summary>Text Windows shows while the agent holds up shutdown in WM_ENDSESSION.</summary>
    public string ShutdownBlockReason { get; set; } = AgentStrings.ShutdownBlockReason;

    /// <summary>Runs <paramref name="action"/> on the window thread (thread-safe; exceptions are logged, never rethrown).</summary>
    public void Post(Action action)
    {
        if (_disposed) return;
        _queue.Enqueue(action);
        if (Interlocked.Exchange(ref _drainPosted, 1) == 0 && !Native.PostMessageW(_hwnd, DrainMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Volatile.Write(ref _drainPosted, 0);
            _log.LogDebug("PostMessage to the tray window failed ({Error}); action stays queued", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>One-shot timer on the window thread (call from the window thread). Returns its id for <see cref="StopTimer"/>.</summary>
    public nuint StartTimer(TimeSpan due, Action onElapsed)
    {
        var id = _nextTimerId++;
        _timers[id] = onElapsed;
        if (Native.SetTimer(_hwnd, id, (uint)Math.Clamp(due.TotalMilliseconds, 10, int.MaxValue), IntPtr.Zero) == 0)
        {
            _timers.Remove(id);
            _log.LogDebug("SetTimer failed ({Error})", Marshal.GetLastPInvokeError());
            return 0;
        }
        return id;
    }

    public void StopTimer(nuint id)
    {
        if (id == 0) return;
        Native.KillTimer(_hwnd, id);
        _timers.Remove(id);
    }

    /// <summary>Pumps messages until <see cref="RequestExit"/> (or WM_QUIT from elsewhere). Main thread only.</summary>
    public void Run()
    {
        Native.MSG msg;
        int result;
        while ((result = Native.GetMessageW(&msg, IntPtr.Zero, 0, 0)) > 0)
        {
            Native.TranslateMessage(&msg);
            Native.DispatchMessageW(&msg);
        }
        if (result < 0) _log.LogError("GetMessage failed ({Error})", Marshal.GetLastPInvokeError());
    }

    /// <summary>Ends <see cref="Run"/> from any thread (PostQuitMessage has to be called on the loop's own thread).</summary>
    public void RequestExit()
    {
        if (_hwnd != IntPtr.Zero) Native.PostMessageW(_hwnd, ExitLoopMessage, IntPtr.Zero, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Nothing may escape into user32: an exception crossing this boundary is a FailFast that also skips the fan hand-back
        // hooks (decisions-v2 §7).
        try
        {
            if (s_current is { } window && (window._hwnd == hwnd || window._hwnd == IntPtr.Zero))
            {
                if (window.HandleMessage(msg, wParam, lParam, out var result)) return result;
            }
        }
        catch (Exception ex)
        {
            try { s_current?._log.LogError(ex, "Tray window message 0x{Message:X4} failed", msg); } catch { /* logging must not throw here */ }
        }

        try
        {
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case TrayCallbackMessage:
                TrayCallback?.Invoke((uint)Native.LoWord(lParam), Native.Point(wParam).X, Native.Point(wParam).Y);
                return true;

            case DrainMessage:
                Drain();
                return true;

            case ExitLoopMessage:
                Native.PostQuitMessage(0);
                return true;

            case Native.WM_TIMER:
                OnTimer((nuint)wParam);
                return true;

            case Native.WM_DEVICECHANGE:
                if (wParam.ToInt64() == Native.DBT_DEVNODES_CHANGED) OnDeviceChangeSignal("WM_DEVICECHANGE");
                return false; // DefWindowProc answers TRUE (do not veto anything)

            case Native.WM_DISPLAYCHANGE:
                OnDeviceChangeSignal("WM_DISPLAYCHANGE");
                return false;

            case Native.WM_QUERYENDSESSION:
                OnQueryEndSession(lParam);
                result = (IntPtr)1; // never veto the logoff; WM_ENDSESSION waits for the fans instead
                return true;

            case Native.WM_ENDSESSION:
                OnEndSession(wParam != IntPtr.Zero);
                return true;

            case Native.WM_CLOSE:
                // Not DestroyWindow: the window has to outlive the shutdown (it pumps while the fans are handed back).
                _log.LogInformation("WM_CLOSE received; shutting down");
                ShutdownRequested?.Invoke("WM_CLOSE");
                return true;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _log.LogInformation("Taskbar (re)created; adding the tray icon again");
            TaskbarCreated?.Invoke();
            return true;
        }

        return false;
    }

    private void Drain()
    {
        // Reset first: a Post racing with this drain either lands in this pass or posts a new drain message.
        Volatile.Write(ref _drainPosted, 0);
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Tray window action failed");
            }
        }
    }

    private void OnTimer(nuint id)
    {
        Native.KillTimer(_hwnd, id);
        if (id == DeviceChangeTimerId)
        {
            if (_deviceChanges.TimerElapsed(Environment.TickCount64)) RaiseDevicesChanged("trailing");
            return;
        }

        if (_timers.Remove(id, out var action)) action();
    }

    private void OnDeviceChangeSignal(string source)
    {
        var (fireNow, armMs) = _deviceChanges.Signal(Environment.TickCount64);
        if (fireNow)
        {
            RaiseDevicesChanged(source);
        }
        else if (armMs is { } due && Native.SetTimer(_hwnd, DeviceChangeTimerId, (uint)Math.Clamp(due, 10, int.MaxValue), IntPtr.Zero) == 0)
        {
            // No timer: fire the trailing notification right away rather than losing it.
            if (_deviceChanges.TimerElapsed(Environment.TickCount64)) RaiseDevicesChanged(source + " (no timer)");
        }
    }

    private void RaiseDevicesChanged(string source)
    {
        _log.LogDebug("Devices changed ({Source})", source);
        DevicesChanged?.Invoke();
    }

    private void OnQueryEndSession(IntPtr lParam)
    {
        _log.LogInformation("WM_QUERYENDSESSION (flags 0x{Flags:X}); handing the fans back", lParam.ToInt64());
        if (!_blockReasonSet)
        {
            _blockReasonSet = Native.ShutdownBlockReasonCreate(_hwnd, ShutdownBlockReason);
            if (!_blockReasonSet) _log.LogDebug("ShutdownBlockReasonCreate failed ({Error})", Marshal.GetLastPInvokeError());
        }
        ShutdownRequested?.Invoke("WM_QUERYENDSESSION");
    }

    private void OnEndSession(bool ending)
    {
        if (ending)
        {
            var finished = WaitForShutdown?.Invoke(SessionEndJoin) ?? true;
            if (finished) _log.LogInformation("WM_ENDSESSION: shutdown complete");
            else _log.LogWarning("WM_ENDSESSION: shutdown still running after {Seconds} s", SessionEndJoin.TotalSeconds);
        }
        else
        {
            // The logoff was cancelled after the shutdown had started; it cannot be undone (the fans are already handed back).
            _log.LogInformation("WM_ENDSESSION: session end cancelled");
        }

        if (_blockReasonSet)
        {
            Native.ShutdownBlockReasonDestroy(_hwnd);
            _blockReasonSet = false;
        }
    }

    private void AllowFromLowerIntegrity(uint message)
    {
        if (message == 0) return;
        if (!Native.ChangeWindowMessageFilterEx(_hwnd, message, Native.MSGFLT_ALLOW, IntPtr.Zero))
        {
            _log.LogDebug("ChangeWindowMessageFilterEx(0x{Message:X}) failed ({Error})", message, Marshal.GetLastPInvokeError());
        }
    }

    private void UnregisterClass()
    {
        fixed (char* cls = ClassName) Native.UnregisterClassW(cls, _instance);
    }

    /// <summary>Destroys the window (window thread, after <see cref="Run"/> returned).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _timers.Keys) Native.KillTimer(_hwnd, id);
        _timers.Clear();
        Native.KillTimer(_hwnd, DeviceChangeTimerId);
        if (_blockReasonSet) Native.ShutdownBlockReasonDestroy(_hwnd);
        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        UnregisterClass();
        Interlocked.CompareExchange(ref s_current, null, this);
    }
}
