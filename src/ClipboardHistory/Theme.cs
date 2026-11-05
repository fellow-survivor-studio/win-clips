using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipboardHistory;

/// <summary>Light/dark colours that follow the Windows "app mode" setting.</summary>
internal static class Theme
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(ResourceDictionary r)
    {
        bool dark = IsDark();
        Set(r, "Bg", dark ? "#202020" : "#F9F9F9");
        Set(r, "Fg", dark ? "#F0F0F0" : "#1B1B1B");
        Set(r, "SubtleFg", dark ? "#9A9A9A" : "#6B6B6B");
        Set(r, "Line", dark ? "#3A3A3A" : "#E3E3E3");
        Set(r, "Outline", dark ? "#505050" : "#C8C8C8");
        Set(r, "FieldBg", dark ? "#2C2C2C" : "#FFFFFF");
        Set(r, "HoverBg", dark ? "#2D2D2D" : "#EDEDED");
        Set(r, "SelBg", dark ? "#1F4A85" : "#D6E6FB");
        Set(r, "PreviewBg", dark ? "#262626" : "#F1F1F1");
        Set(r, "Accent", dark ? "#6CA8F0" : "#2D6CDF");
        Set(r, "Error", dark ? "#FF8A80" : "#B00020");
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        r[key] = brush;
    }
}
