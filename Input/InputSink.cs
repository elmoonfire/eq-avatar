using System;
using System.Runtime.InteropServices;

namespace EQAvatar.Spike.Input;

/// <summary>
/// The seam every control method plugs into. Foreground/SendInput today; an injection sink,
/// an isolated-desktop sink, or a VM sink can implement the same interface later without the
/// roles knowing or caring which one is in use.
/// </summary>
public interface IInputSink
{
    string Name { get; }
    /// <summary>Deliver a key OR mouse-button press. Returns true only if it was actually sent.</summary>
    bool Send(InputKey key, int holdMs = 40);
    /// <summary>Convenience for a keyboard virtual-key.</summary>
    bool SendKey(ushort vk, int holdMs = 40);
    /// <summary>Whether the sink can deliver right now (e.g., game is focused).</summary>
    bool Ready { get; }
}

/// <summary>
/// Foreground control: synthesizes real input to the focused window. Critically, it ONLY
/// fires when the EQL window is actually in the foreground — so the bot can never spray
/// keystrokes into whatever else you're doing. Tab away and it silently pauses.
/// </summary>
public sealed class ForegroundSendInputSink : IInputSink
{
    private readonly Func<IntPtr> _target;

    public ForegroundSendInputSink(Func<IntPtr> target) => _target = target;

    public string Name => "Foreground (SendInput)";

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public bool Ready
    {
        get
        {
            IntPtr t = _target();
            return t != IntPtr.Zero && GetForegroundWindow() == t;
        }
    }

    public bool SendKey(ushort vk, int holdMs = 40) => Send(InputKey.FromVk(vk), holdMs);

    public bool Send(InputKey key, int holdMs = 40)
    {
        if (!Ready || key.IsNone) return false;
        if (key.IsMouse) InputProbe.MouseTap(key.Button, holdMs);
        else InputProbe.SendInputKey(key.Vk, holdMs);
        return true;
    }
}
