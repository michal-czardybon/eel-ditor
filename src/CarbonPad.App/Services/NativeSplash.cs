using System.Runtime.InteropServices;

namespace CarbonPad.App.Services;

/// <summary>
/// Windows: a small native splash (icon and name) shown before Avalonia starts, so something appears within
/// a fraction of a second; closed when the main window opens. Painted with GDI, no extra dependencies.
/// The interop uses source-generated <see cref="LibraryImportAttribute"/> calls and an unmanaged function
/// pointer for the window procedure: both are compiled ahead of time, so no marshalling stubs are
/// generated at run time before the splash can appear.
/// </summary>
internal static unsafe partial class NativeSplash
{
    private static IntPtr _hwnd;
    private static string _title = "", _subtitle = "";
    private static readonly IntPtr ClassName = Marshal.StringToHGlobalUni("CarbonEditorSplash");

    /// <summary>Shows the splash at once; the subtitle can follow when the language is known.</summary>
    public static void Show(string title, string subtitle = "")
    {
        if (!OperatingSystem.IsWindows() || _hwnd != IntPtr.Zero) return;
        _title = title;
        _subtitle = subtitle;
        try
        {
            IntPtr module = GetModuleHandleW(IntPtr.Zero);
            var cls = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = module,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_APPSTARTING),
                lpszClassName = ClassName,
            };
            RegisterClassExW(&cls);
            double scale = DpiScale();
            int w = (int)(480 * scale), h = (int)(150 * scale);
            int x = (GetSystemMetrics(SM_CXSCREEN) - w) / 2, y = (GetSystemMetrics(SM_CYSCREEN) - h) / 2;
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, title, WS_POPUP | WS_VISIBLE, x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            UpdateWindow(_hwnd); // paints now, before the message loop exists
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            StartupTrace.Mark("splash failed: " + ex.Message);
            _hwnd = IntPtr.Zero;
        }
    }

    public static void SetSubtitle(string subtitle)
    {
        _subtitle = subtitle;
        if (_hwnd == IntPtr.Zero) return;
        InvalidateRect(_hwnd, IntPtr.Zero, 1);
        UpdateWindow(_hwnd);
    }

    public static void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_PAINT) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try { Paint(hwnd); }
        catch (Exception) { /* never let an exception cross into native code */ }
        return IntPtr.Zero;
    }

    private static void Paint(IntPtr hwnd)
    {
        PAINTSTRUCT ps;
        IntPtr dc = BeginPaint(hwnd, &ps);
        RECT rc;
        GetClientRect(hwnd, &rc);
        double scale = DpiScale();
        IntPtr bg = CreateSolidBrush(0x00221F1E); // #1E1F22 (BGR)
        FillRect(dc, &rc, bg);
        DeleteObject(bg);
        int icon = (int)(64 * scale), pad = (int)(28 * scale);
        IntPtr hIcon = ExtractIconW(GetModuleHandleW(IntPtr.Zero), Environment.ProcessPath ?? "", 0);
        if (hIcon != IntPtr.Zero)
        {
            DrawIconEx(dc, pad, (rc.Bottom - icon) / 2, hIcon, icon, icon, 0, IntPtr.Zero, DI_NORMAL);
            DestroyIcon(hIcon);
        }
        SetBkMode(dc, TRANSPARENT);
        int left = pad * 2 + icon;
        IntPtr big = CreateFontW(-(int)(24 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        IntPtr small = CreateFontW(-(int)(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        IntPtr old = SelectObject(dc, big);
        SetTextColor(dc, 0x00F0EDEA);
        var r1 = new RECT { Left = left, Top = rc.Bottom / 2 - (int)(30 * scale), Right = rc.Right - pad, Bottom = rc.Bottom / 2 + (int)(4 * scale) };
        DrawTextW(dc, _title, -1, &r1, DT_SINGLELINE | DT_NOPREFIX);
        if (_subtitle.Length > 0)
        {
            SelectObject(dc, small);
            SetTextColor(dc, 0x00989088);
            var r2 = new RECT { Left = left, Top = rc.Bottom / 2 + (int)(8 * scale), Right = rc.Right - pad, Bottom = rc.Bottom };
            DrawTextW(dc, _subtitle, -1, &r2, DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
        }
        SelectObject(dc, old);
        DeleteObject(big);
        DeleteObject(small);
        EndPaint(hwnd, &ps);
    }

    private static double DpiScale()
    {
        try { return GetDpiForSystem() / 96.0; }
        catch (EntryPointNotFoundException) { return 1; }
    }

    private const uint WM_PAINT = 0x000F, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, TRANSPARENT = 1, IDC_APPSTARTING = 32650;
    private const uint DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* c);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr CreateWindowExW(uint ex, IntPtr cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [LibraryImport("user32.dll")] private static partial IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll")] private static partial int UpdateWindow(IntPtr h);
    [LibraryImport("user32.dll")] private static partial int InvalidateRect(IntPtr h, IntPtr rect, int erase);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(IntPtr h);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int i);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] private static partial IntPtr LoadCursorW(IntPtr inst, int id);
    [LibraryImport("user32.dll")] private static partial IntPtr BeginPaint(IntPtr h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int EndPaint(IntPtr h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int GetClientRect(IntPtr h, RECT* r);
    [LibraryImport("user32.dll")] private static partial int FillRect(IntPtr dc, RECT* r, IntPtr brush);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(IntPtr dc, string text, int len, RECT* r, uint format);
    [LibraryImport("user32.dll")] private static partial int DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
    [LibraryImport("user32.dll")] private static partial int DestroyIcon(IntPtr icon);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr ExtractIconW(IntPtr inst, string file, int index);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetModuleHandleW(IntPtr name);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(IntPtr dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(IntPtr dc, uint color);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
}
