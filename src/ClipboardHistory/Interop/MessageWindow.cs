using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClipboardHistory.Core;
using static ClipboardHistory.Interop.NativeMethods;

namespace ClipboardHistory.Interop;

/// <summary>
/// Hidden message-only window on the UI thread. It receives clipboard-change
/// notifications and the global popup hotkey. RegisterHotKey must be called on the
/// thread that owns the window, which is the UI thread here, so no marshalling is needed.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const int HotkeyId = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;
    private bool _hotkeyRegistered;

    public event Action? ClipboardChanged;
    public event Action? HotkeyPressed;

    public IntPtr Handle => _source.Handle;

    /// <summary>The shortcut we hold (or want to hold while suspended).</summary>
    public Hotkey Hotkey { get; private set; }

    public bool HotkeyActive => _hotkeyRegistered;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("ClipboardHistoryMessageWindow")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(_source.Handle))
            throw new InvalidOperationException($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Register <paramref name="hotkey"/> in place of the current one. On failure the old one is restored.</summary>
    public bool TrySetHotkey(Hotkey hotkey)
    {
        bool hadOld = _hotkeyRegistered;
        Unregister();
        if (Register(hotkey))
        {
            Hotkey = hotkey;
            return true;
        }
        // Taken by another app or reserved by Windows: keep the previous one.
        if (hadOld) Register(Hotkey);
        return false;
    }

    /// <summary>Remember <paramref name="hotkey"/> without registering it (used at startup when registration fails).</summary>
    public void SetDesiredHotkey(Hotkey hotkey) => Hotkey = hotkey;

    /// <summary>Temporarily release the hotkey (e.g. while the user is picking a new one).</summary>
    public void SuspendHotkey() => Unregister();

    /// <summary>Re-register the current hotkey after <see cref="SuspendHotkey"/>.</summary>
    public bool ResumeHotkey() => _hotkeyRegistered || Register(Hotkey);

    private bool Register(Hotkey hk)
    {
        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, (uint)hk.Modifiers | MOD_NOREPEAT, (uint)hk.VirtualKey);
        return _hotkeyRegistered;
    }

    private void Unregister()
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
            handled = true;
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
