using System;
using Microsoft.Win32;

namespace ClipboardHistory.Services;

/// <summary>"Start with Windows" via the per-user Run registry key (no admin rights needed).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinClips";

    public static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True only when the Run entry starts this exe (an entry left by an older copy or the Python version does not count).</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value
               && string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
