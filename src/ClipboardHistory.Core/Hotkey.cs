namespace ClipboardHistory.Core;

/// <summary>Modifier bits as used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>A global shortcut such as Ctrl+Shift+V (modifiers + Windows virtual-key code).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const string DefaultText = "Ctrl+Shift+V";

    public static Hotkey Default => Parse(DefaultText);

    private static readonly Dictionary<string, HotkeyModifiers> ModByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Control,
        ["control"] = HotkeyModifiers.Control,
        ["alt"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["win"] = HotkeyModifiers.Win,
        ["windows"] = HotkeyModifiers.Win,
    };

    private static readonly (HotkeyModifiers Bit, string Label)[] ModLabels =
    {
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Win, "Win"),
    };

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
    };

    private static readonly Dictionary<int, string> KeyLabels = new()
    {
        [0x20] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
    };

    /// <summary>True for keys allowed as the non-modifier part of a shortcut.</summary>
    public static bool IsSupportedKey(int vk) =>
        vk is >= 0x30 and <= 0x39      // 0-9
           or >= 0x41 and <= 0x5A      // A-Z
           or >= 0x70 and <= 0x7B      // F1-F12
        || KeyLabels.ContainsKey(vk);

    /// <summary>Parse text such as "Ctrl+Shift+V". Throws FormatException with a Korean message.</summary>
    public static Hotkey Parse(string? text)
    {
        var parts = (text ?? "").Split('+').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2 || parts.Any(p => p.Length == 0))
            throw new FormatException("형식은 'Ctrl+Shift+V' 처럼 수식키+키 입니다.");
        var mods = HotkeyModifiers.None;
        foreach (var name in parts[..^1])
        {
            if (!ModByName.TryGetValue(name, out var bit))
                throw new FormatException($"알 수 없는 수식키: '{name}'");
            mods |= bit;
        }
        return new Hotkey(mods, ParseKey(parts[^1]));
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        try
        {
            hotkey = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            hotkey = default;
            return false;
        }
    }

    private static int ParseKey(string token)
    {
        if (token.Length == 1 && char.IsAscii(token[0]) && char.IsLetterOrDigit(token[0]))
            return char.ToUpperInvariant(token[0]);
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token[1..], out var n) && n is >= 1 and <= 12 && token[1..].All(char.IsAsciiDigit))
            return 0x70 + n - 1;
        if (NamedKeys.TryGetValue(token, out var vk))
            return vk;
        throw new FormatException($"알 수 없는 키: '{token}'");
    }

    /// <summary>Only the modifiers, ending in '+', e.g. "Ctrl+Shift+". Empty when none.</summary>
    public static string FormatModifiers(HotkeyModifiers mods)
    {
        var names = ModLabels.Where(m => mods.HasFlag(m.Bit)).Select(m => m.Label).ToList();
        return names.Count == 0 ? "" : string.Join("+", names) + "+";
    }

    public static string KeyLabel(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)vk).ToString()
        : vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x6F}"
        : KeyLabels.TryGetValue(vk, out var label) ? label
        : $"VK0x{vk:x}";

    /// <summary>Canonical text form: modifiers in the order Ctrl, Alt, Shift, Win.</summary>
    public override string ToString() => FormatModifiers(Modifiers) + KeyLabel(VirtualKey);
}
