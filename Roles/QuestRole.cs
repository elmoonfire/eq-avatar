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
    /// <summary>The step currently in flight, so the log matcher can name-check its item.</summary>
    private volatile TurnInStep? _inFlight;
    /// <summary>Set when the last false from HandOverAsync meant "no icon in the bag" — a miss
    /// where NOTHING was clicked and nothing is on the cursor, so the recovery click that shakes a
    /// stuck item back into its slot must NOT run: over a bag it would pick an item UP.</summary>
    private volatile bool _emptyBagMiss;
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
        if (line.Length == 0 || !_listening) return;
        TurnInStep? step = _inFlight;
        if (step is null) return;

        string l = line.ToLowerInvariant();
        string item = step.Item.ToLowerInvariant();
        string quest = step.Quest.ToLowerInvariant();

        // The only line that names THIS hand-in. When the item is known, nothing else counts —
        // see the note below.
        if ((l.Contains("you offered") || l.Contains("you have given"))
            && (item.Length == 0 || l.Contains(item)))
        {
            _offered = true;
            Stats.LastLine = line.Trim();
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

        if (!ClickAt(slot, 260, $"the bag slot for {step.Item}")) return null;   // pick the item up

        // From here the item is ON THE CURSOR. Every early exit has to put it back in ITS OWN slot
        // first: the cycle restarts at step 1, and step 1's first act is to click step 1's slot —
        // which, with step 2's item still held, drops it into the wrong bag square and every
        // pick-up after that grabs the wrong thing.
        bool holding = true;
        bool? Drop(bool? result) { if (holding) { ClickAt(slot, 260, "the bag slot (returning the item)"); holding = false; } return result; }

        // Where the NPC actually stands. The nameplate follows him; the picked point doesn't.
        ScreenPoint npc = _script.Layout.Npc;
        if (_script.SmartFind && _script.NpcAnchorLearned)
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

        if (!ClickAt(npc, 620, "the NPC")) return Drop(null);   // drop it on the NPC → give window

        // Arm the listener HERE, immediately before the button that commits the trade — not at the
        // top of the cycle. Everything above takes seconds, and a confirmation armed that early can
        // be satisfied by the tail of the PREVIOUS hand-in.
        _inFlight = step;
        _offered = _advanced = false;
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
                if (!_script.NpcAnchorLearned) unarmed.Add("the NPC nameplate isn't anchored (re-pick him while targeted)");
                Log?.Invoke(unarmed.Count == 0
                    ? "smart find ARMED: items by icon in the bag area, the NPC by nameplate."
                    : "⚠ smart find is ON but partly unarmed — " + string.Join("; ", unarmed)
                      + ". Unarmed parts fall back to the fixed picks, which is exactly what failed last time.");
                if (oldSig.Count > 0)
                    Log?.Invoke("· " + string.Join(", ", oldSig) + " still use(s) the old grid scan — "
                              + "re-pick the slot once and the precise sliding search takes over.");
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
                foreach (string phrase in _script.SayPhrases)
                {
                    if (ct.IsCancellationRequested || phrase.Trim().Length == 0) continue;
                    if (!Say("/say " + phrase.Trim())) break;
                    await Task.Delay(900 + _rng.Next(300), ct);
                }
                // Give the server a beat to put the task in the journal before the first offer —
                // an offer that beats the assignment is refused and costs a retry.
                if (_script.SayPhrases.Count > 0) await Task.Delay(800, ct);

                // ---- the hand-ins, in order.
                //
                // A missed step is RETRIED IN PLACE, never by restarting the cycle. The NPC only
                // accepts the item its current quest stage calls for: once the Totem is in, the
                // Sha`rr refuses another Totem until the Orders go in. Restarting from step 1
                // after a step-2 hiccup would therefore offer an item the NPC is guaranteed to
                // reject — turning one slow server reply into a spurious "out of items" stop.
                bool cycleComplete = true;
                List<TurnInStep> steps = _script.Steps.ToList();
                for (int i = 0; i < steps.Count && cycleComplete; i++)
                {
                    TurnInStep step = steps[i];
                    while (true)
                    {
                        if (ct.IsCancellationRequested) { cycleComplete = false; break; }
                        if (!await WaitFocus(ct)) { cycleComplete = false; break; }

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
                            Log?.Invoke($"✖ {step.Item}: nothing came back from the server within "
                                      + $"{_script.ConfirmSeconds}s (miss {misses} of 2 for this item).");
                            // Drop whatever might still be stuck to the cursor before trying again.
                            ClickAt(step.Slot, 300);
                        }
                        if (misses >= 2)
                        {
                            Finish($"Stopped after {Stats.Cycles} cycle(s) / {Stats.HandIns} hand-in(s): this item "
                                 + $"went unanswered twice. Most likely you're out of {step.Item} — if you're not, "
                                 + "re-pick the NPC and GIVE points and check the NPC is in reach.");
                            HumanizedMouse.MoveInstant(home.x, home.y);
                            return;
                        }
                        await Task.Delay(1500, ct);           // retry THIS step
                    }
                }

                if (cycleComplete)
                {
                    _narrate = false;                     // the first cycle told the story; hush now
                    Stats.Cycles++;
                    _script.LifetimeCompleted++;
                    Log?.Invoke($"— cycle {Stats.Cycles} complete —");
                    await Task.Delay(900 + _rng.Next(500), ct);
                }
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
