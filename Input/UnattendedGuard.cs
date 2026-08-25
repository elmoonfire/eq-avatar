using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Input;

/// <summary>
/// Keeps an overnight session ALIVE — the counterpart to the focus safety model, not a hole in it.
///
/// WHAT THE FIELD SHOWED (two instrumented nights, deathwatch logs 2026-08-24). The game client
/// does not merely idle when nothing sends it input — it dies, on a schedule, in two stages:
/// the client flags "You are now A.F.K." after its idle threshold, and ~31.6 minutes later the
/// server drops the session; the client then fails ten re-authentications ("Rejected By World"),
/// prints "Bailing with END_GAME", and the PROCESS exits. Anything that stops input long enough
/// therefore costs the whole night. Two triggers are proven: a death (the respawn window takes
/// input away from the world) and a focus loss (a stray desktop context menu sat open in the
/// game-gone screenshot — a right-drag mouselook released over the desktop opens exactly that).
///
/// WHAT THIS CLASS DOES about it, all opt-out via one Grind-page checkbox:
///  1. FOCUS RESCUE. When a role is meant to be running and the game has been unfocused past a
///     grace period, and the machine looks unattended, it closes whatever grabbed the focus
///     (Esc — a context menu's own input loop answers it) and hands the foreground back to the
///     game. "Unattended" is measured, not assumed: no real input for a minute, or the game
///     unfocused for ten. A person tabbing out for a moment never fights it.
///  2. AFK ANSWER. The A.F.K. line is a free 30-minute warning, read from the same log the roles
///     already tail. On seeing it the guard restores focus if needed and taps SHIFT — a key no
///     EQ client binds to anything alone — which clears the flag and resets the idle clock.
///  3. SESSION HOLD. After a death the right thing is to stop PLAYING, not to stop EXISTING:
///     the owner sets <see cref="HoldSession"/> and the guard keeps the client alive with a
///     Shift tap every few minutes until a role starts or the user stops it. Safety means the
///     character does nothing — it never meant handing the client to the idle kick.
///
/// WHY THIS DOES NOT FIGHT THE USER. GameFocus's own header warns that a runner re-grabbing
/// focus in a loop is frightening to run, and that stands. Every rescue here is gated on the
/// machine being unattended, rate-limited to one attempt per cooldown, narrated to the console,
/// and switched off by the same checkbox that turns the guard on. Tab away and MOVE THE MOUSE
/// and nothing will take the foreground from you — the idle gate sees you.
/// </summary>
public sealed class UnattendedGuard : IDisposable
{
    // Tuned from the two instrumented nights rather than theory: the AFK flag came ~30 min after
    // input stopped and the kick ~31.6 min after the flag, so the guard has minutes of slack —
    // these can afford to be conservative.
    private const int FocusGraceSec = 75;      // unfocused this long before a rescue is considered
    private const int IdleGateSec = 60;        // no real input for this long = machine unattended
    private const int HardGraceSec = 600;      // unfocused this long = unattended regardless of idle
    private const int RescueCooldownSec = 45;  // one rescue attempt per this window
    private const int KeepAliveMinutes = 5;    // hold-mode heartbeat; the client flags AFK only after ~30

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_ESCAPE = 0x1B;

    /// <summary>The activity tap: Shift (bound to nothing alone in any EQ client) plus a 2-pixel
    /// cursor out-and-back. The pair exists because the tap's whole job is resetting the client's
    /// idle clock and there is no readback for "did that count" — a keypress AND a mouse move is
    /// two independent reasons for the answer to be yes. Called only while the game is focused.</summary>
    private static void ActivityTap()
    {
        InputProbe.SendInputKey(VK_SHIFT, 45);
        (int cx, int cy) = HumanizedMouse.CursorPos();
        HumanizedMouse.MoveInstant(cx + 2, cy);
        System.Threading.Thread.Sleep(60);
        HumanizedMouse.MoveInstant(cx, cy);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly Func<IntPtr> _game;
    private readonly Func<bool> _roleActive;
    private readonly AppSettings _s;
    private readonly Action<string> _log;

    private EqLogWatcher? _watcher;
    private string? _watchPath;

    private DateTime _lastFocused = DateTime.UtcNow;
    private DateTime _lastRescue = DateTime.MinValue;
    private DateTime _lastKeepAlive = DateTime.UtcNow;
    private int _rescueBusy;                   // Interlocked: one rescue in flight, ever

    /// <summary>The client's A.F.K. flag went up (and has not been seen to clear).</summary>
    public DateTime? LastAfkAt { get; private set; }
    /// <summary>Last "You have been slain"-class line. For the close post-mortem.</summary>
    public DateTime? LastDeathAt { get; private set; }

    /// <summary>Keep the session alive with periodic input even though no role is running —
    /// set after a death (character parked at bind, hunting would be wrong, but the client
    /// must not be surrendered to the idle kick). Cleared when a role starts or F12 fires.</summary>
    public bool HoldSession { get; set; }

    public UnattendedGuard(Func<IntPtr> game, Func<bool> roleActive, AppSettings s, Action<string> log)
    { _game = game; _roleActive = roleActive; _s = s; _log = log; }

    /// <summary>Tail this client log for A.F.K./death lines. Re-attaches cleanly on a path change;
    /// no-ops when the path is unchanged, so callers may call it every role start.</summary>
    public void Attach(string? logPath)
    {
        if (string.IsNullOrEmpty(logPath) || logPath == _watchPath) return;
        _watcher?.Dispose();
        _watchPath = logPath;
        _watcher = new EqLogWatcher(logPath);
        _watcher.LineRead += OnLine;
        _watcher.Start(fromStart: false);
    }

    /// <summary>Seconds since any REAL (or synthetic) input reached this session. While a role is
    /// sending, this hovers near zero; the moment input stops — role paused, respawn window up —
    /// it climbs. That is exactly the condition the guard exists for, so the pollution is welcome:
    /// a busy role means nothing needs rescuing.</summary>
    /// <summary>Public face of the idle measurement, so the recovery path gates on exactly the
    /// same definition of "nobody is here" that the guard does. Two different presence tests in
    /// one app is two different answers to one question.</summary>
    public static double SecondsSinceInput() => IdleSeconds();

    private static double IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        uint t = GetTickCount();
        return t < lii.dwTime ? 0 : (t - lii.dwTime) / 1000.0;
    }

    private bool Enabled => _s.UnattendedGuardEnabled;
    private bool Wanted => Enabled && (_roleActive() || HoldSession);

    /// <summary>Called from the app's existing UI heartbeat (~300 ms). Cheap on every path.</summary>
    public void Tick()
    {
        IntPtr h = _game();
        if (h == IntPtr.Zero) { _lastFocused = DateTime.UtcNow; return; }   // no game = nothing to guard

        bool focused = GetForegroundWindow() == h;
        DateTime now = DateTime.UtcNow;

        if (focused)
        {
            _lastFocused = now;
            // Hold-mode heartbeat. Shift alone is bound to nothing in any EQ client — it is a
            // modifier — so this cannot cast, move, or toggle; it only counts as activity.
            if (HoldSession && Enabled && (now - _lastKeepAlive).TotalMinutes >= KeepAliveMinutes)
            {
                _lastKeepAlive = now;
                Task.Run(ActivityTap);
                _log("Session hold — activity tap sent (Shift + a 2px mouse nudge).");
            }
            return;
        }

        if (!Wanted) return;
        double unfocused = (now - _lastFocused).TotalSeconds;
        if (unfocused < FocusGraceSec) return;
        if (IdleSeconds() < IdleGateSec && unfocused < HardGraceSec) return;   // a person is here — theirs
        if ((now - _lastRescue).TotalSeconds < RescueCooldownSec) return;
        _lastRescue = now;
        _ = RescueAsync(h, $"unfocused {unfocused:0}s with a run meant to be going");
    }

    private async Task RescueAsync(IntPtr h, string why)
    {
        if (System.Threading.Interlocked.Exchange(ref _rescueBusy, 1) == 1) return;
        try
        {
            _log($"Unattended guard: {why} — dismissing whatever is in front and refocusing the game.");
            // A context menu runs its own modal input loop and swallows SetForegroundWindow; Esc is
            // the one key every menu answers. If something other than a menu holds focus, a single
            // Esc while the machine is unattended is the least input that could matter.
            InputProbe.SendInputKey(VK_ESCAPE, 40);
            await Task.Delay(300);
            bool ok = await GameFocus.BringAndSettleAsync(h, settleMs: 350);
            if (ok)
            {
                ActivityTap();                              // count as activity immediately
                _lastKeepAlive = DateTime.UtcNow;
                _log("Unattended guard: the game is back in front.");
            }
            else
                _log("Unattended guard: could NOT bring the game forward — will retry in "
                     + RescueCooldownSec + "s. If this repeats, something modal is holding the screen.");
        }
        catch (Exception ex) { _log("Unattended guard: rescue failed — " + ex.Message); }
        finally { System.Threading.Interlocked.Exchange(ref _rescueBusy, 0); }
    }

    private void OnLine(string raw)
    {
        LogEvent ev = LogEventParser.Parse(raw);
        switch (ev.Kind)
        {
            case LogEventKind.Death:
                LastDeathAt = DateTime.UtcNow;
                // If this death STOPS the run (stop-on-death grind, or the hunt's own teardown),
                // input stops with it — the exact condition the kick clock starts on. Wait out the
                // teardown, then hold. Redundant when the hunt's Died handler already set it, and
                // that redundancy is the point: every role that stops on a death gets the hold,
                // not just the one that grew an event.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(6000);
                    if (Enabled && !_roleActive() && !HoldSession)
                    {
                        HoldSession = true;
                        _log("The death stopped the run — holding the session alive (activity tap "
                           + "every " + KeepAliveMinutes + " min) so the idle kick never starts.");
                    }
                });
                break;

            case LogEventKind.Afk when ev.Text.Contains("no longer", StringComparison.OrdinalIgnoreCase):
                LastAfkAt = null;
                _log("The A.F.K. flag cleared.");
                break;

            case LogEventKind.Afk:
                LastAfkAt = DateTime.UtcNow;
                _log("⚠ The client flagged A.F.K. — measured on 08-24: the server drops the session "
                   + "~30 minutes after this line and the client then EXITS. "
                   + (Wanted ? "Answering it now." : "The unattended guard is off, so I am only telling you."));
                if (Wanted)
                    _ = Task.Run(async () =>
                    {
                        IntPtr h = _game();
                        if (h == IntPtr.Zero) return;
                        if (GetForegroundWindow() != h)
                        {
                            await RescueAsync(h, "the A.F.K. flag is up");
                        }
                        else
                        {
                            ActivityTap();
                            _lastKeepAlive = DateTime.UtcNow;
                            _log("A.F.K. answered with an activity tap (Shift + mouse nudge). "
                               + "If the log doesn't print 'no longer A.F.K.' shortly, tell me — "
                               + "it would mean this client doesn't clear the flag on input.");
                        }
                    });
                break;
        }
    }

    /// <summary>One short clause for the close post-mortem: what this guard last saw before the
    /// window died. The close-time history keeps minutes; this keeps the WHY beside them, because
    /// a crash-after-death and an idle timer must not be averaged into one meaningless number.</summary>
    public string CloseCause(DateTime closedAtUtc) => CloseCauseWithAge(closedAtUtc).Cause;

    /// <summary>
    /// The cause clause AND the age of the event it names.
    ///
    /// They have to come from one decision. When they were computed separately the caller asked
    /// "how old is the newest death-or-AFK?" while this method answered with the DEATH whenever
    /// one existed — so a run whose real cause was an AFK five minutes ago was filed under a
    /// death from two hours before it started, which is the exact mis-attribution the age check
    /// was added to prevent.
    /// </summary>
    public (string Cause, double? AgeMinutes) CloseCauseWithAge(DateTime closedAtUtc)
    {
        // THE MOST RECENT ONE WINS, not death-by-default. Preferring the death meant a close whose
        // real in-run cause was an AFK five minutes earlier got filed under a death from before
        // the run started — and once the caller's age check rejected that death as too old, the
        // AFK was thrown away with it and the close recorded as having no cause at all.
        DateTime? d = LastDeathAt, a = LastAfkAt;
        bool deathNewer = d is DateTime dd && (a is not DateTime aa || dd >= aa);
        if (deathNewer && d is DateTime dv && (closedAtUtc - dv).TotalHours < 3)
            return ($"death {(closedAtUtc - dv).TotalMinutes:0}m before", (closedAtUtc - dv).TotalMinutes);
        if (a is DateTime av && (closedAtUtc - av).TotalHours < 3)
            return ($"afk {(closedAtUtc - av).TotalMinutes:0}m before", (closedAtUtc - av).TotalMinutes);
        if (d is DateTime dv2 && (closedAtUtc - dv2).TotalHours < 3)
            return ($"death {(closedAtUtc - dv2).TotalMinutes:0}m before", (closedAtUtc - dv2).TotalMinutes);
        return ("no death or afk seen", null);
    }

    public void Dispose() { _watcher?.Dispose(); _watcher = null; _watchPath = null; }
}
