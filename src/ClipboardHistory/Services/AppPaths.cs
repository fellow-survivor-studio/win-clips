using System;
using System.IO;

namespace ClipboardHistory.Services;

/// <summary>Data locations, shared with the earlier Python version.</summary>
internal static class AppPaths
{
    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHistory");

    public static readonly string Database = Path.Combine(AppDir, "history.db");
    public static readonly string Images = Path.Combine(AppDir, "images");
    public static readonly string Settings = Path.Combine(AppDir, "settings.json");
    public static readonly string Log = Path.Combine(AppDir, "app.log");
}
