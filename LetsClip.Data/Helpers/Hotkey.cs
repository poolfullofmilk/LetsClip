using System.Diagnostics;
using LetsClip.Data.Enums;

namespace LetsClip.Data.Helpers;

public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey Default = new(
        HotkeyModifiers.Control | HotkeyModifiers.Shift,
        0x77
    );

    // Virtual Key Codes By The Name Shown For Them
    private static readonly Dictionary<string, int> s_keys = BuildKeys();

    // Keys That Never Type Text, Safe Without Modifiers
    private static readonly HashSet<int> s_quietKeys = [.. Enumerable.Range(0x70, 24), 0x13, 0x91];

    static Hotkey()
    {
        Debug.Assert(SelfTestPasses());
    }

    public bool IsUsable =>
        s_keys.ContainsValue(VirtualKey)
        && (
            s_quietKeys.Contains(VirtualKey)
            || (
                Modifiers
                & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)
            ) != 0
        );

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        var key = VirtualKey;
        parts.Add(s_keys.FirstOrDefault(entry => entry.Value == key).Key ?? $"Key {key}");
        return string.Join("+", parts);
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var modifiers = HotkeyModifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => (HotkeyModifiers)(-1),
            };
        }

        if (modifiers < 0 || !s_keys.TryGetValue(parts[^1], out var virtualKey))
        {
            return null;
        }

        var hotkey = new Hotkey(modifiers, virtualKey);
        return hotkey.IsUsable ? hotkey : null;
    }

    public static Hotkey? FromBrowser(string code, bool control, bool alt, bool shift, bool meta)
    {
        // KeyboardEvent.code Names The Physical Key, Whatever The Layout
        var name = code switch
        {
            _ when code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4 => code[
                3..
            ],
            _ when code.StartsWith("Digit", StringComparison.Ordinal) => code[5..],
            _ when code.StartsWith("Numpad", StringComparison.Ordinal) => "Num" + code[6..],
            _ when code.StartsWith("Arrow", StringComparison.Ordinal) => code[5..],
            _ => code,
        };
        if (!s_keys.TryGetValue(name, out var virtualKey))
        {
            return null;
        }

        var modifiers =
            (control ? HotkeyModifiers.Control : HotkeyModifiers.None)
            | (alt ? HotkeyModifiers.Alt : HotkeyModifiers.None)
            | (shift ? HotkeyModifiers.Shift : HotkeyModifiers.None)
            | (meta ? HotkeyModifiers.Windows : HotkeyModifiers.None);
        return new Hotkey(modifiers, virtualKey);
    }

    private static Dictionary<string, int> BuildKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Pause"] = 0x13,
            ["ScrollLock"] = 0x91,
            ["Space"] = 0x20,
            ["Up"] = 0x26,
            ["Down"] = 0x28,
            ["Left"] = 0x25,
            ["Right"] = 0x27,
        };
        for (var index = 0; index < 24; index++)
        {
            keys[$"F{index + 1}"] = 0x70 + index;
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            keys[letter.ToString()] = letter;
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            keys[digit.ToString()] = 0x30 + digit;
            keys[$"Num{digit}"] = 0x60 + digit;
        }

        return keys;
    }

    private static bool SelfTestPasses()
    {
        return Default.ToString() == "Ctrl+Shift+F8"
            && Parse("Ctrl+Shift+F8") == Default
            && Parse("ctrl + shift + f8") == Default
            && Parse("F9") == new Hotkey(HotkeyModifiers.None, 0x78)
            && Parse("Shift+A") is null
            && Parse("A") is null
            && Parse("Ctrl+Banana") is null
            && Parse("Hyper+F8") is null
            && Parse("Alt+1")?.ToString() == "Alt+1"
            && FromBrowser("KeyS", true, true, false, false)?.ToString() == "Ctrl+Alt+S"
            && FromBrowser("Numpad5", true, false, false, false)?.ToString() == "Ctrl+Num5"
            && FromBrowser("ShiftLeft", false, false, true, false) is null;
    }
}
