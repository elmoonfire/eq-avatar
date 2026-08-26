using System;
using System.Collections.Generic;
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
    /// <summary>No real input for this long = machine unattended. INTERNAL, not private, because
    /// Re-Instance asks the same question before it takes the foreground and the app must not have
    /// two different answers to it.</summary>
    internal const int IdleGateSec = 60;
    private const int HardGraceSec = 600;      // unfocused this long = unattended regardless of idle
    private const int RescueCooldownSec = 45;  // one rescue attempt per this window
    private const int KeepAliveMinutes = 5;    // hold-mode heartbeat; the client flags AFK only after ~30

    /// <summary>
    /// How recently a role must have been running for a death or a park to arm the session hold.
    ///
    /// THE GUARD CANNOT TELL A BOT'S DEATH FROM YOURS, and until this existed it did not try. It
    /// tails the client log ALL the time, not only during a run, so the test "a death just landed
    /// and no role is running" is equally true of a run that died and of Hayden playing his own
    /// character and dying. Measured on his machine 08-26: he died at 12:11 with nothing running,
    /// the hold latched, and the app tapped Shift and nudged his mouse every five minutes for the
    /// next two hours while he sat there using the computer.
    ///
    /// Two minutes is comfortably longer than the teardown the death handler already waits out
    /// (6 s) and far shorter than any gap in which a person could sit down and start playing.
    /// </summary>
    private const int RoleRecentSec = 120;

    /// <summary>
    /// How long a hold may last before it gives up.
    ///
    /// The hold exists to carry a client across the gap between a run ending badly and a person
    /// coming back to it. If nobody has come back in this long, nobody is coming, and a latch that
    /// never clears is exactly how this one ran unnoticed for two hours. Ending it costs the
    /// client an idle kick it was going to get anyway once the app is closed; leaving it on costs
    /// the user their mouse.
    ///
    /// TWELVE, not six: the hold's whole job is to carry a character that died at midnight through
    /// to whenever somebody wakes up, and six hours does not reliably cover a night. With the idle
    /// gate above now in place a stale hold is invisible to a person at the keyboard anyway, so
    /// this number is only here to stop the latch living for days.
    /// </summary>
    private const int HoldMaxHours = 12;

    /// <summary>Minutes of UNBROKEN real input, with no role running, that mean a person has taken
    /// the character over by hand and the hold should let go. Three, because our own keep-alive tap
    /// can only manufacture sixty seconds of apparent presence.</summary>
    private const int HoldPresenceMinutes = 3;

    /// <summary>How many rescues inside <see cref="ThrashWindowMinutes"/> mean the guard is not
    /// fixing anything but fighting something. Field evidence (08-25): FORTY rescues at a
    /// metronomic 75-second beat over an hour, every one reporting success, while the character
    /// stood at one `/loc` and killed nothing. A rescue that is needed again a minute later did
    /// not work, and repeating it forever is the app insisting instead of reporting.</summary>
    private const int ThrashRescues = 3;
    private const int ThrashWindowMinutes = 15;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_ESCAPE = 0x1B;

    /// <summary>The activity tap: Shift (bound to nothing alone in any EQ client) plus a 2-pixel
    /// cursor out-and-back. The pair exists because the tap's whole job is resetting the client's
    /// idle clock and there is no readback for "did that count" — a keypress AND a mouse move is
    /// two independent reasons for the answer to be yes. Called only while the game is focused.</summary>
    /// <summary>
    /// WHO HAS THE SCREEN — title and owning process.
    ///
    /// The single most useful fact about a focus loss, and the guard threw it away: forty log
    /// lines on 08-25 said "dismissing whatever is in front" and not one said WHAT was in front,
    /// so an hour of thrashing produced no evidence at all. The house rule that one number from
    /// the app beats a confident guess applies exactly here, and this costs one call.
    /// </summary>
    private static (string Title, string Process) Foreground()
    {
        IntPtr h = GetForegroundWindow();
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        string proc = "?";
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != 0) proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { }
        return (sb.ToString(), proc);
    }

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
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

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

    /// <summary>The last moment a role was actually running, as seen by the 300 ms tick. This is
    /// what separates "a run just ended in a death" from "somebody is playing their character".</summary>
    private DateTime _lastRoleActive = DateTime.MinValue;
    /// <summary>When the current hold began, so it can end.</summary>
    private DateTime _holdSince = DateTime.MinValue;
    /// <summary>Interlocked: one hold-arming task in flight.</summary>
    private int _armingHold;
    /// <summary>Start of the current unbroken run of real input, or MinValue when idle.</summary>
    private DateTime _presentSince = DateTime.MinValue;

    /// <summary>The client's A.F.K. flag went up (and has not been seen to clear).</summary>
    public DateTime? LastAfkAt { get; private set; }
    /// <summary>Last "You have been slain"-class line. For the close post-mortem.</summary>
    public DateTime? LastDeathAt { get; private set; }

    /// <summary>Keep the session alive with periodic input even though no role is running —
    /// set after a death (character parked at bind, hunting would be wrong, but the client
    /// must not be surrendered to the idle kick). Cleared when a role starts or F12 fires.</summary>
    public bool HoldSession
    {
        get => _hold;
        set
        {
            if (value && !_hold)
            {
                _holdSince = DateTime.UtcNow;
                // AND THE PRESENCE CLOCK STARTS OVER. A RUNNING ROLE'S OWN INPUT KEEPS THE IDLE
                // COUNTER PINNED — this file says so a few lines down — so by the time a run ends
                // in a death, `_presentSince` has been unbroken since the run STARTED. The instant
                // the hold armed, `!_roleActive()` became true, that hours-old run of "presence"
                // was consumed, and the hold cleared itself on the very next 300 ms tick: at 3am,
                // to a sleeping user, announcing that they had been using the computer. Thirty
                // minutes later the A.F.K. flag would have gone up with nothing left to answer it.
                // Only presence established AFTER the hold means anybody came back.
                _presentSince = DateTime.MinValue;
            }
            _hold = value;
        }
    }
    private bool _hold;

    /// <summary>Was a role running just now — or recently enough that whatever we are reacting to
    /// probably belongs to it? See <see cref="RoleRecentSec"/>.</summary>
    private bool RoleRecentlyActive
        => _roleActive() || (DateTime.UtcNow - _lastRoleActive).TotalSeconds <= RoleRecentSec;

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
        DateTime now = DateTime.UtcNow;
        if (_roleActive()) _lastRoleActive = now;

        // SOMEBODY CAME BACK. The hold is cleared by a fresh run or by F12, and neither of those
        // is what a person does when they simply sit down and take over the character by hand —
        // so the latch outlived its purpose and went on tapping at them.
        //
        // Sustained real input is the one signal that cannot be anything else. Our own tap does
        // reset the idle counter, but it only fires when the machine has ALREADY been idle a
        // minute, so a run of presence begun by our own tap ends sixty seconds later when the
        // counter climbs back past the gate — it can never reach the threshold below.
        if (IdleSeconds() < IdleGateSec)
        { if (_presentSince == DateTime.MinValue) _presentSince = now; }
        else _presentSince = DateTime.MinValue;

        if (HoldSession && !_roleActive() && _presentSince != DateTime.MinValue
            && _presentSince > _holdSince                      // belt and braces with the setter
            && (now - _presentSince).TotalMinutes >= HoldPresenceMinutes)
        {
            HoldSession = false;
            _log($"You've been using the computer for {HoldPresenceMinutes} minutes straight, so the session hold "
               + "is off — your own input is keeping the client alive better than my tap was. Start a run (or die "
               + "during one) and I'll pick it up again.");
        }

        // A HOLD THAT NEVER ENDS IS NOT A SAFETY FEATURE. See HoldMaxHours. Re-checked inside,
        // because a death landing between the test and the clear would otherwise have its brand
        // new hold cancelled by this line.
        if (HoldSession && _holdSince != DateTime.MinValue)
        {
            DateTime since = _holdSince;                       // read ONCE, then decide on that read
            if ((now - since).TotalHours >= HoldMaxHours)
            {
                HoldSession = false;
                _log($"Session hold has been running {HoldMaxHours} hours with nobody coming back to the character, "
                   + "so I've stopped it. Nothing is being sent to the game any more.");
            }
        }

        IntPtr h = _game();
        if (h == IntPtr.Zero) { _lastFocused = now; return; }   // no game = nothing to guard

        bool focused = GetForegroundWindow() == h;

        if (focused)
        {
            _lastFocused = now;
            // Hold-mode heartbeat. Shift alone is bound to nothing in any EQ client — it is a
            // modifier — so this cannot cast, move, or toggle; it only counts as activity.
            //
            // AND NOT WHILE SOMEBODY IS AT THE KEYBOARD. This is the gate the rescue path below
            // has always had and this one never did, and it is the whole difference between a
            // safety net and a poltergeist: measured on Hayden's machine on 08-26, a death while
            // he was PLAYING armed the hold, and for the next two hours the app tapped Shift and
            // nudged his mouse every five minutes while he sat there using it. The second nudge is
            // an ABSOLUTE move back to where the cursor was 60 ms earlier, so catching him
            // mid-motion yanks the pointer backwards by however far he had moved.
            //
            // And it was never needed in that state. The keep-alive exists to stop an idle kick;
            // the kick clock is driven by the CLIENT seeing no input. A person typing IS input, so
            // while the idle counter is short there is nothing to prevent and nothing to send.
            if (HoldSession && Enabled && IdleSeconds() >= IdleGateSec
                && (now - _lastKeepAlive).TotalMinutes >= KeepAliveMinutes)
            {
                _lastKeepAlive = now;
                Task.Run(ActivityTap);
                _log("Session hold — activity tap sent (Shift + a 2px mouse nudge).");
            }
            return;
        }

        if (!Wanted || ThrashStop) return;
        double unfocused = (now - _lastFocused).TotalSeconds;
        if (unfocused < FocusGraceSec) return;
        // THE HARD-GRACE OVERRIDE IS FOR A RUNNING ROLE ONLY.
        //
        // It exists because a role's own mouse humanizer pollutes GetLastInputInfo — the counter
        // cannot tell this app's SendInput from a person's hand, so on those machines "idle" never
        // arrives and the guard would never rescue a run that genuinely needed it. Ten minutes
        // unfocused is then taken as proof enough.
        //
        // None of that reasoning survives without a role. Nothing is generating synthetic input, so
        // the idle counter is telling the truth, and overriding it means taking the foreground away
        // from somebody who is demonstrably using the computer — pressing Esc into whatever they
        // were in first. Hayden watched exactly that this morning while working in HWiNFO64.
        bool hardGraceApplies = _roleActive() && unfocused >= HardGraceSec;
        if (IdleSeconds() < IdleGateSec && !hardGraceApplies) return;          // a person is here — theirs
        if ((now - _lastRescue).TotalSeconds < RescueCooldownSec) return;
        _lastRescue = now;
        _ = RescueAsync(h, $"unfocused {unfocused:0}s with a run meant to be going");
    }

    private async Task RescueAsync(IntPtr h, string why)
    {
        if (System.Threading.Interlocked.Exchange(ref _rescueBusy, 1) == 1) return;
        try
        {
            (string fgTitle, string fgProc) = Foreground();
            _log($"Unattended guard: {why}. In front: \"{fgTitle}\" ({fgProc}). Refocusing the game.");

            // A context menu runs its own modal input loop and swallows SetForegroundWindow; Esc is
            // the one key every menu answers.
            //
            // NOT INTO THE GAME, AND NOT INTO OURSELVES.
            //
            // ⚠ WHAT ESC ACTUALLY DOES IN EQ LEGENDS, corrected twice by Hayden on 08-25 because
            // this comment got it wrong twice. It does NOT open a main menu (that is WoW; Options
            // here is bound to `o`), and it does NOT close the target window or the unit-frame
            // HP/mana bars — those are persistent HUD, so the Grind role's screen reads are safe
            // from it. What it closes is BAGS, the character inventory, options panels.
            //
            // Which is still a real hazard, but for a different role than the one guessed at:
            // Auto Merge and the Quest Runner CLICK INTO BAG SLOTS, and both are in this guard's
            // definition of "a run is going". An Esc fired mid-merge closes the very window the
            // next click is aimed at — and a bag click is a toggle, so the click that lands
            // afterwards is not the one that was intended.
            //
            // (For the record, Esc was NOT implicated in the 08-25 logout: the server broadcast
            // "coming down in 1 minute" at 11:14:13 and terminated the session at 11:17:20.)
            string me = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            bool ownWindow = fgProc.Equals(me, StringComparison.OrdinalIgnoreCase)
                          || fgProc.StartsWith("eqgame", StringComparison.OrdinalIgnoreCase)
                          || fgProc.StartsWith("everquest", StringComparison.OrdinalIgnoreCase);
            if (!ownWindow) { InputProbe.SendInputKey(VK_ESCAPE, 40); await Task.Delay(300); }
            else _log("Unattended guard: that window belongs to the game or to me, so no Esc — just raising the game.");
            bool ok = await GameFocus.BringAndSettleAsync(h, settleMs: 350);
            if (ok)
            {
                ActivityTap();                              // count as activity immediately
                _lastKeepAlive = DateTime.UtcNow;
                _log("Unattended guard: the game is back in front.");
                NoteRescueAndCheckThrash(fgTitle, fgProc);
            }
            else
                _log("Unattended guard: could NOT bring the game forward — will retry in "
                     + RescueCooldownSec + "s. If this repeats, something modal is holding the screen.");
        }
        catch (Exception ex) { _log("Unattended guard: rescue failed — " + ex.Message); }
        finally { System.Threading.Interlocked.Exchange(ref _rescueBusy, 0); }
    }

    /// <summary>
    /// Record a rescue and decide whether the guard has stopped helping.
    ///
    /// A rescue that reports success and is needed again seventy-five seconds later did not
    /// succeed at anything; repeating it forty times over an hour, as this did on 08-25 while the
    /// character stood at one `/loc` and killed nothing, is the app insisting rather than
    /// reporting. Past the threshold it stands down, says what kept taking the screen, and leaves
    /// the session-keeping tap running so the client at least survives to be looked at.
    /// </summary>
    private void NoteRescueAndCheckThrash(string fgTitle, string fgProc)
    {
        DateTime now = DateTime.UtcNow;
        _recentRescues.Add(now);
        _recentRescues.RemoveAll(t => (now - t).TotalMinutes > ThrashWindowMinutes);
        if (_recentRescues.Count < ThrashRescues || ThrashStop) return;

        ThrashStop = true;
        HoldSession = true;      // keep the client alive; it is the run that is not working
        _log($"⚠ Unattended guard STANDING DOWN: {_recentRescues.Count} refocus rescues in "
           + $"{ThrashWindowMinutes} minutes means something keeps taking the screen and I am not fixing it — "
           + $"the last thief was \"{fgTitle}\" ({fgProc}). While this is happening the role is paused far more "
           + "than it runs, so the character is achieving nothing. I'll keep the session alive so the client "
           + "isn't idle-kicked, but the run needs you: close whatever that window is, then start the run again.");
    }

    /// <summary>Rescue timestamps inside the thrash window.</summary>
    private readonly List<DateTime> _recentRescues = new();

    /// <summary>The guard gave up rescuing focus for this run — see NoteRescueAndCheckThrash.
    /// Cleared when a role is started, which is the user saying "try again".</summary>
    public bool ThrashStop { get; private set; }

    /// <summary>A fresh run clears the stand-down and the rescue history.</summary>
    public void ResetThrash() { ThrashStop = false; _recentRescues.Clear(); }

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
                // WAS THERE A RUN TO STOP? Without this test the answer was "a death landed and no
                // role is running", which is exactly as true of a person playing their own
                // character as it is of a run that just died — and this guard tails the log
                // whether or not anything is running. Hayden died at 12:11 on 08-26 with nothing
                // going, and the hold latched on for two hours.
                bool runWasGoing = RoleRecentlyActive;
                // ONE ARMING ATTEMPT IN FLIGHT. Two death-class lines can land in the same poll —
                // they did on 08-26 — and each spawned a task that waited the same six seconds and
                // then raced the same `!HoldSession` test, so both won and both announced it.
                if (System.Threading.Interlocked.Exchange(ref _armingHold, 1) == 1) break;
                _ = Task.Run(async () =>
                {
                    try
                    {
                    await Task.Delay(6000);
                    if (Enabled && runWasGoing && !_roleActive() && !HoldSession)
                    {
                        HoldSession = true;
                        _log("The death stopped the run — holding the session alive (activity tap "
                           + "every " + KeepAliveMinutes + " min) so the idle kick never starts.");
                    }
                    }
                    finally { System.Threading.Interlocked.Exchange(ref _armingHold, 0); }
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
                // THE SAME PRESENCE GATE AS EVERY OTHER PATH, and it is needed here MOST.
                //
                // This is a second, independent way into RescueAsync and it had none of the Tick
                // path's guards — no idle test, no role test, no stand-down, no cooldown. Worse,
                // gating the keep-alive tap on idleness (which is right) makes this path MORE
                // reachable, not less: suppressing the tap while a person is present is exactly
                // what lets the client reach the A.F.K. flag in the hold state, and the flag then
                // arrived here and took the foreground anyway. The fix for the poltergeist would
                // have rerouted it rather than stopped it.
                //
                // A running role keeps the old behaviour: the A.F.K. flag during a run is the
                // measured start of a kill chain and is worth interrupting someone for.
                // NOT GATED ON ThrashStop, and the first draft was. A stand-down means the guard
                // has stopped FIGHTING for focus every 75 seconds — it does not mean the client
                // should be surrendered. Its own message promises it "leaves the session-keeping
                // tap running", and that promise is empty while the game is unfocused, which is
                // precisely the state a stand-down implies: the tap only fires in the focused
                // branch. So after a stand-down this is the ONLY thing left keeping the client
                // alive, and it runs about once every half hour rather than once a minute.
                // AND `_roleActive()` DOES NOT SURVIVE A STAND-DOWN. Standing down does not stop the
                // run — the role stays Running, idling in its "paused (EQ not focused)" branch —
                // so a bare `_roleActive()` here goes on bypassing the idle gate for ever after the
                // guard has announced it had stopped fighting for the screen. A person who sits
                // down at nine and works in another window would have had Esc pressed into it and
                // EverQuest pulled in front, every half hour, by a run that is achieving nothing.
                // The idle clause still covers the case that matters: at 3am nobody is typing, so
                // the rescue happens anyway.
                bool mayAnswer = Wanted && ((_roleActive() && !ThrashStop) || IdleSeconds() >= IdleGateSec);
                if (Wanted && !mayAnswer)
                    _log("…but you are at the keyboard, and I will not take the screen off you for it. Note that "
                       + "your typing only clears the flag if it goes INTO the game — if you are working in "
                       + "something else, the client will be kicked in about half an hour. Click into EverQuest "
                       + "for a moment, or start a run, and I'll take it from there.");
                if (mayAnswer)
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
