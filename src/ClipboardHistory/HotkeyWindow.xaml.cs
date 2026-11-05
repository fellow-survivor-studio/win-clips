using System.Windows;
using System.Windows.Input;
using ClipboardHistory.Core;

namespace ClipboardHistory;

/// <summary>Lets the user press the shortcut they want instead of typing its name.</summary>
public partial class HotkeyWindow : Window
{
    private readonly App _app;
    private Hotkey? _captured;

    internal HotkeyWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Theme.Apply(Resources);
        Display.Text = _app.CurrentHotkey.ToString();
        Current.Text = $"현재 단축키: {_app.CurrentHotkey}{(_app.HotkeyActive ? "" : " (등록되지 않음)")}";
        DefaultButton.Content = $"기본값 ({Hotkey.DefaultText})";
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static HotkeyModifiers HeldModifiers()
    {
        var m = Keyboard.Modifiers;
        var mods = HotkeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // nothing in this window should react to keys on its own
        var key = e.Key switch
        {
            Key.System => e.SystemKey,          // keys pressed with Alt
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var mods = HeldModifiers();

        if (key == Key.Escape && mods == HotkeyModifiers.None)
        {
            Close();
            return;
        }
        if (key == Key.Enter && mods == HotkeyModifiers.None)
        {
            Save();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Display.Text = Hotkey.FormatModifiers(mods) + "…";
            Status.Text = "";
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (!Hotkey.IsSupportedKey(vk))
        {
            Status.Text = "이 키는 쓸 수 없습니다. 영문자, 숫자, F1~F12, 방향키 등을 눌러 주세요.";
            return;
        }
        if (mods == HotkeyModifiers.None)
        {
            Status.Text = "Ctrl, Alt, Shift 중 하나와 함께 눌러 주세요.";
            return;
        }
        _captured = new Hotkey(mods, vk);
        Display.Text = _captured.Value.ToString();
        Status.Text = "";
    }

    private void Save()
    {
        if (_captured is not { } hk)
        {
            Status.Text = "먼저 원하는 키 조합을 눌러 주세요.";
            return;
        }
        var error = _app.TryApplyHotkey(hk);
        if (error is null)
            Close();
        else
            Status.Text = error;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        _captured = Hotkey.Default;
        Display.Text = _captured.Value.ToString();
        Status.Text = "";
    }
}
