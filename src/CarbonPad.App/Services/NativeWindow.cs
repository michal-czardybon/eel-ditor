using System.Runtime.InteropServices;

namespace CarbonPad.App.Services;

/// <summary>Windows: bring a window to the foreground even when the process was started by a background process.</summary>
internal static class NativeWindow
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }

    private static readonly IntPtr TopMost = new(-1), NoTopMost = new(-2);
    private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010 | 0x0040; // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW

    /// <summary>
    /// Windows refuses SetForegroundWindow to a process that did not receive the last input (e.g. one started by
    /// `dotnet run` or an IDE); attaching to the input queue of the current foreground window lifts that restriction.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        if (foreground == IntPtr.Zero)
        {
            // Nobody has the foreground (e.g. the process that handed over its files has just exited).
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            RaiseIfRefused(hwnd);
            return;
        }
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
        RaiseIfRefused(hwnd);
    }

    /// <summary>If Windows kept the focus elsewhere, still show the window above the others and flash its taskbar button.</summary>
    private static void RaiseIfRefused(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        SetWindowPos(hwnd, TopMost, 0, 0, 0, 0, NoMoveSizeActivate);
        SetWindowPos(hwnd, NoTopMost, 0, 0, 0, 0, NoMoveSizeActivate);
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = 0x2 | 0xC, Count = 3 }; // TRAY | TIMERNOFG
        FlashWindowEx(ref info);
    }
}
