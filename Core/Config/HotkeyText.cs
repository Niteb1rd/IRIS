namespace Iris.Core.Config;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
}

public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey);

public static class HotkeyText
{
    private static readonly Dictionary<string, int> KeyByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> NameByKey = new();

    static HotkeyText()
    {
        for (var i = 0; i < 26; i++)
            Add(((char)('A' + i)).ToString(), 0x41 + i);
        for (var i = 0; i < 10; i++)
        {
            Add(((char)('0' + i)).ToString(), 0x30 + i);
            Add("Num" + i, 0x60 + i);
        }
        for (var i = 1; i <= 24; i++)
            Add("F" + i, 0x6F + i);

        Add("Space", 0x20);
        Add("Tab", 0x09);
        Add("Enter", 0x0D);
        Add("Backspace", 0x08);
        Add("Insert", 0x2D);
        Add("Delete", 0x2E);
        Add("Home", 0x24);
        Add("End", 0x23);
        Add("PageUp", 0x21);
        Add("PageDown", 0x22);
        Add("Up", 0x26);
        Add("Down", 0x28);
        Add("Left", 0x25);
        Add("Right", 0x27);

        KeyByName["Return"] = 0x0D;
        KeyByName["Del"] = 0x2E;
        KeyByName["Ins"] = 0x2D;
        KeyByName["PgUp"] = 0x21;
        KeyByName["PgDn"] = 0x22;
    }

    private static void Add(string name, int virtualKey)
    {
        KeyByName[name] = virtualKey;
        NameByKey[virtualKey] = name;
    }

    public const string WindowsKeyError = "The Windows key cannot be used in a hotkey; use Ctrl, Alt or Shift.";

    private static bool IsWindowsKey(string token) =>
        token.Equals("win", StringComparison.OrdinalIgnoreCase) || token.Equals("windows", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedKey(int virtualKey) => NameByKey.ContainsKey(virtualKey);

    public static bool TryCreate(HotkeyModifiers modifiers, int virtualKey, out Hotkey? hotkey) =>
        TryCreate(modifiers, virtualKey, out hotkey, out _);

    public static bool TryCreate(HotkeyModifiers modifiers, int virtualKey, out Hotkey? hotkey, out string error)
    {
        hotkey = null;
        error = string.Empty;
        if (!IsSupportedKey(virtualKey))
        {
            error = "That key cannot be used";
            return false;
        }
        hotkey = new Hotkey(modifiers, virtualKey);
        return true;
    }

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(5);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        parts.Add(NameByKey.TryGetValue(hotkey.VirtualKey, out var name) ? name : "?");
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey? hotkey, out string error)
    {
        hotkey = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The hotkey is empty.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                error = $"\"{text}\" is not a hotkey like Ctrl+Shift+O.";
                return false;
            }

            if (IsWindowsKey(token))
            {
                error = WindowsKeyError;
                return false;
            }

            var modifier = ModifierOf(token);
            if (modifier != HotkeyModifiers.None)
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"\"{text}\" names {token} twice.";
                    return false;
                }
                modifiers |= modifier;
            }
            else if (key is null && KeyByName.TryGetValue(token, out var virtualKey))
            {
                key = virtualKey;
            }
            else if (key is not null)
            {
                error = $"\"{text}\" has more than one key.";
                return false;
            }
            else
            {
                error = $"\"{token}\" is not a key IRIS can use in a hotkey.";
                return false;
            }
        }

        if (key is null)
        {
            error = $"\"{text}\" has no key, only Ctrl/Alt/Shift.";
            return false;
        }
        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    public static string Normalize(string? text) =>
        TryParse(text, out var hotkey, out _) ? Format(hotkey!) : string.Empty;

    private static HotkeyModifiers ModifierOf(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Control,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        _ => HotkeyModifiers.None,
    };
}
