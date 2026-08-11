using System;
using System.Runtime.InteropServices;

namespace EQAvatar.Spike.Input;

/// <summary>
/// Types slash commands into the FOCUSED window as hardware-like keystrokes: Enter to open chat,
/// each character as a scan-code tap (shift applied where the layout needs it), Enter to send.
/// This is what lets the Follower issue /target, /follow, /assist and /attack with zero in-game
/// social/macro setup. Same SendInput path the probe proved works with EQL.
/// </summary>
public static class ChatTyper
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    private const ushort VK_RETURN = 0x0D, VK_LSHIFT = 0xA0;

    /// <summary>Type text into whatever has keyboard focus right now.</summary>
    public static void TypeText(string text, int perCharMs = 26, Func<int, int>? vary = null)
    {
        foreach (char c in text)
        {
            short vks = VkKeyScanW(c);
            if (vks == -1) continue;                       // not typeable on this keyboard layout
            ushort vk = (ushort)(vks & 0xFF);
            bool shift = (vks & 0x100) != 0;
            if (shift) InputProbe.KeyDown(VK_LSHIFT);
            InputProbe.SendInputKey(vk, 16);
            if (shift) InputProbe.KeyUp(VK_LSHIFT);
            System.Threading.Thread.Sleep(Math.Max(8, vary?.Invoke(perCharMs) ?? perCharMs));
        }
    }

    /// <summary>Open chat (Enter), type a slash command, send it (Enter). Blocking; call off the UI thread.</summary>
    public static void SendCommand(string command, Func<int, int>? vary = null)
    {
        InputProbe.SendInputKey(VK_RETURN, 28);
        System.Threading.Thread.Sleep(Math.Max(60, vary?.Invoke(170) ?? 170));   // let the chat box open
        TypeText(command, 26, vary);
        System.Threading.Thread.Sleep(Math.Max(40, vary?.Invoke(120) ?? 120));
        InputProbe.SendInputKey(VK_RETURN, 28);
    }
}
