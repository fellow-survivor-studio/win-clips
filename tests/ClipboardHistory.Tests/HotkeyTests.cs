using ClipboardHistory.Core;

namespace ClipboardHistory.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56)]
    [InlineData("ctrl + alt + f12", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7B)]
    [InlineData("Control+Windows+1", HotkeyModifiers.Control | HotkeyModifiers.Win, 0x31)]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20)]
    [InlineData("Shift+PageDown", HotkeyModifiers.Shift, 0x22)]
    [InlineData("Ctrl+Del", HotkeyModifiers.Control, 0x2E)]
    public void Parses(string text, HotkeyModifiers mods, int vk)
    {
        var hk = Hotkey.Parse(text);
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(vk, hk.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("V")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++V")]
    [InlineData("Hyper+V")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+F1x")]
    [InlineData("Ctrl+é")]
    [InlineData("Ctrl+Backspace")]
    public void Rejects(string? text)
    {
        Assert.Throws<FormatException>(() => Hotkey.Parse(text));
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Theory]
    [InlineData("Shift+Win+Delete", "Shift+Win+Delete")]
    [InlineData("shift+ctrl+v", "Ctrl+Shift+V")]
    [InlineData("Win+Alt+Ctrl+F5", "Ctrl+Alt+Win+F5")]
    [InlineData("Alt+Return", "Alt+Enter")]
    public void Formats_canonically(string input, string expected)
    {
        Assert.Equal(expected, Hotkey.Parse(input).ToString());
        Assert.Equal(Hotkey.Parse(input), Hotkey.Parse(expected));
    }

    [Fact]
    public void Default_is_ctrl_shift_v()
    {
        Assert.Equal("Ctrl+Shift+V", Hotkey.Default.ToString());
    }

    [Fact]
    public void Modifier_only_text()
    {
        Assert.Equal("Ctrl+Alt+", Hotkey.FormatModifiers(HotkeyModifiers.Alt | HotkeyModifiers.Control));
        Assert.Equal("", Hotkey.FormatModifiers(HotkeyModifiers.None));
    }

    [Theory]
    [InlineData(0x41, true)]  // A
    [InlineData(0x39, true)]  // 9
    [InlineData(0x7B, true)]  // F12
    [InlineData(0x7C, false)] // F13
    [InlineData(0x26, true)]  // Up
    [InlineData(0x08, false)] // Backspace
    [InlineData(0x1B, false)] // Escape
    [InlineData(0x10, false)] // Shift
    public void Supported_keys(int vk, bool supported)
    {
        Assert.Equal(supported, Hotkey.IsSupportedKey(vk));
    }

    [Fact]
    public void Settings_round_trip_and_fallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cbh-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            Assert.Equal(Hotkey.Default, AppSettings.LoadHotkey(path)); // missing file

            AppSettings.SaveHotkey(path, Hotkey.Parse("Ctrl+Alt+F8"));
            Assert.Equal("Ctrl+Alt+F8", AppSettings.LoadHotkey(path).ToString());
            Assert.Contains("\"hotkey\": \"Ctrl+Alt+F8\"", File.ReadAllText(path));

            File.WriteAllText(path, "{ not json");
            Assert.Equal(Hotkey.Default, AppSettings.LoadHotkey(path));

            File.WriteAllText(path, "{\"hotkey\": 5}");
            Assert.Equal(Hotkey.Default, AppSettings.LoadHotkey(path));

            File.WriteAllText(path, "{\"hotkey\": \"Nope+V\", \"other\": 1}");
            Assert.Equal(Hotkey.Default, AppSettings.LoadHotkey(path));
            AppSettings.SaveHotkey(path, Hotkey.Default);
            Assert.Contains("\"other\": 1", File.ReadAllText(path)); // unknown keys are kept

            // The Python version wrote the same format.
            File.WriteAllText(path, "{\n  \"hotkey\": \"Ctrl+Shift+F9\"\n}");
            Assert.Equal("Ctrl+Shift+F9", AppSettings.LoadHotkey(path).ToString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
