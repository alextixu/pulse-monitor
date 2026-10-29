using System.Runtime.InteropServices;

namespace Pulse.Agent;

/// <summary>
/// The Win32 surface of the agent (user32 / shell32 / gdi32 / advapi32). Source-generated (LibraryImport) and blittable,
/// so no marshalling stubs are built at run time. Constants and layouts follow the Windows SDK headers.
/// </summary>
internal static unsafe partial class Native
{
    // ---- window messages
    public const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    public const uint WM_DISPLAYCHANGE = 0x007E, WM_CONTEXTMENU = 0x007B, WM_TIMER = 0x0113, WM_DEVICECHANGE = 0x0219;
    public const uint WM_USER = 0x0400, WM_APP = 0x8000;
    public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    public const uint NIN_SELECT = WM_USER + 0, NIN_KEYSELECT = WM_USER + 1, NIN_BALLOONSHOW = WM_USER + 2, NIN_BALLOONHIDE = WM_USER + 3,
                      NIN_BALLOONTIMEOUT = WM_USER + 4, NIN_BALLOONUSERCLICK = WM_USER + 5;
    public const int DBT_DEVNODES_CHANGED = 0x0007;

    // ---- window creation
    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint MSGFLT_ALLOW = 1;

    // ---- Shell_NotifyIcon
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_WARNING = 2;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // ---- menus
    public const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RIGHTALIGN = 0x0008, TPM_BOTTOMALIGN = 0x0020, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;

    // ---- GDI
    public const uint BI_RGB = 0, DIB_RGB_COLORS = 0;
    public const int FW_BOLD = 700, TRANSPARENT = 1;
    public const uint DEFAULT_CHARSET = 1, OUT_DEFAULT_PRECIS = 0, CLIP_DEFAULT_PRECIS = 0, ANTIALIASED_QUALITY = 4, DEFAULT_PITCH = 0;
    public const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOCLIP = 0x100;
    public const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    // ---- security
    public const int SE_KERNEL_OBJECT = 6;
    public const uint DACL_SECURITY_INFORMATION = 0x04, LABEL_SECURITY_INFORMATION = 0x10;
    public const uint SDDL_REVISION_1 = 1;

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

    /// <summary>NOTIFYICONDATAW (976 bytes on x64); szTip 128, szInfo 256, szInfoTitle 64 characters including the terminator.</summary>
    [StructLayout(LayoutKind.Sequential)]
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
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public int fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    // ---- user32: windows and messages
    [LibraryImport("user32.dll", SetLastError = true)] public static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterClassW(char* lpClassName, IntPtr hInstance);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial int GetMessageW(MSG* lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(MSG* lpMsg);
    [LibraryImport("user32.dll")] public static partial IntPtr DispatchMessageW(MSG* lpMsg);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int nExitCode);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string lpString);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(IntPtr hWnd, nuint uIDEvent);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    [LibraryImport("user32.dll")] public static partial uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    // ---- user32: menus and icons
    [LibraryImport("user32.dll")] public static partial IntPtr CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(IntPtr hMenu);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(IntPtr hIcon);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial IntPtr CreateIconIndirect(ICONINFO* piconinfo);
    [LibraryImport("user32.dll")] public static partial int DrawTextW(IntPtr hdc, char* lpchText, int cchText, RECT* lprc, uint format);

    // ---- shell32
    [LibraryImport("shell32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    // ---- kernel32
    [LibraryImport("kernel32.dll")] public static partial IntPtr GetModuleHandleW(char* lpModuleName);
    [LibraryImport("kernel32.dll")] public static partial IntPtr GetCurrentProcess();
    [LibraryImport("kernel32.dll")] public static partial IntPtr LocalFree(IntPtr hMem);

    // ---- gdi32
    [LibraryImport("gdi32.dll", SetLastError = true)] public static partial IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFO* pbmi, uint usage, void** ppvBits, IntPtr hSection, uint offset);
    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, void* lpBits);
    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(IntPtr ho);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GdiFlush();
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(IntPtr hdc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(IntPtr hdc, int mode);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
        uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    // ---- advapi32 (pipe integrity label)
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSecurityDescriptorSacl(IntPtr pSecurityDescriptor, out int lpbSaclPresent, out IntPtr pSacl, out int lpbSaclDefaulted);
    [LibraryImport("advapi32.dll")] public static partial uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);
    [LibraryImport("advapi32.dll")]
    public static partial uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr ppsidOwner, IntPtr ppsidGroup, IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr securityDescriptor, uint requestedStringSdRevision, uint securityInformation, out IntPtr stringSecurityDescriptor, IntPtr stringSecurityDescriptorLen);

    // ---- helpers
    public static int LoWord(IntPtr value) => (int)(value.ToInt64() & 0xFFFF);
    public static int HiWord(IntPtr value) => (int)((value.ToInt64() >> 16) & 0xFFFF);
    /// <summary>Signed 16-bit coordinates packed into a WPARAM (NOTIFYICON_VERSION_4 anchor point).</summary>
    public static (int X, int Y) Point(IntPtr value) => ((short)(value.ToInt64() & 0xFFFF), (short)((value.ToInt64() >> 16) & 0xFFFF));

    /// <summary>Copies <paramref name="text"/> into a fixed UTF-16 buffer, truncated to capacity − 1 and NUL-terminated.</summary>
    public static void CopyTo(char* destination, int capacity, string text)
    {
        var n = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < n; i++) destination[i] = text[i];
        destination[n] = '\0';
    }

    public static uint GuiResources(uint flag) => GetGuiResources(GetCurrentProcess(), flag);
}
