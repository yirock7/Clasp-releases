namespace AppleDeviceHub.Core.Commands;

/// <summary>Modifier keys. Values match the Win32 MOD_* flags so the app can pass them straight through.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Result of <see cref="HotkeyBinding.Validate"/>.</summary>
public sealed record HotkeyValidation(bool IsValid, string? Reason)
{
    public static readonly HotkeyValidation Valid = new(true, null);
    public static HotkeyValidation Invalid(string reason) => new(false, reason);
}

/// <summary>
/// A global hotkey: modifiers plus one Windows virtual-key code, written like "Ctrl+Alt+A".
/// An empty setting means unassigned (no binding). Parsing is case-insensitive; formatting is
/// canonical (Ctrl, Alt, Shift, Win order) so a saved value always compares equal.
/// </summary>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Escape"] = 0x1B,
        ["MediaPlayPause"] = 0xB3, ["MediaNext"] = 0xB0, ["MediaPrevious"] = 0xB1, ["MediaStop"] = 0xB2,
    };

    private static readonly Dictionary<uint, string> NamesByKey = BuildNamesByKey();

    /// <summary>Parses "Ctrl+Alt+A"; null with a reason when the text is not a recognisable hotkey.</summary>
    public static bool TryParse(string? text, out HotkeyBinding? binding, out string? reason)
    {
        binding = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(text)) { reason = "Type a shortcut."; return false; }
        if (text.Length > 64) { reason = "That shortcut is too long."; return false; }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        uint? key = null;
        foreach (var part in parts)
        {
            if (part.Length == 0) { reason = "That shortcut isn't valid."; return false; }
            var modifier = ModifierFor(part);
            if (modifier != HotkeyModifiers.None)
            {
                if (key is not null) { reason = "The key has to come last."; return false; }
                if ((modifiers & modifier) != 0) { reason = $"{part} is used twice."; return false; }
                modifiers |= modifier;
                continue;
            }
            if (key is not null) { reason = "A shortcut can use only one key."; return false; }
            key = KeyFor(part);
            if (key is null) { reason = $"Clasp doesn't recognize '{part}'."; return false; }
        }
        if (key is null) { reason = "Add a key to the shortcut."; return false; }
        binding = new HotkeyBinding(modifiers, key.Value);
        return true;
    }

    /// <summary>Parses and validates; null for an empty or unusable value.</summary>
    public static HotkeyBinding? ParseOrNull(string? text) =>
        TryParse(text, out var binding, out _) && binding!.Validate().IsValid ? binding : null;

    /// <summary>Canonical text, for example "Ctrl+Alt+A".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>
    /// Rules: at least one of Ctrl, Alt or Win (Shift alone would hijack normal typing), Win never
    /// as the only modifier (Windows reserves most of those), and a short list of combinations
    /// Windows owns (Ctrl+Alt+Delete, Alt+F4, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Space).
    /// Passing validation does not guarantee Windows will accept it: another app may already own it.
    /// </summary>
    public HotkeyValidation Validate()
    {
        if (KeyName(VirtualKey).StartsWith("0x", StringComparison.Ordinal)) return HotkeyValidation.Invalid("That key isn't supported.");
        if (Modifiers == HotkeyModifiers.None) return HotkeyValidation.Invalid("Include Ctrl, Alt, or Win.");
        if ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
            return HotkeyValidation.Invalid("Shift alone gets in the way of typing. Add Ctrl or Alt.");
        if (Modifiers == HotkeyModifiers.Win) return HotkeyValidation.Invalid("Windows keeps Win shortcuts for itself. Add Ctrl or Alt.");

        const uint Delete = 0x2E, F4 = 0x73, Tab = 0x09, Escape = 0x1B, Space = 0x20;
        if (Modifiers == (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt) && VirtualKey == Delete) return Reserved();
        if (Modifiers == HotkeyModifiers.Alt && VirtualKey is F4 or Tab or Escape or Space) return Reserved();
        if (Modifiers == HotkeyModifiers.Ctrl && VirtualKey == Escape) return Reserved();
        if (Modifiers == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift) && VirtualKey == Escape) return Reserved();
        return HotkeyValidation.Valid;

        static HotkeyValidation Reserved() => HotkeyValidation.Invalid("Windows already uses this shortcut. Pick another.");
    }

    private static HotkeyModifiers ModifierFor(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "super" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static uint? KeyFor(string part)
    {
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return null;
        }
        if (NamedKeys.TryGetValue(part, out var named)) return named;
        if ((part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);
        if (part.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && part.Length == 4 && char.IsAsciiDigit(part[3]))
            return (uint)(0x60 + (part[3] - '0'));
        return null;
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        return NamesByKey.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";
    }

    private static Dictionary<uint, string> BuildNamesByKey()
    {
        var map = new Dictionary<uint, string>();
        foreach (var (name, vk) in NamedKeys) map[vk] = name;
        return map;
    }
}
