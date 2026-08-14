using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Roles;

public sealed class QuestStats
{
    public int Cycles, HandIns, Attempts, Misses;
    public string State = "idle";
    public string LastLine = "";
}

/// <summary>
/// The Quest Runner: repeats a quest chain's hand-ins for as long as you have the items.
///
/// WHAT IT AUTOMATES, AND WHY ONLY THAT. A quest is mostly travel, dialogue and killing — the
/// Grind role already does the killing, and travel is the Maps role's waypoints (and, soon, a
/// recorded path). What is left, and what is genuinely tedious when a quest is farmed, is the
/// hand-in: target, hail, pick the item up, drop it on the NPC, press GIVE, and do it again.
/// That is a fixed gesture, so that is what this drives.
///
/// A CYCLE, NOT A HAND-IN. Hayden's Kerra Isle loop is two items to the same NPC: the Desecrated
/// Kejaar Totem finishes "Something is Wrrrong", which immediately assigns "This Means Warrr",
/// whose Heretic Insurrection Orders go back to the same cat and re-open the first quest. So the
/// unit of repetition is the whole ordered list of hand-ins, hailed once at the top.
///
/// HOW IT KNOWS IT WORKED. It does not trust its own clicks. EQ's log is silent about inventory
/// and about what is on screen, but it is NOT silent about a completed hand-in: the server prints
/// "You offered 1 &lt;item&gt; to &lt;npc&gt;", then some of "has been updated", "You have been
/// given:", "has been assigned the task". Every hand-in waits for one of those before counting.
/// A step that clicks perfectly and confirms nothing is a FAILED step, and two failures in a row
/// stop the run — because the overwhelmingly likely cause is that the item ran out, the second
/// most likely is that a picked point is wrong, and neither is improved by carrying on clicking.
///
/// Foreground-only, same as every other role: it uses <see cref="ForegroundSendInputSink"/> for
/// keys and only moves the mouse while the game is the focused window, so tabbing away pauses it
/// and F12 stops it.
/// </summary>
public sealed class QuestRole
{
    public event Action<string>? Log;
    public event Action? Stopped;
    public QuestStats Stats { get; } = new();
    public bool Running => _cts is { IsCancellationRequested: false };

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private readonly QuestScript _script;
    private readonly IInputSink _sink;
    private readonly AppSettings _s;
    private readonly Func<IntPtr> _hwnd;
    private readonly EqLogWatcher? _watcher;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;

    /// <summary>Set by the log reader the moment the server acknowledges the hand-in in flight.
    /// Armed only for the window between the GIVE click and the confirm deadline.</summary>
    private volatile bool _offered, _advanced;
    private volatile bool _listening;
    /// <summary>When the server last said a task had been assigned. The only honest "you may now
    /// offer the item" — an offer that beats the assignment is refused.</summary>
    private long _assignAtTicks;
    /// <summary>Every line the watcher has delivered since the run began.
    ///
    /// This exists because "the server said nothing" and "I am not reading the server" produce the
    /// IDENTICAL symptom — a hand-in that clicks perfectly and confirms nothing — and the runner
    /// used to blame the first, telling a user with a full bag that he was probably out of items.
    /// A log that has delivered ZERO lines during a run that has been clicking for half a minute
    /// is not a quiet server; it is the wrong file, or /log turned off in game.</summary>
    private int _linesSeen;
    /// <summary>The step currently in flight, so the log matcher can name-check its item.</summary>
    private volatile TurnInStep? _inFlight;
    /// <summary>Set when the last false from HandOverAsync meant "no icon in the bag" — a miss
    /// where NOTHING was clicked and nothing is on the cursor, so the recovery click that shakes a
    /// stuck item back into its slot must NOT run: over a bag it would pick an item UP.</summary>
    private volatile bool _emptyBagMiss;
    /// <summary>
    /// Every line the log produced while the confirmation window was open, and any "You offered"
    /// line in it that named the WRONG item.
    ///
    /// A miss used to report one fact — "nothing came back" — and that one fact covers three
    /// completely different failures: the click never picked anything up, the click picked up the
    /// wrong thing and gave it away, or the hand-over worked and the confirmation matcher is too
    /// narrow. The log distinguishes them for free and we were throwing it away. Now a miss shows
    /// what the server actually said, and silence is reported AS silence.
    /// </summary>
    private readonly List<string> _windowLines = new();
    private string _wrongOffer = "";
    private readonly object _windowGate = new();
    /// <summary>The bag point the item was actually taken from this attempt — which is the found
    /// one, not the picked one, whenever smart find is doing its job. The recovery click has to go
    /// HERE: sending it to the fixed pick shuffles the bag between attempts, and then the run's own
    /// "found it somewhere else" lines look like evidence that an item was consumed.</summary>
    private ScreenPoint _lastSlot = new();
    /// <summary>Whether the log has EVER produced an assignment line this run. EQ Legends may
    /// simply not print one — the quest appears in the journal either way — and waiting every
    /// cycle for a line that is never coming is dead time plus a warning that reads like a fault.</summary>
    private volatile bool _sawAssignEver;
    /// <summary>How many times this run has said the trigger phrase and then waited for a journal
    /// line. Two passes with nothing is evidence about THIS GAME's logging; a busy zone's chatter
    /// is evidence about the zone.</summary>
    private int _phrasePasses;
    private int _finished;

    public QuestRole(QuestScript script, IInputSink sink, AppSettings settings,
                     Func<IntPtr> gameWindow, string? logPath)
    {
        _script = script;
        _sink = sink;
        _s = settings;
        _hwnd = gameWindow;
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath);
    }

    public void Start()
    {
        // One instance, one run: Finish() disposes the log watcher, so a restart gets a fresh role.
        if (Running || Volatile.Read(ref _finished) != 0) return;
        _cts = new CancellationTokenSource();
        if (_watcher is not null) { _watcher.LineRead += OnLine; _watcher.Start(fromStart: false); }
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>End the run exactly once. Idempotent on purpose: the loop calls this from its
    /// normal exit AND from its catch, and on window close the UI's dispatcher is already dead, so
    /// raising <see cref="Log"/> here throws straight into that catch — which would otherwise call
    /// Finish a second time, double-unsubscribing and double-disposing the log watcher.</summary>
    private void Finish(string why)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _listening = false;
        Stats.State = "stopped";
        // Cancel first, so Running reads false for anything the events below wake up: without this
        // the card stays stuck on "■ Stop" after a run ends by itself.
        try { _cts?.Cancel(); } catch { }
        if (_watcher is not null) { _watcher.LineRead -= OnLine; _watcher.Dispose(); }
        _script.LastRun = DateTime.Now;
        try { QuestScriptStore.Current.Save(); } catch { }
        try { Log?.Invoke(why); } catch { }
        try { Stopped?.Invoke(); } catch { }
    }

    // ---------------------------------------------------------------- log

    /// <summary>
    /// Decide whether a log line is the server acknowledging the hand-in currently in flight.
    ///
    /// Deliberately narrow. The obvious wider net — count a faction adjustment or an experience
    /// line — is wrong here, because both of those also print for every mob anyone in the group
    /// kills, and a run that "confirms" off a passing kill never notices that the items ran out.
    /// So: the definitive "You offered … &lt;item&gt;" line, or a quest-state line naming the quest
    /// this step belongs to, or "You have been given:" naming a reward. Nothing generic.
    /// </summary>
    private void OnLine(string line)
    {
        if (line.Length == 0) return;
        Interlocked.Increment(ref _linesSeen);

        // ASSIGNMENT WATCH — armed across the hail, independent of the hand-in listener.
        //
        // Field evidence (three cycles, 2026-08-14): the FIRST totem offer of every cycle went
        // unanswered and the retry ~15 s later always worked, at identical click speed. Speed was
        // never the difference; STATE was. The hail is what (re)assigns the task, and an item
        // offered before the server has actually assigned it is refused — so the run paid one
        // wasted offer and one 12-second confirm timeout per cycle, every cycle.
        // Recorded ALWAYS, not only while waiting. Completing the Orders re-opens "Something is
        // Wrrrong", so the assignment for the NEXT cycle usually prints during the PREVIOUS cycle's
        // confirmation — before this cycle has even hailed. A flag reset at the hail would throw
        // that away and then wait the full window for a line that had already gone past.
        if (line.IndexOf("has been assigned the task", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Volatile.Write(ref _assignAtTicks, DateTime.UtcNow.Ticks);
            _sawAssignEver = true;
        }

        if (!_listening) return;
        TurnInStep? step = _inFlight;
        if (step is null) return;

        string l = line.ToLowerInvariant();
        string item = step.Item.ToLowerInvariant();
        string quest = step.Quest.ToLowerInvariant();

        // Keep what the server said while we were waiting, so a miss can quote it. Bounded: a busy
        // zone can print faster than anyone can read, and this is evidence, not a transcript.
        // A RING, not a prefix. Keeping the first twelve meant that handing an item in beside a
        // fight filled the buffer with combat spam in the first second and threw away every line
        // that came after — including the refusal, the late offer, and the task update, which are
        // the only lines anyone wants.
        lock (_windowGate)
        {
            _windowLines.Add(line.Trim());
            if (_windowLines.Count > 16) _windowLines.RemoveAt(0);
        }

        // The only line that names THIS hand-in. When the item is known, nothing else counts —
        // see the note below.
        if (l.Contains("you offered") || l.Contains("you have given"))
        {
            if (item.Length == 0 || l.Contains(item))
            {
                _offered = true;
                Stats.LastLine = line.Trim();
                return;
            }
            // SOMETHING was handed over and it was not the item we meant. That single line splits
            // the failure space in half: the gesture worked and the bag search picked the wrong
            // icon. Without it, giving away the wrong item and clicking an empty square produce
            // word-for-word the same report.
            lock (_windowGate) _wrongOffer = line.Trim();
            return;
        }

        // Everything below is a FALLBACK for a hand-in whose item the wiki never named, and it is
        // gated on that. In a two-item cycle the looser lines are actively wrong: completing step 1
        // prints "You have been given: <reward>" and "has been assigned the task This Means Warrr"
        // — the second of which carries step 2's quest name. Armed for step 2 they would confirm a
        // hand-in that gave nothing away, which is the exact failure this matcher exists to catch.
        if (item.Length > 0) return;

        bool questLine = l.Contains("has been updated") || l.Contains("has been assigned the task")
                      || l.Contains("you have completed") || l.Contains("your task");
        if ((questLine && quest.Length > 0 && l.Contains(quest)) || l.Contains("you have been given"))
        {
            _advanced = true;
            Stats.LastLine = line.Trim();
        }
    }

    // ---------------------------------------------------------------- screen

    /// <summary>Normalized game-window point → absolute screen pixel, or null if the window
    /// has gone away.</summary>
    private (int x, int y)? Screen(ScreenPoint p)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || !p.Set || !GetWindowRect(h, out RECT r)) return null;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return null;
        return (r.Left + (int)(p.X * w), r.Top + (int)(p.Y * ht));
    }

    /// <summary>Why the most recent ClickAt returned false. A silent false is indistinguishable
    /// from "the bot did nothing" — which is exactly what the first field test looked like.</summary>
    private string _clickFailWhy = "";
    /// <summary>Narrate the first cycle's clicks to the log, coordinates and all, so a field test
    /// that goes wrong says WHERE every click landed instead of nothing at all.</summary>
    private bool _narrate = true;

    /// <summary>Move and click one picked point. Returns false when it can't — and says why in
    /// <see cref="_clickFailWhy"/>, because the caller may be about to loop on it.</summary>
    private bool ClickAt(ScreenPoint p, int settleMs, string what = "")
    {
        if (!p.Set) { _clickFailWhy = $"the pick for {what} isn't set"; return false; }
        if (!_sink.Ready) { _clickFailWhy = "EverQuest isn't the focused window"; return false; }
        if (Screen(p) is not (int x, int y))
        { _clickFailWhy = "the game window has gone away"; return false; }
        if (_narrate && what.Length > 0)
            Log?.Invoke($"· clicking {what} at {p.X * 100:0.0}%, {p.Y * 100:0.0}% → screen ({x}, {y})");
        HumanizedMouse.MoveInstant(x + _rng.Next(-2, 3), y + _rng.Next(-2, 3));
        Thread.Sleep(140 + _rng.Next(80));
        if (!_sink.Ready)                               // re-check: focus can be lost mid-gesture
        { _clickFailWhy = "focus was lost mid-gesture"; return false; }
        HumanizedMouse.Click(_rng);
        Thread.Sleep(settleMs + _rng.Next(90));
        return true;
    }

    /// <summary>Type a slash command, but only while EQ is the focused window. ChatTyper sends raw
    /// SendInput with no target of its own, so without this check a hand-in that overlaps an
    /// alt-tab types "/say Hail, …" into whatever the user switched to.</summary>
    private bool Say(string command)
    {
        if (!_sink.Ready) return false;
        ChatTyper.SendCommand(command);
        return true;
    }

    /// <summary>
    /// Press the OPEN ALL BAGS key, if one is configured. Chords ("alt+b") are held around the
    /// tap and released in reverse — and released even if the send fails, because a stuck ALT is
    /// worse than a closed bag.
    ///
    /// Deliberately the game's OPEN command rather than its show/hide TOGGLE: open is idempotent,
    /// so pressing it when the bags are already open costs nothing, while a toggle pressed on a
    /// guess is a coin flip that closes them half the time.
    /// </summary>
    private bool OpenBags(string why)
    {
        string spec = (_script.OpenBagsKey ?? "").Trim();
        if (spec.Length == 0) return false;
        (ushort[] mods, InputKey key) = InputKey.ParseChord(spec);
        if (key.IsNone) { Log?.Invoke($"⚠ \"{spec}\" isn't a key I can press — use something like alt+b."); return false; }
        if (!_sink.Ready)
        {
            // Never silent. A skipped open-bags press means the next empty scan gets blamed on
            // running out of items, and the log has to show that the check didn't actually run.
            Log?.Invoke("· couldn't press the open-bags key — EverQuest isn't the focused window.");
            return false;
        }

        // NOTHING between the press and the release except the keystroke itself. Log?.Invoke
        // marshals to the UI thread and rebuilds the quest list; doing that with ALT physically
        // down would hold ALT into the game for as long as the redraw takes.
        bool sent;
        foreach (ushort m in mods) InputProbe.KeyDown(m);
        try { sent = _sink.Send(key, 45); }
        finally { for (int i = mods.Length - 1; i >= 0; i--) InputProbe.KeyUp(mods[i]); }

        Log?.Invoke(sent
            ? $"· pressed {spec} to open the bags ({why})"
            : $"· the open-bags key ({spec}) didn't send — focus was lost mid-press.");
        return sent;
    }

    private async Task<bool> WaitFocus(CancellationToken ct)
    {
        bool warned = false;
        while (!ct.IsCancellationRequested && !_sink.Ready)
        {
            if (!warned) { warned = true; Stats.State = "waiting for the game window"; Log?.Invoke("Paused — EverQuest isn't the focused window."); }
            await Task.Delay(400, ct);
        }
        if (warned && !ct.IsCancellationRequested) Log?.Invoke("Game focused again — carrying on.");
        return !ct.IsCancellationRequested;
    }

    // ---------------------------------------------------------------- one hand-in

    /// <summary>
    /// Pick the item up, drop it on the NPC, press GIVE, and wait for the server to say so.
    /// Returns null when the game lost focus mid-gesture (retry, don't count it as a miss).
    /// </summary>
    private async Task<bool?> HandOverAsync(TurnInStep step, CancellationToken ct)
    {
        Stats.State = $"handing over {step.Item}";
        Stats.Attempts++;
        _emptyBagMiss = false;

        // Where the item ACTUALLY is right now. The picked slot is only the fallback: totems
        // migrate through the bag as each one is consumed, and clicking yesterday's slot is how
        // the first field test handed nothing to anyone.
        ScreenPoint slot = step.Slot;
        if (_script.SmartFind && _script.BagSet && step.HasIcon)
        {
            // The sliding search compares windows of the icon's OWN size, so its scores actually
            // discriminate (the grid compare once called Indicolite Gauntlets a totem). Steps
            // picked before icon sizes were stored fall back to the old grid scan.
            bool sliding = step.HasIconSize;
            QuestFind.IconHit? Scan() => sliding
                ? QuestFind.FindIconSliding(_hwnd(), _script, step)
                : QuestFind.FindIconCell(_hwnd(), _script, step);
            double accept = sliding ? QuestFind.SlidingAcceptDistance : QuestFind.IconAcceptDistance;
            QuestFind.IconHit? hit = Scan();

            // A closed bag and an empty bag look identical from here. Before believing the empty
            // one — which stops the run — press OPEN ALL BAGS and look again. Costs one keystroke
            // on the last cycle; saves the whole night when a bag got shut at cycle 40.
            if ((hit is null || hit.Dist > accept) && (_script.OpenBagsKey ?? "").Trim().Length > 0)
            {
                if (OpenBags($"nothing matched {step.Item} — checking the bags are open"))
                {
                    Thread.Sleep(450);
                    QuestFind.IconHit? again = Scan();
                    if (again is not null && (hit is null || again.Dist < hit.Dist)) hit = again;
                }
            }

            if (hit is null)
            {
                Log?.Invoke($"⚠ couldn't scan the bag area for {step.Item} — using the picked slot.");
            }
            else if (hit.Dist <= accept)
            {
                slot = new ScreenPoint { X = hit.X, Y = hit.Y };
                if (_narrate) Log?.Invoke(sliding
                    ? $"· found {step.Item} at {hit.X * 100:0.0}%, {hit.Y * 100:0.0}% of the window (match {hit.Dist:0})"
                    : $"· found {step.Item} in bag cell {hit.Row + 1},{hit.Col + 1} (match {hit.Dist:0})");
            }
            else
            {
                // No spot holds this icon: the honest out-of-items signal, seen BEFORE an item is
                // offered rather than inferred from two unanswered offers.
                Log?.Invoke($"✖ no {step.Item} found in the bag area (closest match {hit.Dist:0}, need ≤ {accept:0}).");
                _emptyBagMiss = true;
                return false;
            }
        }

        _lastSlot = slot;                       // the recovery click must come back HERE, not to the pick
        if (!ClickAt(slot, 260, $"the bag slot for {step.Item}")) return null;   // pick the item up

        // From here the item is ON THE CURSOR. Every early exit has to put it back in ITS OWN slot
        // first: the cycle restarts at step 1, and step 1's first act is to click step 1's slot —
        // which, with step 2's item still held, drops it into the wrong bag square and every
        // pick-up after that grabs the wrong thing.
        bool holding = true;
        bool? Drop(bool? result) { if (holding) { ClickAt(slot, 260, "the bag slot (returning the item)"); holding = false; } return result; }

        // Where the NPC actually stands. The nameplate follows him; the picked point doesn't.
        ScreenPoint npc = _script.Layout.Npc;
        // Only worth an OCR pass if he could be targeted: the nameplate is drawn over the TARGET,
        // so with /target off it will never be there and the read is a guaranteed miss costing a
        // screen capture and an OCR on every single hand-in.
        if (_script.SmartFind && _script.NpcAnchorLearned && _script.TargetByName)
        {
            QuestFind.NpcHit? found = await QuestFind.FindNpcAsync(_hwnd(), _script);
            if (found is not null)
            {
                npc = new ScreenPoint { X = found.X, Y = found.Y };
                if (_narrate) Log?.Invoke($"· nameplate \"{found.Matched}\" at {found.NameX * 100:0.0}%, {found.NameY * 100:0.0}% → clicking the body below it");
            }
            else if (_narrate)
            {
                Log?.Invoke("· nameplate not readable right now — using the picked NPC spot");
            }
        }

        // The trade window has to be UP before GIVE is pressed, and nothing on screen or in the log
        // says when it is. So this is a wait, tuned on the card, not a guess baked into the code.
        int settle = Math.Clamp(_script.GiveSettleMs, 200, 4000);
        if (!ClickAt(npc, settle, "the NPC")) return Drop(null);   // drop it on the NPC → give window

        // Arm the listener HERE, immediately before the button that commits the trade — not at the
        // top of the cycle. Everything above takes seconds, and a confirmation armed that early can
        // be satisfied by the tail of the PREVIOUS hand-in.
        _inFlight = step;
        _offered = _advanced = false;
        lock (_windowGate) { _windowLines.Clear(); _wrongOffer = ""; }
        _listening = true;

        if (!ClickAt(_script.Layout.GiveButton, 500, "the GIVE button")) { _listening = false; _inFlight = null; return Drop(null); }
        holding = false;                                            // GIVE was pressed; the item is the server's problem now
        if (_script.Layout.Confirm.Set) ClickAt(_script.Layout.Confirm, 400);

        Stats.State = "waiting for the server";
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_script.ConfirmSeconds, 3, 60));
        bool confirmed = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_offered || _advanced) { confirmed = true; break; }
            await Task.Delay(250, ct);
        }
        _listening = false;
        _inFlight = null;
        return confirmed;
    }


    /// <summary>
    /// Put the item back after a failed offer.
    ///
    /// WHAT THIS CANNOT KNOW, and stopped pretending to. After a miss the item is in one of three
    /// places — on the cursor, back in its square, or sitting in a trade window the server never
    /// answered — and nothing available here distinguishes them. ClickAt returns true for "I moved
    /// the mouse and clicked", not for "a window was under it". Two cleverer versions of this
    /// method were written and both were wrong:
    ///
    ///   • Escape, on the theory that past GIVE the item must be in a trade window. When no window
    ///     was open Escape closed the BAGS instead, after which the icon scan reads a shut bag as
    ///     an empty one and the run reports being out of items. Worse than the problem.
    ///   • Re-scan the bag and click only if the item has gone. The scanner finds the best match
    ///     ANYWHERE in the bag area and cannot see the cursor — so with a stack of totems, or any
    ///     second copy, it always says "still there" no matter what is being held. It answers
    ///     "was that the last copy", which is not the question.
    ///
    /// So: the plain click, at the square the item was actually taken from. Its known cost is that
    /// when the cursor is already empty it picks an item up and the next attempt puts it down
    /// somewhere else — the totem walking one row down the bag between attempts. That is cosmetic,
    /// it is now understood, and it is narrated every time so it can never again be mistaken for
    /// evidence that the server consumed something.
    /// </summary>
    private void Recover(TurnInStep step)
    {
        ScreenPoint back = _lastSlot.Set ? _lastSlot : step.Slot;
        if (!back.Set) return;
        // NOT gated on _narrate. This click moves things in the bag, and a click that moves things
        // invisibly is how the last three diagnoses went wrong.
        // Reported AFTER the fact, from the result. Announcing it first would put a positive
        // "I clicked here" in the log for a click that never happened — ClickAt refuses while the
        // game isn't focused — and a false click in the record is the exact thing this line exists
        // to prevent.
        if (ClickAt(back, 300))
            Log?.Invoke($"· clicked {back.X * 100:0.0}%, {back.Y * 100:0.0}% to put {step.Item} back "
                      + "(if the cursor was already empty this picked one up instead, and the next "
                      + "attempt will find it a row away — that is this click, not the server)");
        else
            Log?.Invoke($"⚠ couldn't put {step.Item} back: {_clickFailWhy}. If it was on the cursor "
                      + "it still is — click it back into a bag square yourself before carrying on.");
    }

    /// <summary>
    /// Say what the server actually said while we were waiting.
    ///
    /// Three failures wear the same face — nothing was picked up, the wrong thing was picked up
    /// and given away, or the hand-over worked and the matcher missed it. The log already knows
    /// which; it was simply never asked. Silence is reported AS silence, because "the log said
    /// nothing for twelve seconds" is itself the loudest possible clue.
    /// </summary>
    private void ReportWindow(TurnInStep step)
    {
        string wrong;
        List<string> seen;
        lock (_windowGate) { wrong = _wrongOffer; seen = new List<string>(_windowLines); }

        if (wrong.Length > 0)
        {
            // TWO things produce this line and they have opposite fixes, so it must not pick one.
            // Either the bag search grabbed the wrong icon, or the hand-in WORKED and the name we
            // are matching on — scraped off the wiki — isn't spelled the way the server spells it
            // (a plural, an apostrophe, a qualifier). Asserting the first would send someone to
            // re-pick a slot that was right, while the run counted a successful hand-in as a miss
            // and eventually told them they were out of an item they still had a bag full of.
            // So: show both names and let the person who can SEE the game decide.
            // ONE line, not four. Every Log?.Invoke marshals to the UI thread, lands in the card's
            // single-line status field and colours it by its OWN first character — so a four-part
            // message ends with the status reading a fragment, in the green that means "that went
            // fine", while the ⚠ headline is overwritten twice on the way there. It also costs the
            // runner four synchronous round-trips at the exact moment it is about to click.
            Log?.Invoke($"⚠ the server took something, but not under the name I'm watching for. "
                      + $"I'm matching on \"{step.Item}\" · the log said \"{wrong}\" · "
                      + "if those are the SAME item then the hand-in WORKED and my name for it is wrong "
                      + "(fix the item's name on the card); if they're different items the bag search "
                      + "grabbed the wrong icon (re-pick that slot with a tighter box round it).");
            return;
        }
        if (seen.Count == 0)
        {
            Log?.Invoke("· the log said NOTHING at all during that wait — so as far as the server is "
                      + "concerned nothing was handed over. The clicks landed somewhere that didn't "
                      + "open a trade: check the NPC and GIVE picks, and that he's in reach.");
            return;
        }
        List<string> tail = seen.Count > 4 ? seen.GetRange(seen.Count - 4, 4) : seen;
        Log?.Invoke("· what the log said while waiting (most recent last): " + string.Join("  |  ", tail)
                  + (seen.Count > tail.Count ? $"  (+{seen.Count - tail.Count} earlier)" : ""));
    }

    // ---------------------------------------------------------------- the loop

    private async Task LoopAsync(CancellationToken ct)
    {
        (int x, int y) home = HumanizedMouse.CursorPos();
        try
        {
            if (!_script.Ready)
            {
                Finish("Can't start — still need a pick for: " + _script.Missing()
                     + ". Use the ◎ buttons on the quest's automation card.");
                return;
            }
            if (_watcher is null)
                Log?.Invoke("⚠ No log file, so hand-ins cannot be confirmed — it will click and count nothing. "
                          + "Set the log folder on the Log Reader page and restart the run.");

            string items = string.Join(" → ", _script.Steps.Select(s => s.Item));
            Log?.Invoke($"Quest Runner: {items} → {_script.Npc}"
                      + (_script.Repeat > 0 ? $", {_script.Repeat} cycle(s)." : ", until the items run out."));

            // Say out loud what smart find can actually do THIS run. Silence here is how a user
            // updates, runs, and watches the identical failure with no hint the fix exists but is
            // unarmed: an old script has no icon signatures, no bag area and no nameplate anchor.
            if (_script.SmartFind)
            {
                var unarmed = new List<string>();
                if (!_script.BagSet) unarmed.Add("the bag area isn't dragged");
                var oldSig = new List<string>();
                foreach (TurnInStep st in _script.Steps)
                    if (!st.HasIcon) unarmed.Add($"{st.Item}'s icon isn't learned (re-pick its slot)");
                    else if (!st.HasIconSize) oldSig.Add(st.Item);
                if (!_script.TargetByName)
                    Log?.Invoke("· not targeting by name (the say-phrase needs no target), so the nameplate can't "
                              + "be read and the fixed NPC pick does the work. Stand where you picked him.");
                else if (!_script.NpcAnchorLearned)
                    unarmed.Add("the NPC nameplate isn't anchored (re-pick him while targeted)");
                Log?.Invoke(unarmed.Count == 0
                    ? "smart find ARMED: items by icon in the bag area, the NPC by nameplate."
                    : "⚠ smart find is ON but partly unarmed — " + string.Join("; ", unarmed)
                      + ". Unarmed parts fall back to the fixed picks, which is exactly what failed last time.");
                if (oldSig.Count > 0)
                    // ⚠, not "·": the consoles colour warnings amber and dim the routine steps, and
                    // this one has now cost two field runs. The old grid scan divides the bag area
                    // into guessed cells and compares the middle of each — it has matched gauntlets
                    // to a totem at 24 and can "find" an item in an empty square with total
                    // confidence, after which every click in the gesture lands on nothing.
                    Log?.Invoke("⚠ " + string.Join(", ", oldSig) + " still use(s) the OLD grid scan — "
                              + "re-pick the slot once (drag a tight box round the icon) and the precise "
                              + "sliding search takes over. Until then a 'found' line here may be an empty slot.");
            }

            // Open the bags before the first scan rather than hoping they're up. Nothing in the
            // log or on screen says whether they are, so this is the one place a keystroke buys
            // certainty — and if no key is bound, the run simply proceeds as before.
            if (await WaitFocus(ct)) OpenBags("starting the run");

            // PER STEP, not one counter for the run. A cycle restarts from the top after any
            // failure, so a shared counter is reset by step 1 succeeding on the very next pass and
            // can never reach 2 — which is exactly the "you have run out of the second item" case
            // the stop exists for. Keyed by the step object; the list is only edited from the UI
            // while the run is stopped.
            var stepMisses = new Dictionary<TurnInStep, int>();
            int gestureFails = 0;
            // Consecutive passes that got part way and then stuck. Reset by a complete cycle.
            int partialRun = 0;
            // Items offered toward a step's Qty since its last recorded completion.
            var offersToward = new Dictionary<TurnInStep, int>();

            while (!ct.IsCancellationRequested)
            {
                if (_script.Repeat > 0 && Stats.Cycles >= _script.Repeat)
                { Finish($"Done — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s) confirmed."); return; }

                if (!await WaitFocus(ct)) break;

                // ---- top of the cycle: make sure the right NPC is selected and awake
                if (_script.TargetByName && _script.Npc.Length > 0)
                {
                    Stats.State = "targeting";
                    if (!Say("/target " + _script.Npc)) { await Task.Delay(500, ct); continue; }
                    await Task.Delay(700 + _rng.Next(250), ct);
                }
                // Anything assigned from here on counts for THIS cycle. Three seconds of grace
                // reaches back over the previous cycle's last confirmation, which is where the
                // re-assignment usually lands.
                DateTime cycleFrom = DateTime.UtcNow.AddSeconds(-3);
                if (_script.HailFirst)
                {
                    // One keystroke, not a typed sentence: EQL binds hail to a key ("h" by
                    // default), and it acts on the current target — which the /target above (or
                    // the user's own click) has just set.
                    Stats.State = "hailing";
                    InputKey hail = InputKey.Parse(string.IsNullOrWhiteSpace(_script.HailKey) ? "h" : _script.HailKey);
                    if (hail.IsNone || !_sink.Send(hail, 45)) { await Task.Delay(500, ct); continue; }
                    await Task.Delay(900 + _rng.Next(350), ct);
                }
                // THE PHRASE IS THE TRIGGER. Not the hail — saying the bracketed words is what puts
                // the task in the journal, with or without any prior interaction with the NPC. The
                // hail only exists to make him tell you the words in the first place.
                bool saidSomething = false;
                foreach (string phrase in _script.SayPhrases)
                {
                    if (ct.IsCancellationRequested || phrase.Trim().Length == 0) continue;
                    Stats.State = "saying the trigger";
                    if (!Say("/say " + phrase.Trim())) break;
                    saidSomething = true;
                    // Logged, because it is THE step that assigns the task. Its absence from a log
                    // is the first thing anyone diagnosing a refused hand-in needs to see.
                    if (_narrate) Log?.Invoke($"· said \"{phrase.Trim()}\" — that is what assigns the task");
                    await Task.Delay(500 + _rng.Next(150), ct);
                }
                if (_script.HailFirst || _script.SayPhrases.Count > 0)
                {
                    // WAIT FOR THE JOURNAL, don't guess at it. The old 800 ms beat was a guess and
                    // the log showed it losing: the first offer of EVERY cycle went unanswered and
                    // only the retry — by then seconds past the assignment — went through, at
                    // identical click speed. Speed was never the difference; state was.
                    Stats.State = "waiting for the task";
                    // Only when a phrase actually went out. Counting the pass regardless meant an
                    // alt-tab at the wrong instant — Say refuses while the game isn't focused —
                    // logged evidence about this game's logging that was never gathered, and two
                    // of those armed the shortcut for the rest of the run.
                    if (saidSomething) _phrasePasses++;
                    // The phrase assigns the task the moment it lands, so this is a safety net for a
                    // slow frame, not a schedule to keep. And if the watcher has heard NOTHING all
                    // run, waiting for a line it will never deliver is pure dead time.
                    int waitFor = Math.Clamp(_script.AssignWaitSeconds, 1, 30);
                    if (Volatile.Read(ref _linesSeen) == 0 && Stats.Cycles > 0) waitFor = 1;
                    // The line we watch for is the WIKI's wording, not a wording anyone has seen
                    // this game print. Hayden's journal shows the task assigned while the log for
                    // the same seconds carries buffs and chat and nothing else — so on this server
                    // the journal is probably updated silently. Once a run has read plenty of log
                    // and never once seen that line, stop paying for it every cycle.
                    // Gated on PASSES, not on line count. Counting log lines measured how busy the
                    // zone was: forty lines of buffs and General chat accumulate before the first
                    // phrase is even spoken, so the shortcut fired on cycle one and re-introduced
                    // the early-offer miss it was written to avoid.
                    if (!_sawAssignEver && _phrasePasses >= 2) waitFor = 1;
                    DateTime until = DateTime.UtcNow.AddSeconds(waitFor);
                    bool saw = false;
                    while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
                    {
                        if (new DateTime(Volatile.Read(ref _assignAtTicks), DateTimeKind.Utc) > cycleFrom)
                        { saw = true; break; }
                        await Task.Delay(150, ct);
                    }
                    if (saw)
                    {
                        if (_narrate) Log?.Invoke("· the task is in the journal — handing over now");
                        await Task.Delay(250, ct);            // let the journal settle before the offer
                    }
                    else if (_watcher is null)
                    {
                        if (_narrate) Log?.Invoke("· no log to read, so the task can't be confirmed — waited it out");
                    }
                    else if (Volatile.Read(ref _linesSeen) == 0)
                    {
                        if (_narrate)
                            Log?.Invoke($"⚠ nothing at all has been read from the log in {waitFor}s — either the "
                                      + "task was already assigned, or the log isn't being read. Offering anyway.");
                    }
                    else if (_narrate)
                    {
                        // A STEP, not a warning. The log is plainly alive — other lines are coming
                        // through — so the absence of this one is far more likely to mean the game
                        // never prints it than to mean anything went wrong. Colouring that amber
                        // sent Hayden looking for a fault in the one part that was working.
                        Log?.Invoke($"· no journal line in the log after {waitFor}s — this game may not print one. "
                                  + "The phrase was said, so the task should be assigned; offering now.");
                    }
                }

                // ---- the hand-ins, in order.
                //
                // A missed step is RETRIED IN PLACE, never by restarting the cycle. The NPC only
                // accepts the item its current quest stage calls for: once the Totem is in, the
                // Sha`rr refuses another Totem until the Orders go in. Restarting from step 1
                // after a step-2 hiccup would therefore offer an item the NPC is guaranteed to
                // reject — turning one slow server reply into a spurious "out of items" stop.
                bool cycleComplete = true;
                bool abort = false;                       // cancelled or focus gone — leave entirely
                int stepsDone = 0, stepsSkipped = 0;
                List<TurnInStep> steps = _script.Steps.ToList();
                for (int i = 0; i < steps.Count && !abort; i++)
                {
                    TurnInStep step = steps[i];
                    while (true)
                    {
                        if (ct.IsCancellationRequested) { cycleComplete = false; abort = true; break; }
                        if (!await WaitFocus(ct)) { cycleComplete = false; abort = true; break; }

                        bool? result = await HandOverAsync(step, ct);
                        if (result is null)
                        {
                            // NEVER retry silently — the first field test looked like "the bot did
                            // nothing" because this path said nothing. Focus blips are retryable
                            // (WaitFocus above blocks until the game is back); a missing pick or a
                            // vanished window is not, and looping on those is just being quiet
                            // about being broken.
                            gestureFails++;
                            Log?.Invoke($"⚠ couldn't complete the {step.Item} gesture: {_clickFailWhy} "
                                      + $"(attempt {gestureFails}).");
                            if (_clickFailWhy.Contains("isn't set") || _clickFailWhy.Contains("gone away")
                                || gestureFails >= 8)
                            {
                                Finish($"Stopped: {_clickFailWhy}. Re-pick the points on the card and run again.");
                                HumanizedMouse.MoveInstant(home.x, home.y);
                                return;
                            }
                            await Task.Delay(600, ct);
                            continue;                                  // same step again
                        }
                        gestureFails = 0;

                        if (result == true)
                        {
                            stepMisses[step] = 0;
                            Stats.HandIns++;
                            // A confirmed offer is ONE ITEM. For a quest that wants four of a
                            // thing, four offers = one completion — stamping the history on the
                            // first partial offer would mark quests "completed" that never were.
                            int toward = offersToward.TryGetValue(step, out int t) ? t + 1 : 1;
                            if (toward >= Math.Max(1, step.Qty))
                            {
                                QuestCompletions.Record(step.Quest);
                                toward = 0;
                            }
                            offersToward[step] = toward;
                            Log?.Invoke($"✔ {step.Item} accepted — {Stats.LastLine}");
                            stepsDone++;
                            await Task.Delay(1100 + _rng.Next(500), ct);
                            break;                            // next step
                        }

                        int misses = stepMisses.TryGetValue(step, out int m) ? m + 1 : 1;
                        stepMisses[step] = misses;
                        Stats.Misses++;
                        if (_emptyBagMiss)
                        {
                            // Nothing was clicked and nothing is on the cursor — the recovery
                            // click below would PICK AN ITEM UP over a bag, not put one down.
                            Log?.Invoke($"✖ {step.Item}: bag scan found none (miss {misses} of 2 for this item).");
                        }
                        else
                        {
                            if (Volatile.Read(ref _linesSeen) == 0)
                            {
                                // Nothing at all has been read since the run started. That is not a
                                // refused hand-in, it is a log we are not hearing — and saying "you
                                // are probably out of totems" to someone whose bag is full, whose
                                // items visibly left the bag, is the worst answer available.
                                Finish($"✖ Stopped: not a single line has been read from the EverQuest log since this "
                                     + "run began, so nothing can ever be confirmed — the clicks may well be working. "
                                     + "Check /log is ON in game, then press \"Find newest log\" on the Log Reader "
                                     + "page (a new character or a re-login writes to a NEW file).");
                                return;
                            }
                            Log?.Invoke($"✖ {step.Item}: nothing came back from the server within "
                                      + $"{_script.ConfirmSeconds}s (miss {misses} of 2 for this item).");
                            ReportWindow(step);
                            Recover(step);
                        }
                        if (misses >= 2)
                        {
                            // MOVE ON, don't stop. This used to end the run, and that was the bug
                            // Hayden hit twice: the Sha`rr takes ONE item per quest stage, so if the
                            // stage this step belongs to is already satisfied — the quest was picked
                            // up earlier, the totem already handed in — then this item can never be
                            // accepted, and the item that CAN be is the next step down the list. The
                            // runner sat on step 1 offering a totem the NPC had no use for while the
                            // Orders it actually wanted were three inches away in the same bag, and
                            // called the whole thing "you're out of totems".
                            stepsSkipped++;
                            cycleComplete = false;
                            stepMisses[step] = 0;             // next cycle gets its own two attempts
                            Log?.Invoke(i + 1 < steps.Count
                                ? $"↷ giving up on {step.Item} for now and trying {steps[i + 1].Item} — the NPC "
                                  + "only takes the item his current quest stage asks for, so if this stage is "
                                  + "already done, the next item is the one he wants."
                                : $"↷ {step.Item} was refused twice and it's the last item in the cycle.");
                            break;                            // next STEP, not the end of the run
                        }
                        await Task.Delay(1500, ct);           // retry THIS step
                    }
                }

                // Nothing at all got through this pass. Now — and only now — is stopping right:
                // every item in the cycle has been offered twice and refused, so retrying the same
                // list forever would be a loop, not persistence.
                if (!abort && stepsDone == 0 && stepsSkipped > 0)
                {
                    Finish($"Stopped after {Stats.Cycles} cycle(s) / {Stats.HandIns} hand-in(s): every item in "
                         + "the cycle was offered twice and the server acknowledged none of them. The log IS "
                         + (Volatile.Read(ref _linesSeen) > 0 ? "being read (other lines came through), so " : "quiet, so ")
                         + "this is the NPC declining, not a missed click: either you're out of these items, or "
                         + "the quest you're holding is at a stage that wants none of them. Check the journal, "
                         + "hand one in by hand to see what he takes, then run again.");
                    HumanizedMouse.MoveInstant(home.x, home.y);
                    return;
                }

                // A PASS is one trip round the list; a CYCLE is a pass where every step confirmed.
                // They used to be the same thing, and once a step could be skipped they stopped
                // being: a script whose second item is exhausted (or whose name doesn't match the
                // server's wording) would hand item one over forever, never counting a cycle, so
                // "3 cycles" never ended, the pacing delay never ran, and the first-cycle narration
                // never hushed — a run that quietly became infinite and chatty.
                _narrate = false;                         // the first pass told the story; hush now
                if (cycleComplete)
                {
                    partialRun = 0;
                    Stats.Cycles++;
                    _script.LifetimeCompleted++;
                    Log?.Invoke($"— cycle {Stats.Cycles} complete —");
                }
                else if (stepsDone > 0)
                {
                    // Something worked and something didn't. Worth another go — but not forever:
                    // a step that can NEVER succeed would otherwise be offered twice a pass, for
                    // the rest of the night, burning a real item each time.
                    partialRun++;
                    if (partialRun >= 3)
                    {
                        Finish($"Stopped after {Stats.Cycles} full cycle(s) / {Stats.HandIns} hand-in(s): three "
                             + "passes in a row got part of the way and then stuck on the same item. Something "
                             + "about that step is wrong rather than unlucky — most likely the item's name on "
                             + "the card isn't spelled the way the server spells it (so the hand-in works and "
                             + "goes uncounted), or its slot pick needs re-taking. The lines above say which "
                             + "item and what the server said.");
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        return;
                    }
                }
                await Task.Delay(900 + _rng.Next(500), ct);
            }

            HumanizedMouse.MoveInstant(home.x, home.y);
            Finish($"Stopped — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s) confirmed this run.");
        }
        catch (OperationCanceledException)
        {
            try { HumanizedMouse.MoveInstant(home.x, home.y); } catch { }
            Finish($"Stopped — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s) confirmed this run.");
        }
        catch (Exception ex)
        {
            Diag.BotLog.Log("quest", "runner error: " + ex);
            Finish("Quest Runner error: " + ex.Message);
        }
    }
}
