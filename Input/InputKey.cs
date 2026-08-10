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
            >= 0x70 and <= 0x87 => "F" + (Vk - 0x6F),
            _ => ((char)Vk).ToString()
        }
    };
}
