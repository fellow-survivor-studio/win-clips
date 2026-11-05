using System;
using System.Runtime.InteropServices;
using static ClipboardHistory.Interop.NativeMethods;

namespace ClipboardHistory.Interop;

internal static class Input
{
    /// <summary>Cursor position in physical pixels.</summary>
    public static (int X, int Y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Move a window so it opens at the cursor, kept inside the work area of the
    /// monitor under the cursor. Sizes are in WPF device-independent units.
    /// </summary>
    public static void PlaceNearCursor(IntPtr hwnd, double widthDip, double heightDip)
    {
        GetCursorPos(out var pt);
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            SetWindowPos(hwnd, IntPtr.Zero, pt.X, pt.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }
        double scale = 1.0;
        if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        var work = info.rcWork;
        int x = Math.Max(work.Left, Math.Min(pt.X, work.Right - w));
        int y = Math.Max(work.Top, Math.Min(pt.Y, work.Bottom - h));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
