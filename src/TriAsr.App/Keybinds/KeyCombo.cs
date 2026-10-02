using System.Runtime.InteropServices;

namespace TriAsr.App;

/// <summary>Why a combination of keys cannot be used to start something from any program.</summary>
public enum KeyProblem
{
    None,
    /// <summary>Only modifier keys were pressed, or nothing at all.</summary>
    NoKey,
    /// <summary>A letter, digit or other key on its own (or with Shift only) would stop that key from working in every program.</summary>
    NeedsModifier,
    /// <summary>The Windows key: Windows keeps it for its own shortcuts and opens the Start menu when it is let go of, so it cannot be chosen by pressing it.</summary>
    WindowsKey,
    /// <summary>Copy, paste, close and the like: nearly every program uses them.</summary>
    CommonShortcut
}

/// <summary>
/// A combination of keys that works in every program (Ctrl+Alt+Space, F9...), as <c>RegisterHotKey</c> wants it: the modifier flags and a virtual-key code. The text form
/// (<see cref="Id"/>) is what is saved; it is the same as what is shown (<see cref="Label"/>) except for the keys of punctuation, which the label shows as the
/// keyboard of the person prints them.
/// </summary>
/// <param name="Modifiers">The Windows modifier flags: <see cref="Alt"/>, <see cref="Control"/>, <see cref="Shift"/>, <see cref="Windows"/>.</param>
/// <param name="Key">The virtual-key code; 0 means no keys at all.</param>
public readonly record struct KeyCombo(uint Modifiers, uint Key)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Windows = 8;

    /// <summary>The virtual-key codes of the modifier keys themselves, which are never the main key of a combination.</summary>
    private static readonly HashSet<uint> ModifierKeys = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    private static readonly (uint Key, string Name)[] Named = BuildNames();

    private static (uint, string)[] BuildNames()
    {
        var names = new List<(uint, string)>
        {
            (0x20, "Space"), (0x0D, "Enter"), (0x09, "Tab"), (0x1B, "Esc"), (0x08, "Backspace"), (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"),
            (0x21, "PageUp"), (0x22, "PageDown"), (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x2C, "PrintScreen"), (0x13, "Pause"),
            (0x6A, "Num*"), (0x6B, "Num+"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/")
        };
        for (var i = 0; i < 26; i++) names.Add(((uint)('A' + i), ((char)('A' + i)).ToString()));
        for (var i = 0; i < 10; i++) { names.Add(((uint)('0' + i), i.ToString())); names.Add(((uint)(0x60 + i), "Num" + i)); }
        for (var i = 1; i <= 24; i++) names.Add(((uint)(0x6F + i), "F" + i));
        return [.. names];
    }

    /// <summary>The punctuation keys. Their codes mean different characters on different keyboards, so they are saved by code and shown as the keyboard prints them.</summary>
    private static bool IsPunctuation(uint key) => key is >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF;

    public static KeyCombo None => default;

    public bool IsNone => Key == 0;

    /// <summary>Whether a key code can be the main key of a combination.</summary>
    public static bool CanBeKey(uint key) => key != 0 && !ModifierKeys.Contains(key) && (IsPunctuation(key) || Named.Any(item => item.Key == key));

    /// <summary>Whether a key is only a modifier (Ctrl, Alt, Shift, the Windows key).</summary>
    public static bool IsModifierKey(uint key) => ModifierKeys.Contains(key);

    /// <summary>The modifier keys only: "Ctrl+Alt+", for showing what is held while the person has not pressed the main key yet.</summary>
    public static string ModifierText(uint modifiers) => string.Concat(
        (modifiers & Control) != 0 ? "Ctrl+" : "", (modifiers & Alt) != 0 ? "Alt+" : "", (modifiers & Shift) != 0 ? "Shift+" : "", (modifiers & Windows) != 0 ? "Win+" : "");

    /// <summary>The keys as they are saved: "Ctrl+Alt+Space", or "" for no keys.</summary>
    public string Id => IsNone ? "" : ModifierText(Modifiers) + (IsPunctuation(Key) ? "Oem" + Key.ToString("X2") : NameOf(Key));

    /// <summary>The keys as they are shown: like <see cref="Id"/>, but a punctuation key is shown as the character the keyboard prints on it.</summary>
    public string Label => IsNone ? "" : ModifierText(Modifiers) + (IsPunctuation(Key) ? PrintedOn(Key) : NameOf(Key));

    public override string ToString() => Label;

    private static string NameOf(uint key) => Named.FirstOrDefault(item => item.Key == key).Name ?? "Key" + key.ToString("X2");

    private static string PrintedOn(uint key)
    {
        var character = MapVirtualKey(key, 2) & 0x7FFFFFFF;                       // MAPVK_VK_TO_CHAR; the top bit marks a dead key
        return character > ' ' ? ((char)character).ToString() : "Oem" + key.ToString("X2");
    }

    public KeyProblem Problem
    {
        get
        {
            if (!CanBeKey(Key)) return KeyProblem.NoKey;
            if ((Modifiers & Windows) != 0) return KeyProblem.WindowsKey;
            var functionKey = Key is >= 0x70 and <= 0x87;
            var strong = (Modifiers & (Control | Alt | Windows)) != 0;
            if (!strong && !functionKey) return KeyProblem.NeedsModifier;
            if (Modifiers == Control && Key is 'A' or 'C' or 'F' or 'N' or 'O' or 'P' or 'S' or 'V' or 'W' or 'X' or 'Y' or 'Z') return KeyProblem.CommonShortcut;
            if (Modifiers == Alt && Key is 0x73 or 0x09 or 0x20 or 0x1B) return KeyProblem.CommonShortcut;        // Alt+F4, Alt+Tab, Alt+Space, Alt+Esc
            if (Modifiers == Control && Key is 0x1B or 0x09) return KeyProblem.CommonShortcut;                    // Ctrl+Esc, Ctrl+Tab
            return KeyProblem.None;
        }
    }

    private static readonly Dictionary<string, KeyCombo> LegacyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl-alt-space"] = new(Control | Alt, 0x20), ["ctrl-shift-space"] = new(Control | Shift, 0x20), ["ctrl-alt-d"] = new(Control | Alt, 'D'),
        ["f9"] = new(0, 0x78), ["f8"] = new(0, 0x77)
    };

    /// <summary>Reads the saved text. "" is no keys; text that is not a combination is refused.</summary>
    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (text is null) return false;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (LegacyIds.TryGetValue(text, out combo)) return true;
        uint modifiers = 0, key = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // A key named "+" does not exist in the list, so a lone "+" cannot be a part; the numpad's plus is spelled Num+, which splits into "Num" and "": put it together again.
        var merged = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 && merged.Count > 0 && (merged[^1] is "Num" or "Num+")) { merged[^1] += "+"; continue; }
            merged.Add(parts[i]);
        }
        foreach (var part in merged)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Control; continue;
                case "alt": modifiers |= Alt; continue;
                case "shift": modifiers |= Shift; continue;
                case "win" or "windows": modifiers |= Windows; continue;
            }
            if (key != 0) return false;
            if (part.StartsWith("Oem", StringComparison.OrdinalIgnoreCase) && uint.TryParse(part[3..], System.Globalization.NumberStyles.HexNumber, null, out var oem) && IsPunctuation(oem)) key = oem;
            else if (Named.FirstOrDefault(item => item.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) is { Name: not null } found) key = found.Key;
            else return false;
        }
        if (key == 0) return false;
        combo = new KeyCombo(modifiers, key);
        return true;
    }

    /// <summary>The saved text as it should be saved again: a combination that can be used, or none ("") if that is what was saved, or <paramref name="fallback"/> for anything else (the old ids become the new text).</summary>
    public static string Normalize(string? text, string fallback) => TryParse(text, out var combo) && (combo.IsNone || combo.Problem == KeyProblem.None) ? combo.Id : fallback;
    /// <summary>The saved text read back, or <paramref name="fallback"/> when it is not a combination.</summary>
    public static KeyCombo ParseOr(string? text, KeyCombo fallback) => TryParse(text, out var combo) ? combo : fallback;

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
}
