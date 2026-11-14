using System.Globalization;

namespace Jarvis.Core.Shell;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

/// <summary>A parsed global shortcut such as "Ctrl+Alt+J". Values match the Win32 RegisterHotKey contract.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string Display)
{
    private static readonly Dictionary<string, (uint Vk, string Name)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = (0x20, "Space"), ["spacebar"] = (0x20, "Space"), ["tab"] = (0x09, "Tab"),
        ["enter"] = (0x0D, "Enter"), ["return"] = (0x0D, "Enter"), ["esc"] = (0x1B, "Esc"), ["escape"] = (0x1B, "Esc"),
        ["backspace"] = (0x08, "Backspace"), ["insert"] = (0x2D, "Insert"), ["ins"] = (0x2D, "Insert"),
        ["delete"] = (0x2E, "Delete"), ["del"] = (0x2E, "Delete"), ["home"] = (0x24, "Home"), ["end"] = (0x23, "End"),
        ["pageup"] = (0x21, "PageUp"), ["pgup"] = (0x21, "PageUp"), ["pagedown"] = (0x22, "PageDown"), ["pgdn"] = (0x22, "PageDown"),
        ["left"] = (0x25, "Left"), ["up"] = (0x26, "Up"), ["right"] = (0x27, "Right"), ["down"] = (0x28, "Down"),
        ["pause"] = (0x13, "Pause"), ["`"] = (0xC0, "`"), ["tilde"] = (0xC0, "`")
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = new HotkeyGesture(HotkeyModifiers.None, 0, "");
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "The shortcut is empty."; return false; }

        var modifiers = HotkeyModifiers.None;
        uint vk = 0;
        var keyName = "";
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win": case "windows": case "super": modifiers |= HotkeyModifiers.Win; continue;
            }

            if (vk != 0) { error = "A shortcut can contain only one non-modifier key."; return false; }

            if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0]))
            {
                var c = char.ToUpperInvariant(raw[0]);
                vk = c;
                keyName = c.ToString();
            }
            else if (raw.Length is 2 or 3 && (raw[0] == 'F' || raw[0] == 'f')
                     && int.TryParse(raw.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var fn) && fn is >= 1 and <= 24)
            {
                vk = (uint)(0x70 + fn - 1);
                keyName = "F" + fn.ToString(CultureInfo.InvariantCulture);
            }
            else if (NamedKeys.TryGetValue(raw, out var named))
            {
                vk = named.Vk;
                keyName = named.Name;
            }
            else
            {
                error = $"'{raw}' is not a recognised key.";
                return false;
            }
        }

        if (vk == 0) { error = "The shortcut needs a key in addition to modifiers."; return false; }
        var isFunctionKey = vk is >= 0x70 and <= 0x87;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            error = "Add at least one modifier (Ctrl, Alt, Shift or Win) so the shortcut does not capture normal typing.";
            return false;
        }
        if (modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && vk == 0x2E)
        {
            error = "Ctrl+Alt+Delete is reserved by Windows.";
            return false;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(keyName);
        gesture = new HotkeyGesture(modifiers, vk, string.Join("+", parts));
        return true;
    }
}
