using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Log.Shell;

// Plain Win32 imports. Blittable only, so AOT needs no marshalling stubs.
internal static unsafe class Native
{
    // Window messages
    public const uint WM_DESTROY = 0x2, WM_SIZE = 0x5, WM_SETFOCUS = 0x7, WM_PAINT = 0xF, WM_CLOSE = 0x10,
        WM_ERASEBKGND = 0x14, WM_SHOWWINDOW = 0x18, WM_ACTIVATE = 0x6, WM_MOUSEACTIVATE = 0x21, WM_GETTEXT = 0xD,
        WM_NCCREATE = 0x81, WM_NCDESTROY = 0x82, WM_NCHITTEST = 0x84, WM_KEYDOWN = 0x100, WM_COMMAND = 0x111,
        WM_TIMER = 0x113, WM_DRAWITEM = 0x2B, WM_SETTINGCHANGE = 0x1A, WM_THEMECHANGED = 0x31A,
        WM_DPICHANGED = 0x2E0, WM_GETDLGCODE = 0x87, WM_APP = 0x8000, WM_POWERBROADCAST = 0x218,
        WM_WTSSESSION_CHANGE = 0x2B1, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205, WM_NCLBUTTONDOWN = 0xA1,
        WM_SYSCOLORCHANGE = 0x15, WM_PRINTCLIENT = 0x318, WM_KILLFOCUS = 0x8, WM_ENABLE = 0xA;

    public const uint BM_CLICK = 0xF5;

    // Window styles
    public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
        WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000, WS_TABSTOP = 0x00010000, WS_GROUP = 0x00020000,
        WS_DISABLED = 0x08000000;
    public const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000, WS_EX_TOPMOST = 0x8;
    public const uint BS_OWNERDRAW = 0xB, SS_NOPREFIX = 0x80;

    public const int GWLP_USERDATA = -21, GWLP_WNDPROC = -4, GWL_EXSTYLE = -20;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_SHOWNA = 8, SW_RESTORE = 9;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly nint HWND_TOPMOST = -1, HWND_MESSAGE = -3;
    public const int HTTRANSPARENT = -1, HTCLIENT = 1, HTCAPTION = 2;
    public const int MA_NOACTIVATE = 3;
    public const uint DLGC_BUTTON = 0x2, DLGC_WANTALLKEYS = 0x4;
    public const int VK_RETURN = 0xD, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_DOWN = 0x28;
    public const int IDCANCEL = 2;

    // Owner draw
    public const uint ODS_SELECTED = 1, ODS_DISABLED = 4, ODS_FOCUS = 0x10, ODS_HOTLIGHT = 0x40;

    // System colors and metrics
    public const int COLOR_WINDOW = 5, COLOR_WINDOWTEXT = 8, COLOR_HIGHLIGHT = 13, COLOR_HIGHLIGHTTEXT = 14,
        COLOR_GRAYTEXT = 17, COLOR_BTNFACE = 15, COLOR_BTNTEXT = 18, COLOR_HOTLIGHT = 26;
    public const uint SPI_GETHIGHCONTRAST = 0x42, HCF_HIGHCONTRASTON = 1;
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_CXSMICON = 49;

    // Shell notify icon
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        public fixed byte rgb[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemAction, itemState;
        public nint hwndItem, hDC;
        public RECT rcItem;
        public nuint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags; public Guid guid; public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BROWSEINFO
    {
        public nint hwndOwner, pidlRoot; public char* pszDisplayName, lpszTitle; public uint ulFlags;
        public nint lpfn, lParam; public int iImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIGHCONTRAST { public uint cbSize, dwFlags; public nint lpszDefaultScheme; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    // kernel32 / ole32
    [DllImport("kernel32", ExactSpelling = true)] public static extern nint GetModuleHandleW(char* name);
    [DllImport("advapi32", ExactSpelling = true)] public static extern int RegGetValueW(nint hkey, char* subKey, char* value, uint flags, uint* type, uint* data, uint* size);
    [DllImport("kernel32", ExactSpelling = true)] public static extern nint GetCurrentProcess();
    [DllImport("kernel32", ExactSpelling = true)] public static extern int K32EmptyWorkingSet(nint process);
    [DllImport("kernel32", ExactSpelling = true)] public static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);
    [DllImport("ole32", ExactSpelling = true)] public static extern int CoInitializeEx(nint reserved, uint flags);

    // user32: windows
    [DllImport("user32", ExactSpelling = true)] public static extern ushort RegisterClassExW(WNDCLASSEX* c);
    [DllImport("user32", ExactSpelling = true)] public static extern nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32", ExactSpelling = true)] public static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32", ExactSpelling = true)] public static extern nint CallWindowProcW(nint proc, nint h, uint m, nint w, nint l);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetMessageW(MSG* m, nint h, uint a, uint b);
    [DllImport("user32", ExactSpelling = true)] public static extern int TranslateMessage(MSG* m);
    [DllImport("user32", ExactSpelling = true)] public static extern nint DispatchMessageW(MSG* m);
    [DllImport("user32", ExactSpelling = true)] public static extern int PeekMessageW(MSG* m, nint h, uint a, uint b, uint remove);
    [DllImport("user32", ExactSpelling = true)] public static extern int RedrawWindow(nint h, RECT* r, nint rgn, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern int IsDialogMessageW(nint dlg, MSG* m);
    [DllImport("user32", ExactSpelling = true)] public static extern void PostQuitMessage(int code);
    [DllImport("user32", ExactSpelling = true)] public static extern int PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32", ExactSpelling = true)] public static extern nint SendMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32", ExactSpelling = true)] public static extern int DestroyWindow(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int ShowWindow(nint h, int cmd);
    [DllImport("user32", ExactSpelling = true)] public static extern int IsWindowVisible(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int IsIconic(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int SetWindowPos(nint h, nint after, int x, int y, int w, int cy, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern int MoveWindow(nint h, int x, int y, int w, int cy, int repaint);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetClientRect(nint h, RECT* r);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetWindowRect(nint h, RECT* r);
    [DllImport("user32", ExactSpelling = true)] public static extern int ClientToScreen(nint h, POINT* p);
    [DllImport("user32", ExactSpelling = true)] public static extern int SetWindowRgn(nint h, nint rgn, int redraw);
    [DllImport("user32", ExactSpelling = true)] public static extern int InvalidateRect(nint h, RECT* r, int erase);
    [DllImport("user32", ExactSpelling = true)] public static extern int UpdateWindow(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern nint BeginPaint(nint h, PAINTSTRUCT* p);
    [DllImport("user32", ExactSpelling = true)] public static extern int EndPaint(nint h, PAINTSTRUCT* p);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetDC(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32", ExactSpelling = true)] public static extern int PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern int SetWindowTextW(nint h, char* text);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetWindowTextW(nint h, char* text, int max);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetWindowTextLengthW(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int EnableWindow(nint h, int enable);
    [DllImport("user32", ExactSpelling = true)] public static extern nint SetFocus(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetFocus();
    [DllImport("user32", ExactSpelling = true)] public static extern int SetForegroundWindow(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetParent(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetDlgCtrlID(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetDlgItem(nint h, int id);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32", ExactSpelling = true)] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32", ExactSpelling = true)] public static extern uint GetDpiForSystem();
    [DllImport("user32", ExactSpelling = true)] public static extern nint SetTimer(nint h, nint id, uint ms, nint f);
    [DllImport("user32", ExactSpelling = true)] public static extern int KillTimer(nint h, nint id);
    [DllImport("user32", ExactSpelling = true)] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport("user32", ExactSpelling = true)] public static extern nint GetWindowLongPtrW(nint h, int idx);
    [DllImport("user32", ExactSpelling = true)] public static extern nint SetWindowLongPtrW(nint h, int idx, nint value);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetSystemMetrics(int i);
    [DllImport("user32", ExactSpelling = true)] public static extern int SystemParametersInfoW(uint action, uint param, void* data, uint winIni);
    [DllImport("user32", ExactSpelling = true)] public static extern uint GetSysColor(int index);
    [DllImport("user32", ExactSpelling = true)] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetMonitorInfoW(nint mon, MONITORINFO* info);
    [DllImport("user32", ExactSpelling = true)] public static extern int GetCursorPos(POINT* p);
    [DllImport("user32", ExactSpelling = true)] public static extern nint CreatePopupMenu();
    [DllImport("user32", ExactSpelling = true)] public static extern int AppendMenuW(nint menu, uint flags, nuint id, char* text);
    [DllImport("user32", ExactSpelling = true)] public static extern int DestroyMenu(nint menu);
    [DllImport("user32", ExactSpelling = true)] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint prm);
    [DllImport("user32", ExactSpelling = true)] public static extern uint RegisterWindowMessageW(char* name);
    [DllImport("user32", ExactSpelling = true)] public static extern int DestroyIcon(nint icon);
    [DllImport("user32", ExactSpelling = true)] public static extern nint CreateIconFromResourceEx(byte* bits, uint size, int isIcon, uint version, int cx, int cy, uint flags);
    [DllImport("user32", ExactSpelling = true)] public static extern int MessageBoxW(nint h, char* text, char* caption, uint type);

    // gdi32
    [DllImport("gdi32", ExactSpelling = true)] public static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32", ExactSpelling = true)] public static extern int CombineRgn(nint dst, nint a, nint b, int mode);
    [DllImport("gdi32", ExactSpelling = true)] public static extern nint CreatePolygonRgn(POINT* pts, int count, int mode);
    [DllImport("gdi32", ExactSpelling = true)] public static extern int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32", ExactSpelling = true)] public static extern int DeleteObject(nint o);
    [DllImport("gdi32", ExactSpelling = true)] public static extern int DeleteDC(nint dc);
    [DllImport("gdi32", ExactSpelling = true)] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32", ExactSpelling = true)] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32", ExactSpelling = true)] public static extern nint SelectObject(nint dc, nint o);
    [DllImport("gdi32", ExactSpelling = true)] public static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, BITMAPINFOHEADER* bi, uint usage);

    // shell32 / dwmapi
    [DllImport("shell32", ExactSpelling = true)] public static extern nint SHBrowseForFolderW(BROWSEINFO* bi);
    [DllImport("shell32", ExactSpelling = true)] public static extern int SHGetPathFromIDListW(nint pidl, char* path);
    [DllImport("shell32", ExactSpelling = true)] public static extern nint ShellExecuteW(nint h, char* verb, char* file, char* args, char* dir, int show);
    [DllImport("ole32", ExactSpelling = true)] public static extern void CoTaskMemFree(nint p);
    [DllImport("shell32", ExactSpelling = true)] public static extern int Shell_NotifyIconW(uint msg, NOTIFYICONDATA* d);
    [DllImport("shell32", ExactSpelling = true)] public static extern uint ExtractIconExW(char* file, int index, nint* large, nint* small, uint n);
    [DllImport("dwmapi", ExactSpelling = true)] public static extern int DwmSetWindowAttribute(nint h, int attr, int* value, int size);

    /// <summary>Folder picker (UI thread). Null when cancelled.</summary>
    public static string? PickFolder(nint owner, string title)
    {
        var display = stackalloc char[260];
        fixed (char* t = title)
        {
            var bi = new BROWSEINFO { hwndOwner = owner, pszDisplayName = display, lpszTitle = t, ulFlags = 0x1 | 0x40 }; // folders only, new style
            var pidl = SHBrowseForFolderW(&bi);
            if (pidl == 0) return null;
            var path = stackalloc char[520];
            var ok = SHGetPathFromIDListW(pidl, path) != 0;
            CoTaskMemFree(pidl);
            return ok ? new string(path) : null;
        }
    }

    /// <summary>Opens a file with its default app.</summary>
    public static bool OpenWithDefault(string path)
    {
        fixed (char* verb = "open")
        fixed (char* file = path)
            return (long)ShellExecuteW(0, verb, file, null, null, SW_SHOW) > 32;
    }

    /// <summary>Returns idle pages to Windows; a hidden tray process should hold little memory.</summary>
    public static void TrimWorkingSet() => K32EmptyWorkingSet(GetCurrentProcess());

    public static nint Hinstance => GetModuleHandleW(null);

    public static string GetText(nint h)
    {
        var n = GetWindowTextLengthW(h);
        if (n <= 0) return "";
        var buf = new char[n + 1];
        fixed (char* p = buf) { GetWindowTextW(h, p, n + 1); }
        return new string(buf, 0, n);
    }

    public static void SetText(nint h, string text)
    {
        fixed (char* p = text) { SetWindowTextW(h, p); }
    }

    public static nint CreateChild(string cls, string text, uint style, nint parent, int id, int x, int y, int w, int h)
    {
        fixed (char* c = cls)
        fixed (char* t = text)
            return CreateWindowExW(0, c, t, style | WS_CHILD | WS_VISIBLE, x, y, w, h, parent, id, Hinstance, 0);
    }
}
