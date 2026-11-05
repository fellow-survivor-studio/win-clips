using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static ClipboardHistory.Interop.NativeMethods;

namespace ClipboardHistory.Interop;

internal static class Input
{
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>Bring <paramref name="hwnd"/> to the front and send Ctrl+V to it.</summary>
    public static async Task PasteIntoAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        // If the user is still holding Shift/Alt/Win (e.g. from the popup shortcut),
        // Ctrl+V would turn into another shortcut. Wait briefly for them to be released.
        for (int i = 0; i < 40 && ModifiersHeld(); i++)
            await Task.Delay(25);

        uint target = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        // Windows only lets a process take the foreground in some cases; attaching to
        // the target's input queue makes SetForegroundWindow reliable.
        bool attached = target != 0 && target != me && AttachThreadInput(me, target, true);
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, target, false);
        }
        await Task.Delay(60);

        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool ModifiersHeld()
    {
        foreach (var vk in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN, VK_CONTROL })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

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
