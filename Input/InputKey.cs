using System;

namespace EQAvatar.Spike.Input;

/// <summary>Which mouse button an <see cref="InputKey"/> refers to.</summary>
public enum MouseBtn { Left, Right, Middle, X1, X2 }

/// <summary>
/// A bindable input that is EITHER a keyboard key or a mouse button, so a role can bind
/// "target = Tab" and "consider = mouse5" the same way. Parses friendly strings:
/// letters/digits, F1–F24, Tab/Space/Enter/Esc/Shift/Ctrl/Alt, arrows, and
/// mouse1..mouse5 (mouse4 = XButton1, mouse5 = XButton2).
/// </summary>
public readonly struct InputKey
{
    public enum InputKind { None, Key, Mouse }

    public InputKind Kind { get; }
    public ushort Vk { get; }          // valid when Kind == Key
    public MouseBtn Button { get; }    // valid when Kind == Mouse

    private InputKey(InputKind kind, ushort vk, MouseBtn btn) { Kind = kind; Vk = vk; Button = btn; }

    public static readonly InputKey None = new(InputKind.None, 0, MouseBtn.Left);
    public static InputKey FromVk(ushort vk) => vk == 0 ? None : new(InputKind.Key, vk, MouseBtn.Left);
    public static InputKey FromMouse(MouseBtn b) => new(InputKind.Mouse, 0, b);

    public bool IsNone => Kind == InputKind.None;
    public bool IsMouse => Kind == InputKind.Mouse;

    public static InputKey Parse(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return None;
        switch (s.ToLowerInvariant())
        {
            case "tab": return FromVk(0x09);
            case "space": case "spacebar": return FromVk(0x20);
            case "enter": case "return": return FromVk(0x0D);
            case "esc": case "escape": return FromVk(0x1B);
            case "shift": return FromVk(0x10);
            case "ctrl": case "control": return FromVk(0x11);
            case "alt": return FromVk(0x12);
            case "up": return FromVk(0x26);
            case "down": return FromVk(0x28);
            case "left": return FromVk(0x25);
            case "right": return FromVk(0x27);
            case "home": return FromVk(0x24);
            case "end": return FromVk(0x23);
            case "pgup": case "pageup": case "prior": return FromVk(0x21);
            case "pgdn": case "pagedown": case "next": return FromVk(0x22);
            case "ins": case "insert": return FromVk(0x2D);
            case "del": case "delete": return FromVk(0x2E);
            case "mouse1": case "lmb": case "leftmouse": case "m1": return FromMouse(MouseBtn.Left);
            case "mouse2": case "rmb": case "rightmouse": case "m2": return FromMouse(MouseBtn.Right);
            case "mouse3": case "mmb": case "middlemouse": case "m3": return FromMouse(MouseBtn.Middle);
            case "mouse4": case "xbutton1": case "x1": case "m4": case "mb4": return FromMouse(MouseBtn.X1);
            case "mouse5": case "xbutton2": case "x2": case "m5": case "mb5": return FromMouse(MouseBtn.X2);
        }
        string low = s.ToLowerInvariant();
        if (low[0] == 'f' && low.Length >= 2 && int.TryParse(low.Substring(1), out int fn) && fn is >= 1 and <= 24)
            return FromVk((ushort)(0x70 + (fn - 1)));
        return FromVk(char.ToUpperInvariant(s[0]));   // single letter/digit → VK
    }

    /// <summary>
    /// Split a chord like "alt+b", "ctrl+shift+i" or plain "h" into its modifier virtual-keys and
    /// the key they modify. Modifiers are returned in press order and must be released in reverse.
    ///
    /// Kept separate from <see cref="Parse"/> on purpose: a chord is not an InputKey, and folding
    /// modifiers into one would quietly change every existing binding's meaning. The game's own
    /// keybind UI writes chords this way, so a user can copy what they see there.
    /// </summary>
    public static (ushort[] Mods, InputKey Key) ParseChord(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return (Array.Empty<ushort>(), None);
        string[] parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (Array.Empty<ushort>(), None);        // "+" splits to nothing
        if (parts.Length == 1) return (Array.Empty<ushort>(), StrictKey(parts[0]));

        var mods = new System.Collections.Generic.List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods.Add(0x11); break;
                case "alt": mods.Add(0x12); break;
                case "shift": mods.Add(0x10); break;
                default: return (Array.Empty<ushort>(), None);   // not a modifier — refuse rather than guess
            }
        InputKey key = StrictKey(parts[^1]);
        return key.IsNone || key.IsMouse ? (Array.Empty<ushort>(), None) : (mods.ToArray(), key);
    }

    /// <summary>
    /// <see cref="Parse"/>, minus its last-resort "use the first character" rule.
    ///
    /// That rule is right for a keymap read from the game's own config, where the token is known
    /// good. It is dangerous for a box a human types into: "alt b" (space instead of plus) parses
    /// to A, and the bot then taps A at a focused EverQuest — auto-attack — while the log claims
    /// it pressed alt+b. A chord we don't understand must be refused, out loud, not approximated.
    /// </summary>
    private static InputKey StrictKey(string token)
    {
        token = token.Trim();
        InputKey k = Parse(token);
        if (k.IsNone || k.IsMouse) return k;
        // Multi-character token that came back as a single character = the fallback fired.
        return token.Length > 1 && k.Display.Length == 1 ? None : k;
    }

    /// <summary>Human-readable label for logs/UI ("Tab", "Mouse5", "4", "F1").</summary>
    public string Display => Kind switch
    {
        InputKind.None => "—",
        InputKind.Mouse => Button switch
        {
            MouseBtn.Left => "Mouse1", MouseBtn.Right => "Mouse2", MouseBtn.Middle => "Mouse3",
            MouseBtn.X1 => "Mouse4", MouseBtn.X2 => "Mouse5", _ => "Mouse"
        },
        _ => Vk switch
        {
            0x09 => "Tab", 0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc",
            0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
            0x2D => "Ins", 0x2E => "Del",
            >= 0x70 and <= 0x87 => "F" + (Vk - 0x6F),
            _ => ((char)Vk).ToString()
        }
    };
}
