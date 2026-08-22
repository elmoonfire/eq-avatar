using System;
using System.Collections.Generic;
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

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    // Static on purpose — see the class comment. Losing this delegate to the GC is a process crash.
    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;
    private static Action? _onPanic;
    // A second reference that Uninstall does NOT clear, so Refresh can rebuild after a failed
    // re-seat. _onPanic is what the callback reads; this is what the intent remembers.
    private static Action? _onPanicKeep;
    private static uint _vk;

    /// <summary>
    /// Other keys to WATCH — observed as they pass, never taken away from the game.
    ///
    /// This is why they live here rather than in RegisterHotKey. Registering a bare key claims it
    /// system-wide and SWALLOWS it: bind F7 to a hotbar slot in game, open this app, and that slot
    /// silently stops working until the app closes — a symptom nobody would think to blame on a bot
    /// launcher. The hook this class already runs sees every key without taking any, which is the
    /// property the class comment above calls out as the reason it exists. It also removes the
    /// "another app got there first" failure entirely, which is how the last start key was lost.
    ///
    /// The cost is that a hook sees keys everywhere, so the handler — not this class — decides
    /// whether the press was meant for the game. Starting a grind because someone hit F7 in a text
    /// editor would be worse than not having the key at all.
    /// </summary>
    /// COPY-ON-WRITE, so the callback never takes a lock. A low-level hook that overruns Windows'
    /// LowLevelHooksTimeout has the whole chain SILENTLY REMOVED — panic key included, with no
    /// error and no symptom — which is the exact death this class was built to prevent. Today the
    /// only callers are on the hook's own thread and a lock would never contend; publishing a new
    /// array instead means that stays true however this is called next year.
    private static volatile (uint Vk, Action Act)[] _watch = Array.Empty<(uint, Action)>();
    private static readonly object _watchGate = new();

    public static void Watch(uint vk, Action onKey)
    {
        lock (_watchGate)
        {
            var next = new List<(uint, Action)>(_watch.Length + 1);
            foreach ((uint w, Action a) in _watch) if (w != vk) next.Add((w, a));
            next.Add((vk, onKey));
            _watch = next.ToArray();
        }
    }

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
        // The watched keys go with the hook. Refresh() deliberately does NOT clear them — the
        // 30-second re-seat heartbeat runs through it, and dropping the grind keys every time the
        // hook was re-seated would make them work for half a minute at a time.
        _watch = Array.Empty<(uint, Action)>();
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
        // cheap: read one int, compare, post. The actual stopping happens on the UI thread.
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                try
                {
                    // MODIFIER-BLIND, and deliberately back to being so. A previous release shared
                    // this key with Shift+F12 and had to teach the hook to stand aside for shift —
                    // which meant reasoning about WHOSE shift it was, since this app holds shift
                    // itself for every capital letter it types, and getting that wrong killed both
                    // panic paths at once. The grind now has its own quiet function keys and this
                    // watcher has its one job back.
                    uint vk = (uint)Marshal.ReadInt32(lParam);
                    // THE PANIC KEY FIRST, and on its own terms. Whatever else this hook has been
                    // asked to watch, nothing may sit in front of the one key that has to work.
                    if (vk == _vk) { _onPanic?.Invoke(); }
                    else
                    {
                        foreach ((uint w, Action a) in _watch) if (w == vk) { a(); break; }
                    }
                }
                catch { /* a panic key that crashes the input pipeline is worse than none */ }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
