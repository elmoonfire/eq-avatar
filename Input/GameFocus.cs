using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EQAvatar.Spike.Input;

/// <summary>
/// Puts EverQuest in front before a run starts.
///
/// WHY THIS EXISTS. Every role is foreground-only on purpose: <see cref="ForegroundSendInputSink"/>
/// refuses to send anything unless the game is the focused window, so the bot can never spray
/// clicks into whatever else you're doing. The cost of that safety was a chore — press Run in the
/// app, then alt-tab to the game within a few seconds, or watch the runner sit there saying
/// "Paused — EverQuest isn't the focused window". Pressing Run IS the intent to hand over control,
/// so the app hands it over.
///
/// WHY ONLY AT THE START. Focus is also the panic brake: tab away and everything pauses. A runner
/// that re-grabbed focus whenever it lost it would be fighting the user for the mouse — the exact
/// behaviour that makes automation frightening to run. So this is called once, from the button
/// press, and never from inside a loop.
///
/// WHY IT CAN'T JUST CALL SetForegroundWindow. Windows only grants a foreground change to the
/// process that already owns the foreground — true here, since the user just clicked our button,
/// but not true if a menu is open, a drag is in progress, or another app stole focus in between.
/// The AttachThreadInput fallback covers those, and the result is VERIFIED rather than assumed:
/// the caller is told whether the game is actually in front, because "I thought I focused it" is
/// how a run starts clicking at a window that isn't there.
/// </summary>
public static class GameFocus
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    public static bool IsFront(IntPtr h) => h != IntPtr.Zero && GetForegroundWindow() == h;

    /// <summary>One attempt to raise the window. Returns whether it is now genuinely in front.</summary>
    public static bool Bring(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsFront(h)) return true;

        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);       // minimized: restore before raising
        SetForegroundWindow(h);
        if (IsFront(h)) return true;

        // Borrow the current foreground thread's input state so Windows treats the change as
        // coming from the app that owns the foreground — the documented way past the lock.
        uint me = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgTid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint gameTid = GetWindowThreadProcessId(h, out _);
        bool attachedFg = fgTid != 0 && fgTid != me && AttachThreadInput(me, fgTid, true);
        bool attachedGame = gameTid != 0 && gameTid != me && AttachThreadInput(me, gameTid, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
            SetActiveWindow(h);
        }
        finally
        {
            if (attachedGame) AttachThreadInput(me, gameTid, false);
            if (attachedFg) AttachThreadInput(me, fgTid, false);
        }
        return IsFront(h);
    }

    /// <summary>
    /// Raise the window, wait until it really is in front, then wait a beat longer for it to
    /// repaint. That last pause is not politeness: the first thing a run does is photograph the
    /// screen to find an icon or read a nameplate, and a game that has been behind a window for
    /// an hour has not drawn its new frame yet — the capture would be of stale pixels, and the
    /// bot would hunt for a totem in a picture of the app it was just looking at.
    /// </summary>
    public static async Task<bool> BringAndSettleAsync(IntPtr h, int settleMs = 700, int timeoutMs = 2500)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsFront(h)) return true;                      // already up and painted — don't stall the start

        // NEVER on the UI thread. AttachThreadInput merges our input queue with the game's, and
        // attaching to a thread that has stopped pumping messages — which EQ does while it loads a
        // zone — can hang the attaching thread. Hanging the UI thread would also kill the F12 panic
        // hotkey, which is delivered as a window message to that same thread: the one moment you
        // most want the brake is the moment it would be gone.
        bool front = await Task.Run(() => Bring(h));
        for (int waited = 0; !front && waited < timeoutMs; waited += 250)
        {
            await Task.Delay(250);
            front = await Task.Run(() => Bring(h));
        }
        if (front && settleMs > 0) await Task.Delay(settleMs);
        return front;
    }
}
