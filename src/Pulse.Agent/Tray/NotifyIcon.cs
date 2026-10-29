using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pulse.Core.Localization;

namespace Pulse.Agent.Tray;

/// <summary>
/// The notification-area icon (Shell_NotifyIconW, NOTIFYICON_VERSION_4, uID 1 on the <see cref="TrayWindow"/>). Owns the
/// HICON currently handed to the shell: the shell copies it on NIM_ADD / NIM_MODIFY, so the previous one is destroyed right
/// after each swap (a leaked icon per update exhausted the 10 000 GDI/USER object quota of the old app after ~29 h).
/// Window thread only.
/// </summary>
internal sealed unsafe class NotifyIcon : IDisposable
{
    /// <summary>Keep the id (and the exe path) stable: Windows remembers the user's "show in the taskbar" choice by them.</summary>
    private const uint IconId = 1;
    private const int TipCapacity = 128;
    private const int InfoTitleCapacity = 64;
    private const int InfoCapacity = 256;
    private const int MaxAddAttempts = 30;
    private static readonly TimeSpan AddRetryDelay = TimeSpan.FromSeconds(2);

    private readonly TrayWindow _window;
    private readonly ILogger _log;
    private IntPtr _icon;
    private string _tip = Strings.AppTitle;
    private bool _added;
    private int _addAttempts;
    private nuint _retryTimer;
    private bool _disposed;

    /// <param name="initialIcon">Owned from now on (destroyed on the next swap or on dispose); may be zero.</param>
    public NotifyIcon(TrayWindow window, IntPtr initialIcon, ILogger log)
    {
        _window = window;
        _icon = initialIcon;
        _log = log;
    }

    public bool IsAdded => _added;

    /// <summary>
    /// Adds the icon. When the shell is not ready yet (agent started at logon before Explorer's tray), retries every 2 s for a
    /// minute; TaskbarCreated also re-adds it once the taskbar appears.
    /// </summary>
    public bool Add()
    {
        if (_disposed) return false;
        var nid = NewData();
        nid.uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP | Native.NIF_SHOWTIP;
        nid.uCallbackMessage = TrayWindow.TrayCallbackMessage;
        nid.hIcon = _icon;
        Native.CopyTo(nid.szTip, TipCapacity, _tip);

        var ok = Native.Shell_NotifyIconW(Native.NIM_ADD, &nid);
        if (!ok)
        {
            var error = Marshal.GetLastPInvokeError();
            // NIM_ADD can report a timeout although the shell added the icon (busy Explorer), or the icon may still exist
            // from before a TaskbarCreated: a successful NIM_MODIFY means it is there.
            nid.uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP | Native.NIF_SHOWTIP;
            ok = Native.Shell_NotifyIconW(Native.NIM_MODIFY, &nid);
            if (!ok)
            {
                _added = false;
                ScheduleAddRetry(error);
                return false;
            }
        }

        _added = true;
        _addAttempts = 0;
        nid.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        if (!Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &nid))
        {
            _log.LogWarning("Shell_NotifyIcon(NIM_SETVERSION 4) failed ({Error}); tray clicks may not be reported", Marshal.GetLastPInvokeError());
        }
        return true;
    }

    /// <summary>Explorer restarted: the old icon is gone, add it again with the current image and tooltip.</summary>
    public void Readd()
    {
        _added = false;
        _addAttempts = 0;
        Add();
    }

    /// <summary>
    /// Replaces the image (<paramref name="newIcon"/> != zero; ownership passes to this object) and/or the tooltip (non-null)
    /// in one NIM_MODIFY, then destroys the previous image.
    /// </summary>
    public void Update(IntPtr newIcon, string? tip)
    {
        if (_disposed)
        {
            if (newIcon != IntPtr.Zero) Native.DestroyIcon(newIcon);
            return;
        }

        var old = IntPtr.Zero;
        if (newIcon != IntPtr.Zero)
        {
            old = _icon;
            _icon = newIcon;
        }
        if (tip is not null) _tip = tip;

        if (_added)
        {
            var nid = NewData();
            nid.uFlags = Native.NIF_SHOWTIP;
            if (newIcon != IntPtr.Zero)
            {
                nid.uFlags |= Native.NIF_ICON;
                nid.hIcon = newIcon;
            }
            if (tip is not null)
            {
                nid.uFlags |= Native.NIF_TIP;
                Native.CopyTo(nid.szTip, TipCapacity, tip);
            }
            if (!Native.Shell_NotifyIconW(Native.NIM_MODIFY, &nid))
            {
                _log.LogDebug("Shell_NotifyIcon(NIM_MODIFY) failed ({Error})", Marshal.GetLastPInvokeError());
            }
        }

        // The shell keeps its own copy; ours is no longer needed (when not added, Add() uses the new one later).
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    /// <summary>Balloon / Windows notification (NIF_INFO, warning icon). Keeps the current image and tooltip.</summary>
    public bool ShowBalloon(string title, string text)
    {
        if (_disposed || !_added) return false;
        var nid = NewData();
        nid.uFlags = Native.NIF_INFO | Native.NIF_SHOWTIP;
        nid.dwInfoFlags = Native.NIIF_WARNING;
        Native.CopyTo(nid.szInfoTitle, InfoTitleCapacity, title);
        Native.CopyTo(nid.szInfo, InfoCapacity, text);
        var ok = Native.Shell_NotifyIconW(Native.NIM_MODIFY, &nid);
        if (!ok) _log.LogWarning("Shell_NotifyIcon(NIF_INFO) failed ({Error})", Marshal.GetLastPInvokeError());
        return ok;
    }

    private void ScheduleAddRetry(int error)
    {
        _addAttempts++;
        if (_addAttempts == 1) _log.LogWarning("Shell_NotifyIcon(NIM_ADD) failed ({Error}); retrying every {Seconds} s", error, AddRetryDelay.TotalSeconds);
        if (_addAttempts >= MaxAddAttempts)
        {
            _log.LogWarning("Tray icon still not added after {Attempts} attempts; waiting for TaskbarCreated", _addAttempts);
            return;
        }
        if (_retryTimer != 0) return;
        _retryTimer = _window.StartTimer(AddRetryDelay, () =>
        {
            _retryTimer = 0;
            if (!_added && !_disposed) Add();
        });
    }

    private Native.NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = IconId,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.StopTimer(_retryTimer);
        if (_added)
        {
            var nid = NewData();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, &nid);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            Native.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
