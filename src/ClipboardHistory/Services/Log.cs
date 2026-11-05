using System;
using System.IO;
using System.Text;

namespace ClipboardHistory.Services;

/// <summary>Minimal append-only log file (app.log next to the history database).</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            // Keep the log small: start over once it passes 1 MB.
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, path + ".old", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARNING", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
