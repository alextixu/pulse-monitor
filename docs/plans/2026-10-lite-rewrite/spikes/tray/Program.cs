// SpikeTray — headless .NET 9 notification-area (tray) icon spike.
// No Avalonia / WPF / WinForms. Raw user32/shell32/gdi32 P/Invoke + System.Drawing.Common for the
// primary glyph renderer, plus a pure-GDI DIB/CreateIconIndirect fallback renderer.
//
// Usage:  SpikeTray.exe            -> System.Drawing renderer (GDI fallback used for tick 0 only)
//         SpikeTray.exe --gdi      -> pure GDI renderer for every tick (System.Drawing never loaded)
//         SpikeTray.exe --seconds N (default 25)

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpikeTray;

internal static unsafe class Program
{
    // ---------------------------------------------------------------- config / state
    static int DurationSeconds = 25;
    const uint TRAY_ID = 1;
    const uint CMD_OPEN = 100, CMD_QUIT = 101;

    // Private window messages (WM_APP range, never collide with system messages)
    const uint WM_TRAY = Native.WM_APP + 1;   // Shell_NotifyIcon callback (uCallbackMessage)
    const uint WM_TICK = Native.WM_APP + 2;   // posted from the System.Threading.Timer thread
    const uint WM_QUITREQ = Native.WM_APP + 3;   // posted from Ctrl+C handler

    static IntPtr _hwnd;
    static IntPtr _hIcon;                 // icon currently handed to the shell
    static uint _wmTaskbarCreated;
    static Timer? _timer;
    static int _tick;
    static int _smallIconSize;            // GetSystemMetrics(SM_CXSMICON)
    static bool _gdiOnly;
    static bool _menuOpen;
    static bool _trayAdded;
    static int _lastValue = -1, _lastSize = 32;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly string OutDir = AppContext.BaseDirectory;

    static readonly (byte r, byte g, byte b) Green = (52, 199, 89);
    static readonly (byte r, byte g, byte b) Orange = (255, 159, 10);
    static readonly (byte r, byte g, byte b) Red = (255, 69, 58);

    // ---------------------------------------------------------------- entry
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--gdi") _gdiOnly = true;
            else if (args[i] == "--seconds" && i + 1 < args.Length) DurationSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }

        Log($"SpikeTray start  runtime={RuntimeInformation.FrameworkDescription}  renderer={(_gdiOnly ? "GDI only" : "System.Drawing (GDI+)")}  pid={Environment.ProcessId}");
        Log($"runtimeconfig: GC.Concurrent={AppContext.GetData("System.GC.Concurrent") ?? "(default)"}  GC.ConserveMemory={AppContext.GetData("System.GC.ConserveMemory") ?? "(default)"}  " +
            $"Globalization.Invariant={AppContext.GetData("System.Globalization.Invariant") ?? "(default)"}  TieredPGO={AppContext.GetData("System.Runtime.TieredPGO") ?? "(default)"}  " +
            $"UseSystemResourceKeys={AppContext.GetData("System.Resources.UseSystemResourceKeys") ?? "(default)"}");

        // DPI: opt in to per-monitor v2 so GetSystemMetrics reports the real small-icon size.
        bool dpiOk = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _smallIconSize = Native.GetSystemMetrics(Native.SM_CXSMICON);
        Log($"DPI: SetProcessDpiAwarenessContext(PMv2)={dpiOk}  GetDpiForSystem={Native.GetDpiForSystem()}  SM_CXSMICON={_smallIconSize}  (will use {_smallIconSize}px for t<=12, 32px for t>12)");

        PrintMeasure("t=0 baseline (before window/icon)");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Native.PostMessageW(_hwnd, WM_QUITREQ, IntPtr.Zero, IntPtr.Zero); };

        try
        {
            CreateMessageWindow();

            // First icon: always exercise the pure-GDI fallback once so we prove it compiles and yields an HICON.
            _hIcon = RenderIconGdi(32, 87, 0.87f, Red);
            Log($"GDI fallback icon (32px, '87'): hIcon=0x{_hIcon.ToInt64():X}  GDI objects now={GuiRes(Native.GR_GDIOBJECTS)}");
            if (!_gdiOnly)
            {
                SaveGdiIconPreview(32, 87, 0.87f, Red, Path.Combine(OutDir, "render-gdi-32px-87.png"));
                SaveGdiIconPreview(_smallIconSize, 87, 0.87f, Red, Path.Combine(OutDir, $"render-gdi-{_smallIconSize}px-87.png"));
            }

            if (!TrayAdd(_hIcon, "Spike starting…")) Log("!! Shell_NotifyIcon(NIM_ADD) failed");
            else Log("Shell_NotifyIcon NIM_ADD + NIM_SETVERSION(4) ok");

            _timer = new Timer(_ =>
            {
                int t = Interlocked.Increment(ref _tick);
                // Marshal to the window thread: never touch Shell_NotifyIcon/GDI from the pool thread.
                Native.PostMessageW(_hwnd, WM_TICK, (IntPtr)t, IntPtr.Zero);
            }, null, 1000, 1000);

            RunMessageLoop();
        }
        finally
        {
            _timer?.Dispose();
            if (_trayAdded) TrayDelete();
            if (_hIcon != IntPtr.Zero) { Native.DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        }

        PrintMeasure("exit (icon removed, before GC)");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        PrintMeasure("exit (after GC.Collect)");
        Console.Out.Flush();
        return 0;
    }

    // ---------------------------------------------------------------- window + loop
    static void CreateMessageWindow()
    {
        IntPtr hInstance = Native.GetModuleHandleW(null);
        fixed (char* cls = "SpikeTrayMessageWindow")
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = hInstance,
                lpszClassName = (IntPtr)cls,
            };
            ushort atom = Native.RegisterClassExW(ref wc);
            if (atom == 0) throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");

            // Normal hidden TOP-LEVEL window (parent = NULL). HWND_MESSAGE windows do not receive tray callbacks.
            _hwnd = Native.CreateWindowExW(0, (IntPtr)cls, "SpikeTray", Native.WS_OVERLAPPED,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        _wmTaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        Log($"hidden window hwnd=0x{_hwnd.ToInt64():X}  WM_TASKBARCREATED=0x{_wmTaskbarCreated:X}");
    }

    static void RunMessageLoop()
    {
        Native.MSG msg;
        int r;
        while ((r = Native.GetMessageW(out msg, IntPtr.Zero, 0, 0)) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        if (r < 0) Log($"GetMessage error {Marshal.GetLastWin32Error()}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TICK) { OnTick((int)wParam.ToInt64()); return IntPtr.Zero; }
            if (msg == WM_TRAY) { OnTrayCallback(wParam, lParam); return IntPtr.Zero; }
            if (msg == WM_QUITREQ) { Log("Ctrl+C -> quitting"); Native.PostQuitMessage(0); return IntPtr.Zero; }
            if (msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
            {
                // Explorer restarted: our icon is gone, add it again.
                Log("WM_TASKBARCREATED received -> re-adding icon");
                _trayAdded = false;
                TrayAdd(_hIcon, "Spike (re-added)");
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case Native.WM_COMMAND:
                    OnCommand((uint)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;
                case Native.WM_CLOSE:
                    Native.DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case Native.WM_DESTROY:
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never cross the unmanaged boundary.
            Log($"!! WndProc exception: {ex}");
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- per-second update
    static void OnTick(int t)
    {
        if (t > DurationSeconds) return;

        int value = Math.Min(100, (int)Math.Round(t * 100.0 / (DurationSeconds - 2)));
        int size = t <= DurationSeconds / 2 ? _smallIconSize : 32;
        var tone = value < 60 ? Green : value <= 80 ? Orange : Red;
        float f = value / 100f;

        string? png = null;
        if (!_gdiOnly && (t == 2 || t == DurationSeconds / 2 + 2 || value == 87 || t == DurationSeconds - 1))
            png = Path.Combine(OutDir, $"render-gdiplus-{size}px-{value}.png");

        IntPtr newIcon = _gdiOnly ? RenderIconGdi(size, value, f, tone) : RenderIconGdiPlus(size, value, f, tone, png);
        string tip = $"Spike {value}%\nCPU {30 + t}% · {50 + t / 2}°C";
        bool ok = TrayModify(newIcon, tip);

        // Shell copies the icon on NIM_MODIFY; release ours once the swap succeeded.
        IntPtr old = _hIcon;
        _hIcon = newIcon;
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
        _lastValue = value; _lastSize = size;

        Log($"t={t,2}s value={value,3} size={size}px tone={(tone == Green ? "green" : tone == Orange ? "orange" : "red"),-6} modify={ok} GDI={GuiRes(Native.GR_GDIOBJECTS)} USER={GuiRes(Native.GR_USEROBJECTS)}{(png != null ? "  saved " + Path.GetFileName(png) : "")}");

        if (t == 3)
        {
            bool b = TrayBalloon("Spike warning", $"Balloon test at t=3s ({value}%)");
            Log($"  balloon (NIF_INFO, NIIF_WARNING) sent={b}");
        }
        if (t == 2)
        {
            // Self-test of the context menu path without user interaction: fake a VERSION_4 WM_CONTEXTMENU callback.
            // Done early (t=2..3) on purpose: the first popup menu makes user32/uxtheme cache a couple of GDI objects
            // (one-off), and we want the t=5 -> t=25 window to measure icon swaps only.
            var (x, y) = MenuAnchor();
            Log($"  self-test: posting synthetic WM_CONTEXTMENU tray callback at ({x},{y}); menu auto-closes at t=3");
            Native.PostMessageW(_hwnd, WM_TRAY, (IntPtr)MakeLong(x, y), (IntPtr)MakeLong((int)Native.WM_CONTEXTMENU, (int)TRAY_ID));
        }
        if (t == 3 && _menuOpen)
        {
            // We are being dispatched from inside TrackPopupMenuEx's modal loop; EndMenu closes it.
            bool e = Native.EndMenu();
            Log($"  self-test: EndMenu()={e}");
        }
        if (t == 5) { PrintMeasure("t=5"); CaptureTrayRect("t5"); }
        if (t == DurationSeconds - 5) CaptureTrayRect($"t{t}");
        if (t == DurationSeconds)
        {
            PrintMeasure($"t={t} (icon still shown)");
            _timer?.Dispose(); _timer = null;
            TrayDelete();
            Native.DestroyIcon(_hIcon); _hIcon = IntPtr.Zero;
            PrintMeasure($"t={t} (icon removed)");
            Native.PostQuitMessage(0);
        }
    }

    static (int x, int y) MenuAnchor()
    {
        if (Native.Shell_NotifyIconGetRect(NewIdentifier(), out var rc) == 0 && IsOnScreen(rc))
            return ((rc.left + rc.right) / 2, rc.top);
        // Fallback: bottom-right of the primary work area.
        var wa = new Native.RECT();
        Native.SystemParametersInfoW(Native.SPI_GETWORKAREA, 0, &wa, 0);
        return (wa.right - 8, wa.bottom - 8);
    }

    // ---------------------------------------------------------------- tray callback (NOTIFYICON_VERSION_4 semantics)
    static void OnTrayCallback(IntPtr wParam, IntPtr lParam)
    {
        // v4: LOWORD(lParam)=event, HIWORD(lParam)=icon id, wParam = anchor point (x in low word, y in high word)
        long lp = lParam.ToInt64(), wp = wParam.ToInt64();
        uint evt = (uint)(lp & 0xFFFF);
        uint id = (uint)((lp >> 16) & 0xFFFF);
        int x = (short)(wp & 0xFFFF), y = (short)((wp >> 16) & 0xFFFF);

        string name = evt switch
        {
            Native.WM_LBUTTONDOWN => "WM_LBUTTONDOWN", Native.WM_LBUTTONUP => "WM_LBUTTONUP", Native.WM_LBUTTONDBLCLK => "WM_LBUTTONDBLCLK",
            Native.WM_RBUTTONDOWN => "WM_RBUTTONDOWN", Native.WM_RBUTTONUP => "WM_RBUTTONUP", Native.WM_MBUTTONUP => "WM_MBUTTONUP",
            Native.WM_CONTEXTMENU => "WM_CONTEXTMENU", Native.WM_MOUSEMOVE => "WM_MOUSEMOVE",
            Native.NIN_SELECT => "NIN_SELECT", Native.NIN_KEYSELECT => "NIN_KEYSELECT",
            Native.NIN_BALLOONSHOW => "NIN_BALLOONSHOW", Native.NIN_BALLOONHIDE => "NIN_BALLOONHIDE",
            Native.NIN_BALLOONTIMEOUT => "NIN_BALLOONTIMEOUT", Native.NIN_BALLOONUSERCLICK => "NIN_BALLOONUSERCLICK",
            Native.NIN_POPUPOPEN => "NIN_POPUPOPEN", Native.NIN_POPUPCLOSE => "NIN_POPUPCLOSE",
            _ => $"0x{evt:X}"
        };
        if (evt != Native.WM_MOUSEMOVE) Log($"  tray event {name} id={id} at ({x},{y})");

        switch (evt)
        {
            case Native.WM_LBUTTONUP:   // v4 sends both WM_LBUTTONUP and NIN_SELECT for a left click; act on NIN_SELECT only
                break;
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                OnCommand(CMD_OPEN);
                break;
            case Native.WM_CONTEXTMENU: // right-click release or keyboard (Shift+F10 / Apps key)
                ShowContextMenu(x, y);
                break;
        }
    }

    static void ShowContextMenu(int x, int y)
    {
        if (_menuOpen) return;
        IntPtr menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)CMD_OPEN, "Open");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)CMD_QUIT, "Quit");

        // Required dance for popup menus owned by a hidden window: become foreground first, otherwise the
        // menu will not dismiss when the user clicks elsewhere (classic KB135788 issue).
        Native.SetForegroundWindow(_hwnd);
        _menuOpen = true;
        int cmd = Native.TrackPopupMenuEx(menu,
            Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_BOTTOMALIGN | Native.TPM_RIGHTALIGN,
            x, y, _hwnd, IntPtr.Zero);
        _menuOpen = false;
        // Force a task switch so the menu disappears immediately (second half of the KB135788 dance).
        Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Native.DestroyMenu(menu);
        Log($"  menu closed, cmd={cmd}  (0 = cancelled)");
        if (cmd != 0) OnCommand((uint)cmd);
    }

    static void OnCommand(uint cmd)
    {
        switch (cmd)
        {
            case CMD_OPEN: Log("  command: Open"); break;
            case CMD_QUIT: Log("  command: Quit -> exiting early"); Native.PostQuitMessage(0); break;
        }
    }

    // ---------------------------------------------------------------- Shell_NotifyIcon wrappers
    static Native.NOTIFYICONDATAW NewNid()
    {
        var nid = new Native.NOTIFYICONDATAW { cbSize = (uint)sizeof(Native.NOTIFYICONDATAW), hWnd = _hwnd, uID = TRAY_ID };
        return nid;
    }

    static Native.NOTIFYICONIDENTIFIER NewIdentifier()
        => new() { cbSize = (uint)sizeof(Native.NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = TRAY_ID };

    static bool TrayAdd(IntPtr icon, string tip)
    {
        var nid = NewNid();
        nid.uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP | Native.NIF_SHOWTIP;
        nid.uCallbackMessage = WM_TRAY;
        nid.hIcon = icon;
        CopyTo(nid.szTip, 128, tip);
        bool ok = Native.Shell_NotifyIconW(Native.NIM_ADD, ref nid);
        if (!ok) { Log($"  NIM_ADD failed, GetLastError={Marshal.GetLastWin32Error()}"); return false; }
        _trayAdded = true;
        nid.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        bool okv = Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref nid);
        if (!okv) Log($"  NIM_SETVERSION failed, GetLastError={Marshal.GetLastWin32Error()}");
        return ok && okv;
    }

    static bool TrayModify(IntPtr icon, string tip)
    {
        var nid = NewNid();
        nid.uFlags = Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        nid.hIcon = icon;
        CopyTo(nid.szTip, 128, tip);
        bool ok = Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
        if (!ok) Log($"  NIM_MODIFY failed, GetLastError={Marshal.GetLastWin32Error()}");
        return ok;
    }

    static bool TrayBalloon(string title, string text)
    {
        var nid = NewNid();
        nid.uFlags = Native.NIF_INFO | Native.NIF_SHOWTIP;
        nid.dwInfoFlags = Native.NIIF_WARNING;
        CopyTo(nid.szInfoTitle, 64, title);
        CopyTo(nid.szInfo, 256, text);
        bool ok = Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
        if (!ok) Log($"  NIM_MODIFY(NIF_INFO) failed, GetLastError={Marshal.GetLastWin32Error()}");
        return ok;
    }

    static void TrayDelete()
    {
        var nid = NewNid();
        bool ok = Native.Shell_NotifyIconW(Native.NIM_DELETE, ref nid);
        _trayAdded = false;
        Log($"  NIM_DELETE ok={ok}");
    }

    static void CaptureTrayRect(string tag)
    {
        int hr = Native.Shell_NotifyIconGetRect(NewIdentifier(), out var rc);
        bool onScreen = hr == 0 && IsOnScreen(rc);
        Log($"  Shell_NotifyIconGetRect hr=0x{hr:X8} rect=({rc.left},{rc.top})-({rc.right},{rc.bottom}) {rc.right - rc.left}x{rc.bottom - rc.top}  onScreen={onScreen}");
        if (!onScreen || _gdiOnly) return;
        try
        {
            // Graphics.CopyFromScreen threw "The handle is invalid" here, so grab the pixels with plain GDI BitBlt.
            int pad = 6, w = rc.right - rc.left + 2 * pad, h = rc.bottom - rc.top + 2 * pad;
            uint* px = stackalloc uint[w * h];
            if (!ScreenGrab(rc.left - pad, rc.top - pad, w, h, px)) { Log($"  capture: BitBlt failed, GetLastError={Marshal.GetLastWin32Error()}"); return; }
            using var small = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(px + y * w, (byte*)data.Scan0 + y * data.Stride, w * 4, w * 4);
            small.UnlockBits(data);
            // Save at 6x so the glyph can be inspected.
            using var big = new Bitmap(w * 6, h * 6, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(big)) { g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(small, 0, 0, big.Width, big.Height); }
            string file = Path.Combine(OutDir, $"tray-capture-{tag}.png");
            big.Save(file, ImageFormat.Png);
            Log($"  captured tray icon from screen -> {Path.GetFileName(file)}");
        }
        catch (Exception ex) { Log($"  capture failed: {ex.Message}"); }
    }

    /// Copies a screen rectangle into `dst` (BGRX, alpha forced to 255) using GDI only.
    static bool ScreenGrab(int x, int y, int w, int h, uint* dst)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32; bmi.bmiHeader.biCompression = Native.BI_RGB;
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr dib = Native.CreateDIBSection(mem, ref bmi, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Native.SelectObject(mem, dib);
        bool ok = Native.BitBlt(mem, 0, 0, w, h, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
        Native.GdiFlush();
        if (ok)
        {
            Buffer.MemoryCopy((void*)bits, dst, (long)w * h * 4, (long)w * h * 4);
            for (int i = 0; i < w * h; i++) dst[i] |= 0xFF000000;
        }
        Native.SelectObject(mem, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(mem);
        Native.ReleaseDC(IntPtr.Zero, screen);
        return ok;
    }

    static bool IsOnScreen(Native.RECT rc)
    {
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        return rc.right > rc.left && rc.bottom > rc.top && rc.left >= vx && rc.top >= vy && rc.right <= vx + vw && rc.bottom <= vy + vh;
    }

    // ---------------------------------------------------------------- renderer 1: System.Drawing (GDI+)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static IntPtr RenderIconGdiPlus(int size, int value, float f, (byte r, byte g, byte b) tone, string? savePng)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // grayscale AA (ClearType would not work on a transparent background); grid-fit keeps digits crisp
            g.Clear(Color.Transparent);

            float stroke = size * 0.125f;
            var rect = new RectangleF(stroke / 2f, stroke / 2f, size - stroke, size - stroke);
            using (var track = new Pen(Color.FromArgb(70, 128, 128, 128), stroke))
                g.DrawEllipse(track, rect);
            if (f > 0.001f)
            {
                using var pen = new Pen(Color.FromArgb(tone.r, tone.g, tone.b), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, rect, -90f, Math.Min(360f, 360f * f));
            }

            string text = value.ToString(CultureInfo.InvariantCulture);
            float fontPx = size * (text.Length >= 3 ? 0.36f : 0.5f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), sf);
        }
        if (savePng != null) bmp.Save(savePng, ImageFormat.Png);
        return bmp.GetHicon(); // caller owns the HICON -> DestroyIcon when replaced
    }

    // ---------------------------------------------------------------- renderer 2: pure GDI (32-bit DIB + CreateIconIndirect)
    static IntPtr RenderIconGdi(int size, int value, float f, (byte r, byte g, byte b) tone)
    {
        uint* pixels = stackalloc uint[size * size];
        RenderPixelsGdi(size, value, f, tone, pixels);

        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = size;
        bmi.bmiHeader.biHeight = -size; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Native.BI_RGB;

        IntPtr hbmColor = Native.CreateDIBSection(IntPtr.Zero, ref bmi, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (hbmColor == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        Buffer.MemoryCopy(pixels, (void*)bits, size * size * 4, size * size * 4);

        // 1bpp AND mask, all zero (= opaque; ignored anyway because the colour bitmap carries alpha). Must be zeroed:
        // CreateBitmap with lpBits=NULL leaves the bits uninitialised.
        int maskStride = ((size + 15) / 16) * 2;
        byte* maskBits = stackalloc byte[maskStride * size];
        new Span<byte>(maskBits, maskStride * size).Clear();
        IntPtr hbmMask = Native.CreateBitmap(size, size, 1, 1, maskBits);

        var ii = new Native.ICONINFO { fIcon = 1, hbmMask = hbmMask, hbmColor = hbmColor };
        IntPtr hIcon = Native.CreateIconIndirect(ref ii);   // copies both bitmaps
        Native.DeleteObject(hbmMask);
        Native.DeleteObject(hbmColor);
        if (hIcon == IntPtr.Zero) throw new InvalidOperationException($"CreateIconIndirect failed: {Marshal.GetLastWin32Error()}");
        return hIcon;
    }

    /// Fills `dst` (size*size, straight/non-premultiplied BGRA, top-down) with the ring glyph using analytic
    /// anti-aliasing for the ring/arc/caps and GDI DrawTextW (ANTIALIASED_QUALITY) for the digits.
    static void RenderPixelsGdi(int size, int value, float f, (byte r, byte g, byte b) tone, uint* dst)
    {
        // --- digits: draw white-on-black into a scratch DIB, use the blue channel as text coverage
        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(Native.BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = size; bmi.bmiHeader.biHeight = -size; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32; bmi.bmiHeader.biCompression = Native.BI_RGB;
        IntPtr hdc = Native.CreateCompatibleDC(IntPtr.Zero);
        IntPtr hbmText = Native.CreateDIBSection(hdc, ref bmi, Native.DIB_RGB_COLORS, out IntPtr textBits, IntPtr.Zero, 0);
        IntPtr oldBmp = Native.SelectObject(hdc, hbmText);
        new Span<uint>((void*)textBits, size * size).Clear();

        string text = value.ToString(CultureInfo.InvariantCulture);
        int fontPx = (int)Math.Round(size * (text.Length >= 3 ? 0.36 : 0.5));
        IntPtr font = Native.CreateFontW(-fontPx, 0, 0, 0, Native.FW_BOLD, 0, 0, 0, Native.DEFAULT_CHARSET,
            Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS, Native.ANTIALIASED_QUALITY, Native.DEFAULT_PITCH, "Segoe UI");
        IntPtr oldFont = Native.SelectObject(hdc, font);
        Native.SetTextColor(hdc, 0x00FFFFFF);
        Native.SetBkMode(hdc, Native.TRANSPARENT);
        var rc = new Native.RECT { left = 0, top = 0, right = size, bottom = size };
        Native.DrawTextW(hdc, text, -1, ref rc, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOCLIP);
        Native.GdiFlush();

        // --- ring geometry
        double c = size / 2.0, stroke = size * 0.125, rOut = size / 2.0, rIn = rOut - stroke, rMid = rOut - stroke / 2;
        double sweep = Math.Min(360.0, 360.0 * f);
        double a0 = -90 * Math.PI / 180, a1 = (-90 + sweep) * Math.PI / 180;
        double cx0 = c + rMid * Math.Cos(a0), cy0 = c + rMid * Math.Sin(a0);
        double cx1 = c + rMid * Math.Cos(a1), cy1 = c + rMid * Math.Sin(a1);
        uint* txt = (uint*)textBits;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double px = x + 0.5 - c, py = y + 0.5 - c;
            double d = Math.Sqrt(px * px + py * py);
            double ring = Clamp01(Math.Min(d - rIn, rOut - d) + 0.5);          // 1px analytic AA on both edges
            double arc = 0;
            if (f > 0.001f)
            {
                double theta = (Math.Atan2(py, px) * 180 / Math.PI + 90 + 360) % 360; // 0 = top, clockwise
                arc = theta <= sweep ? ring : 0;
                double capA = Clamp01(stroke / 2 - Dist(px + c, py + c, cx0, cy0) + 0.5);
                double capB = Clamp01(stroke / 2 - Dist(px + c, py + c, cx1, cy1) + 0.5);
                arc = Math.Max(arc, Math.Max(capA, capB));
            }
            double ta = (txt[y * size + x] & 0xFF) / 255.0;                         // text coverage

            // premultiplied "over" compositing: track -> arc -> text
            double pa = 0, pr = 0, pg = 0, pb = 0;
            Over(ref pa, ref pr, ref pg, ref pb, 128, 128, 128, 0.27 * ring);
            Over(ref pa, ref pr, ref pg, ref pb, tone.r, tone.g, tone.b, arc);
            Over(ref pa, ref pr, ref pg, ref pb, 255, 255, 255, ta);

            // CreateIconIndirect expects straight (non-premultiplied) alpha like a 32bpp .ico XOR mask
            byte A = (byte)Math.Round(pa * 255);
            byte R = pa > 0 ? (byte)Math.Round(Math.Min(255, pr / pa)) : (byte)0;
            byte G = pa > 0 ? (byte)Math.Round(Math.Min(255, pg / pa)) : (byte)0;
            byte B = pa > 0 ? (byte)Math.Round(Math.Min(255, pb / pa)) : (byte)0;
            dst[y * size + x] = (uint)(A << 24 | R << 16 | G << 8 | B);
        }

        Native.SelectObject(hdc, oldFont);
        Native.SelectObject(hdc, oldBmp);
        Native.DeleteObject(font);
        Native.DeleteObject(hbmText);
        Native.DeleteDC(hdc);
    }

    static void Over(ref double pa, ref double pr, ref double pg, ref double pb, byte r, byte g, byte b, double a)
    {
        if (a <= 0) return;
        double k = 1 - a;
        pr = pr * k + r * a; pg = pg * k + g * a; pb = pb * k + b * a; pa = pa * k + a;
    }
    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static double Dist(double x0, double y0, double x1, double y1) { double dx = x0 - x1, dy = y0 - y1; return Math.Sqrt(dx * dx + dy * dy); }

    /// Diagnostic only: run the GDI pixel renderer and write its output through System.Drawing as PNG.
    static void SaveGdiIconPreview(int size, int value, float f, (byte r, byte g, byte b) tone, string file)
    {
        uint* px = stackalloc uint[size * size];
        RenderPixelsGdi(size, value, f, tone, px);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++) Buffer.MemoryCopy(px + y * size, (byte*)data.Scan0 + y * data.Stride, size * 4, size * 4);
        bmp.UnlockBits(data);
        bmp.Save(file, ImageFormat.Png);
        Log($"  saved {Path.GetFileName(file)}");
    }

    // ---------------------------------------------------------------- measurements / helpers
    static uint GuiRes(uint flag) => Native.GetGuiResources(Native.GetCurrentProcess(), flag);

    static void PrintMeasure(string label)
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        long ws = p.WorkingSet64, pb = p.PrivateMemorySize64;
        int threads = p.Threads.Count, handles = p.HandleCount;
        TimeSpan cpu = p.TotalProcessorTime;
        string gdiplus = "not loaded";
        foreach (ProcessModule m in p.Modules)
        {
            if (m.ModuleName.Equals("gdiplus.dll", StringComparison.OrdinalIgnoreCase)) { gdiplus = $"loaded ({m.ModuleMemorySize / 1024} KB image)"; break; }
        }
        var gcInfo = GC.GetGCMemoryInfo();
        Console.WriteLine(
            $"[MEASURE {label}] t={Clock.Elapsed.TotalSeconds:F1}s\n" +
            $"   WorkingSet64        = {ws / 1048576.0,7:F2} MB ({ws:N0} B)\n" +
            $"   PrivateMemorySize64 = {pb / 1048576.0,7:F2} MB ({pb:N0} B)\n" +
            $"   Threads             = {threads}\n" +
            $"   HandleCount         = {handles}\n" +
            $"   GDI objects         = {GuiRes(Native.GR_GDIOBJECTS)}\n" +
            $"   USER objects        = {GuiRes(Native.GR_USEROBJECTS)}\n" +
            $"   Total CPU time      = {cpu.TotalMilliseconds:F0} ms\n" +
            $"   Managed heap        = {GC.GetTotalMemory(false) / 1024.0:F0} KB live, {gcInfo.HeapSizeBytes / 1024.0:F0} KB heap, Gen0/1/2 collections = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}\n" +
            $"   gdiplus.dll         = {gdiplus}");
        Console.Out.Flush();
    }

    static void Log(string s) { Console.WriteLine($"{Clock.Elapsed.TotalSeconds,6:F2}s  {s}"); }

    static long MakeLong(int lo, int hi) => (uint)((lo & 0xFFFF) | ((hi & 0xFFFF) << 16));

    static void CopyTo(char* dst, int capacity, string s)
    {
        int n = Math.Min(s.Length, capacity - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }
}

// ============================================================================ P/Invoke surface
internal static unsafe class Native
{
    // ---- messages
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_CONTEXTMENU = 0x007B, WM_APP = 0x8000, WM_USER = 0x0400;
    public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208;
    public const uint NIN_SELECT = WM_USER + 0, NIN_KEYSELECT = WM_USER + 1, NIN_BALLOONSHOW = WM_USER + 2, NIN_BALLOONHIDE = WM_USER + 3,
                      NIN_BALLOONTIMEOUT = WM_USER + 4, NIN_BALLOONUSERCLICK = WM_USER + 5, NIN_POPUPOPEN = WM_USER + 6, NIN_POPUPCLOSE = WM_USER + 7;

    // ---- window
    public const uint WS_OVERLAPPED = 0x00000000;
    public const int SM_CXSMICON = 49, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const uint SPI_GETWORKAREA = 0x0030;
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    // ---- Shell_NotifyIcon
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_INFO = 0x10, NIF_GUID = 0x20, NIF_REALTIME = 0x40, NIF_SHOWTIP = 0x80;
    public const uint NIIF_NONE = 0, NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // ---- menus
    public const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    public const uint TPM_LEFTBUTTON = 0x0000, TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080, TPM_RIGHTALIGN = 0x0008, TPM_BOTTOMALIGN = 0x0020;

    // ---- GDI
    public const uint BI_RGB = 0, DIB_RGB_COLORS = 0, SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int FW_BOLD = 700, DEFAULT_CHARSET = 1, OUT_DEFAULT_PRECIS = 0, CLIP_DEFAULT_PRECIS = 0, ANTIALIASED_QUALITY = 4, DEFAULT_PITCH = 0, TRANSPARENT = 1;
    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOCLIP = 0x100;
    public const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    // ---- structs
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState; public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;              // union { UINT uTimeout; UINT uVersion; }
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public int fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    // ---- user32
    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, IntPtr lpClassName, string lpWindowName, uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string lpString);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, void* pvParam, uint fWinIni);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EndMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    // ---- shell32
    [DllImport("shell32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(in NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    // ---- kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ---- gdi32
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, void* lpBits);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GdiFlush();
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
        uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);
}
