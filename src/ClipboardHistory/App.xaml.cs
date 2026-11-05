using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipboardHistory.Core;
using ClipboardHistory.Interop;
using ClipboardHistory.Services;

namespace ClipboardHistory;

/// <summary>
/// Application controller: owns the store, the hidden message window (clipboard
/// listener + global hotkey), the tray icon and the popup.
/// </summary>
public partial class App : Application
{
    public const string AppName = "WinClips";
    public const int MaxItems = 200;
    private const string MutexName = @"Local\ClipboardHistory.SingleInstance"; // same as the Python version

    private Mutex? _mutex;
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private PopupWindow? _popup;
    private HotkeyWindow? _hotkeyWindow;

    internal HistoryStore Store { get; private set; } = null!;
    internal Hotkey CurrentHotkey => _messages?.Hotkey ?? Hotkey.Default;
    internal bool HotkeyActive => _messages?.HotkeyActive ?? false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(AppPaths.AppDir);
        Log.Init(AppPaths.Log);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _mutex = new Mutex(false, MutexName, out bool createdNew);
        if (!createdNew)
        {
            var hk = AppSettings.LoadHotkey(AppPaths.Settings);
            MessageBox.Show($"{AppName}이(가) 이미 실행 중입니다.\n트레이 아이콘을 클릭하거나 {hk}를 누르세요.",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        try
        {
            Store = new HistoryStore(AppPaths.Database, AppPaths.Images, MaxItems);
        }
        catch (Exception ex)
        {
            Log.Error("could not open the history database", ex);
            MessageBox.Show($"기록 데이터베이스를 열 수 없습니다.\n{AppPaths.Database}\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _messages = new MessageWindow();
        _messages.ClipboardChanged += () =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Capture));
        _messages.HotkeyPressed += TogglePopup;

        var hotkey = AppSettings.LoadHotkey(AppPaths.Settings);
        bool hotkeyOk = _messages.TrySetHotkey(hotkey);
        if (!hotkeyOk) _messages.SetDesiredHotkey(hotkey);

        _popup = new PopupWindow(this);
        _tray = new TrayIcon(this);

        if (!hotkeyOk)
        {
            Log.Warn($"could not register hotkey {hotkey}");
            _tray.ShowWarning($"단축키 {hotkey}를 등록하지 못했습니다 (다른 프로그램이 사용 중일 수 있습니다). " +
                              "트레이 메뉴의 '단축키 변경'에서 다른 조합을 고르세요.");
        }
        Log.Info($"started (hotkey {hotkey}{(hotkeyOk ? "" : ", not registered")})");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let one bad clipboard value or UI glitch kill a tray app.
        Log.Error("unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    // ------------------------------------------------------------ clipboard capture

    private void Capture()
    {
        if (_messages is null) return;
        try
        {
            var content = Win32Clipboard.Read(_messages.Handle, HistoryStore.MaxImageBytes);
            long? id = content switch
            {
                FilesContent f => Store.AddFiles(f.Paths),
                TextContent t => Store.AddText(t.Text),
                ImageContent img => Store.AddImage(img.Dib, ImageLabel(img.Dib)),
                _ => null,
            };
            if (id is not null) _popup?.OnHistoryChanged();
        }
        catch (Exception ex)
        {
            Log.Error("capture failed", ex);
        }
    }

    private static string ImageLabel(byte[] dib)
    {
        var size = ClipboardData.DibSize(dib);
        return size is { } s ? $"[Image {s.Width}x{s.Height}]" : "[Image]";
    }

    // ------------------------------------------------------------ popup

    internal void TogglePopup()
    {
        if (_popup is null) return;
        if (_popup.IsVisible)
            _popup.HidePopup();
        else
            _popup.ShowPopup();
    }

    /// <summary>Put the entry on the clipboard (the user pastes it with Ctrl+V). Returns false on failure.</summary>
    internal bool CopyToClipboard(HistoryEntry entry)
    {
        if (_messages is null) return false;
        var formats = ClipboardFormats(entry);
        if (formats is null)
        {
            Log.Warn($"entry {entry.Id} has no data to copy");
            return false;
        }
        Store.Touch(entry.Id);
        if (!Win32Clipboard.Write(_messages.Handle, formats))
        {
            Log.Warn($"could not set clipboard for entry {entry.Id}");
            return false;
        }
        return true;
    }

    private List<(uint, byte[])>? ClipboardFormats(HistoryEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.Text:
                return new() { (NativeMethods.CF_UNICODETEXT, ClipboardData.TextBytes(entry.Text)) };
            case EntryKind.Files:
                return new() { (NativeMethods.CF_HDROP, ClipboardData.DropFilesBytes(entry.Text.Split('\n'))) };
            case EntryKind.Image:
                var dib = Store.GetImageDib(entry.Id);
                return dib is null ? null : new() { (NativeMethods.CF_DIB, dib) };
            default:
                return null;
        }
    }

    internal bool ConfirmClearUnpinned(Window? owner)
    {
        const string text = "고정하지 않은 항목을 모두 삭제할까요?";
        var result = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, AppName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(text, AppName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return false;
        Store.Clear(keepPinned: true);
        _popup?.OnHistoryChanged();
        return true;
    }

    // ------------------------------------------------------------ tray actions

    internal bool AutostartEnabled => Autostart.IsEnabled();

    internal void ToggleAutostart()
    {
        bool enable = !Autostart.IsEnabled();
        try
        {
            Autostart.SetEnabled(enable);
            Log.Info($"autostart set to {enable}");
        }
        catch (Exception ex)
        {
            Log.Error("autostart change failed", ex);
            MessageBox.Show($"자동 실행 설정을 바꾸지 못했습니다.\n{ex.Message}", AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal void ShowHotkeyWindow()
    {
        if (_hotkeyWindow is not null)
        {
            _hotkeyWindow.Activate();
            return;
        }
        _popup?.HidePopup();
        // Release our own shortcut while the user picks a new one, so pressing it
        // (or the same combination again) reaches the dialog instead of the popup.
        _messages?.SuspendHotkey();
        _hotkeyWindow = new HotkeyWindow(this);
        _hotkeyWindow.Closed += (_, _) =>
        {
            _hotkeyWindow = null;
            if (_messages is not null && !_messages.ResumeHotkey())
                Log.Warn($"could not re-register hotkey {_messages.Hotkey}");
            _tray?.UpdateHotkeyText();
        };
        _hotkeyWindow.Show();
        _hotkeyWindow.Activate();
    }

    /// <summary>Switch the popup shortcut. Returns null on success, otherwise a message for the user.</summary>
    internal string? TryApplyHotkey(Hotkey hotkey)
    {
        if (_messages is null) return "아직 시작 중입니다.";
        if (!_messages.TrySetHotkey(hotkey))
            return $"{hotkey}는 다른 프로그램이 쓰고 있거나 Windows가 예약한 키입니다. 다른 조합을 골라 주세요.";
        try
        {
            AppSettings.SaveHotkey(AppPaths.Settings, hotkey);
        }
        catch (Exception ex)
        {
            Log.Error("could not save settings", ex);
        }
        Log.Info($"popup hotkey changed to {hotkey}");
        _tray?.UpdateHotkeyText();
        _popup?.UpdateFooter();
        return null;
    }

    internal void Quit()
    {
        _hotkeyWindow?.Close();
        _popup?.CloseForExit();
        _tray?.Dispose();
        _tray = null;
        _messages?.Dispose();
        _messages = null;
        Store?.Dispose();
        Log.Info("stopped");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _messages?.Dispose();
        if (_mutex is not null)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        base.OnExit(e);
    }
}
