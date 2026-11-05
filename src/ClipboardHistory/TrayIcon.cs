using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ClipboardHistory;

/// <summary>System tray icon: left click opens the popup, right click shows the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _autostartItem;

    public TrayIcon(App app)
    {
        _app = app;

        var menu = new Forms.ContextMenuStrip();
        _showItem = new Forms.ToolStripMenuItem("기록 열기", null, (_, _) => _app.TogglePopup(fromHotkey: false));
        _showItem.Font = new Font(_showItem.Font, System.Drawing.FontStyle.Bold);
        var shortcutItem = new Forms.ToolStripMenuItem("단축키 변경...", null, (_, _) => _app.ShowHotkeyWindow());
        _autostartItem = new Forms.ToolStripMenuItem("Windows 시작 시 자동 실행", null, (_, _) => _app.ToggleAutostart());
        var clearItem = new Forms.ToolStripMenuItem("고정하지 않은 기록 지우기", null, (_, _) => _app.ConfirmClearUnpinned(null));
        var openFolderItem = new Forms.ToolStripMenuItem("데이터 폴더 열기", null, (_, _) => OpenDataFolder());
        var quitItem = new Forms.ToolStripMenuItem("종료", null, (_, _) => _app.Quit());
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _showItem, shortcutItem, _autostartItem, new Forms.ToolStripSeparator(),
            clearItem, openFolderItem, new Forms.ToolStripSeparator(), quitItem,
        });
        menu.Opening += (_, _) =>
        {
            _autostartItem.Checked = _app.AutostartEnabled;
            UpdateHotkeyText();
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.TogglePopup(fromHotkey: false);
        };
        UpdateHotkeyText();
    }

    private static Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void UpdateHotkeyText()
    {
        var hk = _app.CurrentHotkey.ToString();
        _showItem.ShortcutKeyDisplayString = _app.HotkeyActive ? hk : $"{hk} (사용 안 됨)";
        _icon.Text = $"{App.AppName} ({hk})";
    }

    public void ShowWarning(string message) =>
        _icon.ShowBalloonTip(8000, App.AppName, message, Forms.ToolTipIcon.Warning);

    private static void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Services.AppPaths.AppDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Services.Log.Error("could not open data folder", ex);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
