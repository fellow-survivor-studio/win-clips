using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipboardHistory.Core;

/// <summary>Small JSON settings file (currently the popup shortcut). Same format as the Python version.</summary>
public static class AppSettings
{
    /// <summary>The saved shortcut, or the default when the file is missing or invalid.</summary>
    public static Hotkey LoadHotkey(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var text = node?["hotkey"]?.GetValue<string>();
            return Hotkey.TryParse(text, out var hk) ? hk : Hotkey.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException)
        {
            return Hotkey.Default;
        }
    }

    public static void SaveHotkey(string path, Hotkey hotkey)
    {
        // Keep any other keys a newer version may have written.
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            obj = new JsonObject();
        }
        obj["hotkey"] = hotkey.ToString();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, obj.ToJsonString(options));
    }
}
