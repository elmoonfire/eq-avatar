using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EQAvatar.Spike.Input;

/// <summary>
/// A LOW-LEVEL keyboard hook watching for the panic key, behind RegisterHotKey as belt-and-braces.
///
/// The panic key is the one feature that must work when everything else is on fire, and
/// RegisterHotKey has a silent failure mode aimed straight at it: only ONE app on the system may
/// own a hotkey, F12 is the single most fought-over key on a gaming PC (Steam's screenshot key,
/// debuggers, overlays), and the call reports defeat through a return value this app was
/// discarding. Register second and the panic key simply doesn't exist — no error, no symptom,
/// discovered mid-run with the bot clicking through a bag.
///
/// A WH_KEYBOARD_LL hook has no such exclusivity: every keyboard event in the system passes
/// through it regardless of focus, regardless of who else registered what. The cost is care:
///  · the callback runs inside the system's input pipeline, so it must do nothing but post — a
///    slow hook gets the whole CHAIN silently removed by Windows, panic key included;
///  · the delegate must be held in a static, or the GC collects it and the process faults the
///    next time any key is pressed anywhere;
///  · the key is OBSERVED, never swallowed — the game should still see its F12, because eating
///    keystrokes systemwide is how an automation tool gets mistaken for something worse.
/// </summary>
public static class PanicKey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    /// <summary>KBDLLHOOKSTRUCT.flags bit 4: this event was SYNTHESISED by SendInput rather than
    /// pressed by a person. Offset 8 in the struct, after vkCode and scanCode.</summary>
    private const int LLKHF_INJECTED = 0x10;
    private const int FlagsOffset = 8;

    /// <summary>Is a REAL shift key down — one a person is holding, not one this app injected?</summary>
    private static bool _realShift;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_SHIFT = 0x10;

    /// <summary>
    /// Let SHIFT + the panic key through untouched, because something else now owns that
    /// combination.
    ///
    /// This hook is deliberately modifier-blind — it fires on the key itself, whoever else has
    /// registered what — and that is exactly right for a panic key. But it means that the moment
    /// any Shift+F12 binding exists, pressing it panics: RegisterHotKey would deliver the new
    /// binding AND this hook would stop everything, in an order nobody controls.
    ///
    /// So the exception is narrow and it is COUPLED TO REALITY: it is only turned on when the
    /// Shift+F12 registration actually succeeded. If that registration lost the race to another
    /// app, this stays off and bare-modifier-blind behaviour is restored, because a panic key that
    /// declines to fire in favour of a binding that does not exist is the worst of both.
    ///
    /// AND IT ONLY EVER STANDS ASIDE FOR A SHIFT A PERSON IS HOLDING. This app injects shift
    /// constantly — ChatTyper holds it for every capital letter of every /say, and a bound chord
    /// like alt+shift+B holds it too — and asking the SYSTEM whether shift is down cannot tell the
    /// difference. That version had the panic key dying in the worst possible window: while the bot
    /// is typing into the game is exactly when someone reaches for F12, and in that moment the
    /// injected shift would suppress this hook AND stop RegisterHotKey's bare-F12 registration from
    /// matching, so BOTH paths would fail together and Shift+F12's grind toggle would fire instead
    /// — a partial stop, silently. The two paths have to stay independent, so this one tracks real
    /// keystrokes and ignores injected ones.
    /// </summary>
    public static bool IgnoreWithShift { get; set; }

    // Static on purpose — see the class comment. Losing this delegate to the GC is a process crash.
    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static Action? _onPanic;
    // A second reference that Uninstall does NOT clear, so Refresh can rebuild after a failed
    // re-seat. _onPanic is what the callback reads; this is what the intent remembers.
    private static Action? _onPanicKeep;
    private static uint _vk;

    /// <summary>Install the watcher. `onPanic` is invoked from inside the input pipeline — it must
    /// only POST (Dispatcher.BeginInvoke) and return. Safe to call once; re-calls are ignored.</summary>
    public static bool Install(uint vk, Action onPanic)
    {
        if (_hook != IntPtr.Zero) return true;
        _vk = vk;
        _onPanic = onPanic;
        _onPanicKeep = onPanic;
        _wanted = true;
        _proc = Callback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    /// <summary>True from the first successful Install until a deliberate Uninstall — including
    /// across a failed re-seat, which is the point: "should be watching" and "is watching" being
    /// separate facts is what lets the heartbeat keep retrying instead of giving up silently.</summary>
    private static bool _wanted;
    private static bool _refreshFailSaid;

    /// <summary>Re-seat the hook. Windows silently removes a low-level hook whose thread stalls
    /// past LowLevelHooksTimeout, and offers no way to ask whether that has happened — so the
    /// caller heartbeats this instead. A hook that was never wanted stays uninstalled; a re-seat
    /// that FAILS keeps being retried on later heartbeats, because a heartbeat that dies at its
    /// first stumble is the silent-death failure it exists to prevent, moved one step over.</summary>
    public static void Refresh()
    {
        if (!_wanted || _onPanicKeep is null) return;
        uint vk = _vk;
        Action act = _onPanicKeep;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; _proc = null; }
        _onPanic = act;
        _proc = Callback;
        _vk = vk;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero && !_refreshFailSaid)
        {
            _refreshFailSaid = true;      // once — a 30-second failure drumbeat helps nobody
            try { Diag.BotLog.Log("panic", "re-seating the F12 watcher failed — will keep retrying"); }
            catch { }
        }
        if (_hook != IntPtr.Zero) _refreshFailSaid = false;
    }

    public static void Uninstall()
    {
        _wanted = false;
        _onPanicKeep = null;
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _onPanic = null;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        // code < 0 must be passed straight through, per contract. And everything here is deliberately
        // cheap: read two ints, compare, post. The actual stopping happens on the UI thread.
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            try
            {
                uint vk = (uint)Marshal.ReadInt32(lParam);
                bool injected = (Marshal.ReadInt32(lParam, FlagsOffset) & LLKHF_INJECTED) != 0;

                // Track the real shift key. Injected presses are this app's own and are ignored —
                // they must not be able to stand the panic key down.
                if (!injected && vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
                    _realShift = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN && vk == _vk)
                {
                    // BOTH have to agree before the panic stands aside, and they disagree in the
                    // safe direction. A key-up this hook never saw (it was re-seated mid-press, or
                    // the press happened during an alt-tab) would leave _realShift stuck true and
                    // the panic key dead — so the system's own answer gets a veto. If either says
                    // no shift, the panic fires.
                    bool stepAside = IgnoreWithShift && _realShift && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                    if (!stepAside) _onPanic?.Invoke();
                }
            }
            catch { /* a panic key that crashes the input pipeline is worse than none */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
