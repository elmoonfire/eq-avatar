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
/// A step that clicks perfectly and confirms nothing is a FAILED step, and the runner moves
/// straight ON TO THE NEXT ITEM rather than trying again: this NPC only accepts what his
/// current quest stage asks for, so a step that can't be satisfied usually means the stage it
/// belongs to is already done — and the item he actually wants is the next one in the list. The
/// run stops only when a whole pass gets nothing through, or when three passes in a row stick on
/// the same item.
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
    /// where the scan gave up BEFORE any click, so nothing in THAT attempt can have picked an item
    /// up — though an earlier one still might have; see WarnPossiblyHeld.</summary>
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
    /// <summary>An offer of the RIGHT item to the WRONG creature. Not a hand-in; a donation.</summary>
    private string _wrongNpc = "";
    /// <summary>This window saw an offer naming ANOTHER step of this script — the signature of a
    /// trade window committing a backlog, which means its consequence lines are about that hand-in
    /// and not this one. Sticky for the window: a later wrong offer must not erase it.</summary>
    private bool _spilled;
    /// <summary>Every step's item name, lower-cased, snapshotted at construction. The card can add
    /// and remove steps while a run is going; the log thread must not walk that list.</summary>
    private readonly string[] _stepNames;
    /// <summary>Offer lines that matched the in-flight item inside ONE confirmation window. More
    /// than one means the trade window had been accumulating and committed a pile at once — which
    /// is several items gone for a single counted hand-in, and the reason retries were removed.</summary>
    private int _offersThisWindow;
    /// <summary>The user's own "that worked" phrases, lower-cased once at construction.</summary>
    private readonly List<string> _successLines;
    /// <summary>Set the instant an offer is acknowledged, so the NEXT line can be captured.</summary>
    private volatile bool _grabNext;
    /// <summary>The line that followed a confirmed hand-in — a ready-made success phrase.</summary>
    private volatile string? _suggestedSuccess;
    private bool _suggestedShown;
    /// <summary>The hand-in that just "succeeded" was assumed, not acknowledged. Kept apart so the
    /// durable completion history never records something nobody saw happen.</summary>
    private bool _assumedThisStep;
    private bool _assumedAnyThisPass;

    /// <summary>"confirmed", or nothing at all when confirmation is switched off. A tally the
    /// server never acknowledged must not be reported as one it did.</summary>
    private string ConfirmTail => _script.WaitForConfirm
        ? " confirmed this run."
        : " this run — assumed, since confirmation was switched off.";
    private readonly object _windowGate = new();
    /// <summary>Whether the log has EVER produced an assignment line this run. EQ Legends may
    /// simply not print one — the quest appears in the journal either way — and waiting every
    /// cycle for a line that is never coming is dead time plus a warning that reads like a fault.</summary>
    private volatile bool _sawAssignEver;
    /// <summary>Whether the log has shown ANY hand-over acknowledgement this run.</summary>
    private volatile bool _sawAnyOffer;
    /// <summary>
    /// The trade window has CLOSED — "You complete the trade with &lt;npc&gt;".
    ///
    /// This is the line that ends the guessing. It is printed whether or not the NPC kept anything,
    /// so seen WITHOUT an offer line it means the item went in and came straight back out: the
    /// trade is over, nothing was accepted, and there is nothing left to wait for. Every failed
    /// hand-in in Hayden's logs sat out the full confirmation window after this line had already
    /// gone past.
    /// </summary>
    private long _tradeClosedAt;
    /// <summary>The NPC's own words when he hands something back — "I have no need for this,
    /// Bryari. You can have it back." Worth quoting: it is the difference between "the click
    /// missed" and "he doesn't want this right now", which no amount of clicking will fix.</summary>
    private string _refusal = "";
    /// <summary>The last attempt ended with the trade closing and nothing accepted.</summary>
    private bool _handedBack;
    /// <summary>Items already warned about for a borderline icon match. Once each, per run.</summary>
    private readonly HashSet<string> _marginalSaid = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The last scan's score, carried so a miss can QUOTE it. The "found …" line is hushed
    /// after the first pass, so telling someone to "check the match score above" pointed at a line
    /// that no longer existed — and the advice matters most when the score was unremarkable, which
    /// is exactly when the once-per-run ⚠ never fired.</summary>
    private string _lastMatch = "";
    /// <summary>How many times this run has said the trigger phrase and then waited for a journal
    /// line. Two passes with nothing is evidence about THIS GAME's logging; a busy zone's chatter
    /// is evidence about the zone.</summary>
    private int _phrasePasses;
    /// <summary>
    /// ONE attempt per item per pass. Retrying is not free here — it is the most expensive thing
    /// the runner can do.
    ///
    /// Hayden's game chat settles it. Three consecutive "You offered 1 Desecrated Kejaar Totem to
    /// The Kerran Sha`rr." lines, in a burst, at the moment a LATER hand-in went through — while
    /// the runner's own log for those same three attempts shows nothing but buff ticks. The offers
    /// were real; they just did not commit when they were made. Each attempt had dropped its item
    /// into the trade window and pressed a GIVE that did not land, so the window accumulated, and
    /// one GIVE that finally landed committed the lot.
    ///
    /// Which means every retry HANDS OVER ANOTHER ITEM. Three attempts at a totem is three totems
    /// gone for one quest step — on a 1,024-totem grind. And it explains the whole shape of the
    /// last four field logs: attempts that "failed" while items steadily left the bag.
    ///
    /// So: one attempt. Hayden's read from watching it — "the first attempt when an item is
    /// actually picked up and handed to the npc does work" — matches every confirmed hand-in in
    /// every log. If it doesn't confirm, the answer is the NEXT ITEM, not the same one again.
    /// </summary>
    private const int MaxStepMisses = 1;
    private int _finished;

    public QuestRole(QuestScript script, IInputSink sink, AppSettings settings,
                     Func<IntPtr> gameWindow, string? logPath)
    {
        _script = script;
        _sink = sink;
        _s = settings;
        _hwnd = gameWindow;
        _stepNames = script.Steps.Select(x => (x.Item ?? "").Trim().ToLowerInvariant())
                                 .Where(x => x.Length > 0).ToArray();
        _successLines = (script.SuccessLines ?? new List<string>())
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(x => x.Length >= 6)          // shorter than this and it would match half the log
            .ToList();
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
        // Run-wide, OUTSIDE the listening window: "has this NPC taken a single thing from us all
        // run" is a different question from "did he take the one we just offered", and the answer
        // changes which advice the ending should give.
        if (line.IndexOf("you offered", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("you have given", StringComparison.OrdinalIgnoreCase) >= 0)
            _sawAnyOffer = true;

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

        // THE TRADE IS OVER. Recorded before anything else so it can't be missed, and timestamped
        // rather than acted on immediately: within one poll the log's lines arrive back to back, so
        // this line can be delivered a hair BEFORE the offer line it accompanies. The confirm loop
        // gives it a short grace period rather than calling a miss on ordering.
        if (l.Contains("you complete the trade with"))
            Volatile.Write(ref _tradeClosedAt, DateTime.UtcNow.Ticks);
        // The refusal itself. EQ Legends phrases it as the NPC talking, and the two halves are
        // stable across NPCs even where the wording around them isn't.
        // Scoped to NPC SPEECH. "you can have it back" on its own is a phrase a person can type in
        // a tell, and a refusal recorded off group chat would suppress the wrong-item and
        // wrong-creature reports below and assert that nothing was lost.
        if (l.Contains(" says, '")
            && (l.Contains("i have no need for this") || l.Contains("you can have it back")))
            lock (_windowGate) _refusal = line.Trim();

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
                // WHO took it matters as much as what. The fixed NPC pick is a point on the
                // screen, so anything that wanders through that point gets the click — Hayden's
                // chat log has "You offered 1 Heretic Insurrection Orders to a patrolling tiger"
                // sitting between two hand-ins the runner counted as successes.
                //
                // But this test FAILS OPEN, and that is the whole design. The quest NPC's name is
                // scraped off a wiki infobox: it can carry a curly apostrophe where the server
                // writes a backtick, or be a piped display name ("the Sha`rr"). A test that
                // required a positive match on that string would reject REAL hand-ins for a
                // punctuation mismatch, and then accuse the user of feeding items to wildlife
                // while quoting a line naming the right NPC. So the only thing that counts as a
                // donation is a POSITIVE identification of something else: EQ names creatures
                // "a patrolling tiger", "an angry bear" — lowercase, with an article. Real NPC
                // names never look like that.
                if (LooksLikeACreature(line))
                {
                    lock (_windowGate) _wrongNpc = line.Trim();
                    return;
                }
                // Offer lines only. The confirmation matcher deliberately accepts either wording,
                // because nobody has established which this server prints — but if ONE hand-in
                // printed both, counting both would tell the user two items had vanished and send
                // them off to re-pick a GIVE button that was working perfectly.
                if (l.Contains("you offered")) Interlocked.Increment(ref _offersThisWindow);
                _grabNext = true;               // the very next line is the quest's own success line
                _offered = true;
                Stats.LastLine = line.Trim();
                return;
            }
            // SOMETHING was handed over and it was not the item we meant. That single line splits
            // the failure space in half: the gesture worked and the bag search picked the wrong
            // icon. Without it, giving away the wrong item and clicking an empty square produce
            // word-for-word the same report.
            // Decided HERE, once, and remembered — not re-derived later from _wrongOffer, which is
            // last-writer-wins: a second wrong offer would overwrite the first and erase the fact
            // that a spill had happened. Read off a snapshot taken in the constructor rather than
            // by walking _script.Steps, because this runs on the log-watcher thread while the card
            // can remove a step from the UI thread — and a "collection was modified" throw here
            // skips the watcher's offset advance, so the whole batch replays and every count in it
            // doubles.
            lock (_windowGate)
            {
                _wrongOffer = line.Trim();
                foreach (string other in _stepNames)
                    if (!string.Equals(other, item, StringComparison.Ordinal) && l.Contains(other))
                    { _spilled = true; break; }
            }
            return;
        }

        // LEARN the quest's success line rather than asking for it blind. The line immediately
        // after an accepted offer is the quest-specific consequence — "You validated the Kerran
        // Sha`rr's concerns…" — which is exactly what belongs in the box on the card, and which no
        // wiki scrape could ever have supplied. Faction and experience lines are skipped: both are
        // word-for-word identical for every turn-in on the island, so neither can tell one hand-in
        // from the one before it.
        if (_grabNext)
        {
            _grabNext = false;
            string body = StripStamp(line);
            // No digits. Every EQ line carries a 27-character timestamp, so testing the RAW line's
            // length rejected nothing at all — and the lines that slipped through were combat and
            // chat spam ("Grimfang hits a patrolling tiger for 42 points of damage"), which is the
            // worst possible thing to suggest: pasted into the card it is true several times a
            // second and every hand-in "confirms" instantly for the rest of the night. A quest's
            // own success line is prose; a number in it means it is about something else.
            if (!l.Contains("you offered") && !l.Contains("you have given")
                && !l.Contains("you have been given")
                && !l.Contains("faction standing") && !l.Contains("you gain experience")
                && body.Length >= 20 && !body.Any(char.IsDigit)
                && body.StartsWith("you", StringComparison.OrdinalIgnoreCase))
                _suggestedSuccess = body;
        }

        // THE QUEST'S OWN SUCCESS LINE. Hayden's chat prints one immediately after every accepted
        // hand-in — "You validated the Kerran Sha`rr's concerns…" for the totem, "You've dealt a
        // blow to the Heretics…" for the orders — and they are per turn-in and unmistakable. No
        // scrape can know them, so they are typed into the card, and matching one ends the wait at
        // once instead of sitting out a timeout for a hand-in that plainly worked.
        // GUARD FIRST. Everything below is weaker evidence than "You offered 1 <item>", and it is
        // only safe while this window is uncontaminated. Two things contaminate it, and both are
        // observed in the field:
        //   • an offer line naming a DIFFERENT item — which is what a trade window committing the
        //     previous step's backlog looks like. Its consequence lines (the reward, the quest's
        //     own success line) land in THIS window and would confirm a hand-in that gave nothing.
        //   • a donation to a passing creature. LooksLikeACreature refuses to confirm it, but that
        //     refusal is worth nothing if the next line confirms the step anyway — the ✖ that
        //     whole design exists to print is only reached on a miss.
        // Narrowly. "An offer naming something other than this step's item" has two causes with
        // opposite meanings: the previous step's backlog committing (a real spill — contaminate),
        // or this very hand-in succeeding under a name the wiki spelled differently (in which case
        // contaminating would disable the success lines for exactly the people they were added
        // for, on every single hand-in). So it only counts as a spill if the line names ANOTHER
        // STEP of this script.
        bool spilled, mob;
        lock (_windowGate) { spilled = _spilled; mob = _wrongNpc.Length > 0; }
        if (spilled || mob) return;

        foreach (string phrase in _successLines)
            if (l.Contains(phrase))
            {
                _advanced = true;
                Stats.LastLine = line.Trim();
                return;
            }

        // The reward. "You have been given: <thing>" is printed by the hand-in that earned it and
        // names a different thing for each step of a cycle, so inside the few seconds this listener
        // is armed it is evidence about THIS hand-in. (The faction line that arrives alongside it is
        // deliberately NOT used: its text is identical for every turn-in on the island, so it can't
        // distinguish the hand-in it belongs to from the one before.)
        if (l.Contains("you have been given:"))
        {
            _advanced = true;
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
        if (questLine && quest.Length > 0 && l.Contains(quest))
        {
            _advanced = true;
            Stats.LastLine = line.Trim();
        }
    }

    /// <summary>
    /// Does this offer line say the item went to a generic creature rather than to a named NPC?
    ///
    /// EQ's naming convention does the work: mobs are "a patrolling tiger", "an angry bear" —
    /// lowercase, indefinite article — while quest NPCs are proper names, "The Kerran Sha`rr".
    /// Testing for the mob shape rather than for the NPC's exact spelling means a wiki-scraped
    /// name that differs by one apostrophe costs nothing, where the reverse test would have
    /// refused every real hand-in on this quest.
    /// </summary>
    private bool LooksLikeACreature(string line)
    {
        int to = line.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (to < 0) return false;
        string tail = line[(to + 4)..].TrimEnd('.', ' ', '\r', '\n');
        // If the tail IS the NPC we came for, it is a hand-in whatever it looks like. EverQuest
        // does name interactable NPCs "a shady vendor", and without this line such a quest would
        // have every real hand-in rejected as a donation — the exact failure this test was
        // rewritten to avoid, coming back through the other door. Suppressing a rejection can only
        // ever be safe; that asymmetry is the whole design.
        string npc = _script.Npc.Trim();
        if (npc.Length > 0 && tail.StartsWith(npc, StringComparison.OrdinalIgnoreCase))
            return false;
        return tail.StartsWith("a ", StringComparison.Ordinal)
            || tail.StartsWith("an ", StringComparison.Ordinal);
    }

    /// <summary>Drop EQ's "[Fri Aug 14 05:22:48 2026] " stamp, so a suggested phrase is one the
    /// user can paste straight into the card without it matching only that one second.</summary>
    private static string StripStamp(string line)
    {
        int close = line.IndexOf(']');
        return close > 0 && line.StartsWith("[") ? line[(close + 1)..].Trim() : line.Trim();
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
    private bool OpenBags(string why, bool quiet = false)
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

        // Quiet on the routine per-pass press: it happens 1,024 times on a full grind, every one a
        // blocking hop to the UI thread immediately before a click sequence, and every one repaints
        // the card's single-line status over whatever warning was there. A press that FAILS is
        // never quiet — that one changes what the next empty scan means.
        if (!quiet || !sent)
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
        _assumedThisStep = false;
        _emptyBagMiss = false;
        // THIS step's score, or nothing at all — never the last step's. A step with no icon
        // signature, or one whose screen grab failed, skips the scan entirely, and a stale value
        // here would be quoted back as though it described the item just handed over.
        _lastMatch = "";

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
            double accept = sliding ? Math.Clamp(_script.IconTolerance, 8, 60) : QuestFind.IconAcceptDistance;
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
                // A match within a few points of the limit is a guess, and a guess picks up the
                // wrong item — Hayden's run offered a Bone-clasped Girdle that scored 35 against a
                // ceiling of 35. Said out loud every time, not just while narrating, because the
                // consequence (an item handed to an NPC) is not something to find out about later.
                // ONCE per item per run, and amber. A margin can't tell a genuinely mediocre
                // signature from a wrong item — Hayden's real Orders score 33 against a limit of
                // 35, the Bone-clasped Girdle it grabbed scored 35 — so this can only ever say
                // "look at this", not "this is wrong". Said every cycle it would be hundreds of
                // identical blocking marshals; said once it is a fact worth having.
                _lastMatch = $"{hit.Dist:0} of {accept:0} allowed";
                bool marginal = sliding && hit.Dist > accept - 3 && _marginalSaid.Add(step.Item);
                if (_narrate)
                    Log?.Invoke(sliding
                        ? $"· found {step.Item} at {hit.X * 100:0.0}%, {hit.Y * 100:0.0}% of the window "
                          + $"(match {hit.Dist:0} of {accept:0} allowed)"
                        : $"· found {step.Item} in bag cell {hit.Row + 1},{hit.Col + 1} (match {hit.Dist:0})");
                if (marginal)
                    Log?.Invoke($"⚠ {step.Item} only matched at {hit.Dist:0} against a limit of {accept:0} — that is "
                              + "close enough to the edge to be a different item wearing a similar icon. If she starts "
                              + "handing over the wrong thing, re-pick this item's slot with a tight box round the "
                              + "icon. (Said once per item per run.)");
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

        // From here the item is ON THE CURSOR — as far as anything here can tell. (ClickAt returns
        // true for "I moved the mouse and clicked", not for "an item was under it", so on an old
        // grid-scan signature that "found" an empty square this is optimistic. It is still the best
        // available reading, and it only ever drives a warning.) Every early exit from here has to
        // put it back in ITS OWN slot first: the cycle restarts at step 1, and step 1's first act
        // is to click step 1's slot — which, with step 2's item still held, drops it into the wrong
        // bag square and every pick-up after that grabs the wrong thing.
        _maybeHolding = true;
        bool holding = true;
        bool? Drop(bool? result)
        {
            if (holding)
            {
                // Its result matters: a refused click (focus lost) means the item is STILL held,
                // which is exactly the state the warning exists for.
                if (ClickAt(slot, 260, "the bag slot (returning the item)")) _maybeHolding = false;
                holding = false;
            }
            return result;
        }

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
        // THE NPC CLICK is what takes the item off the cursor — PROBABLY. Same caveat as every other
        // click here: ClickAt reports that the mouse moved and clicked, not that the NPC was under
        // it, so a stale NPC pick leaves the item in hand and this line is wrong. It is still the
        // better default. Leaving `holding` true until after GIVE meant that when GIVE was refused
        // — focus lost between the two, the ordinary case — Drop() ran with an EMPTY cursor and its
        // "put it back" click picked a fresh copy UP, then reported success and cleared the held
        // flag: an item genuinely on the cursor with every signal saying otherwise. Being wrong this
        // way costs a stranded item that _maybeHolding still reports; being wrong the other way
        // silently created one and hid it.
        holding = false;

        // Arm the listener HERE, immediately before the button that commits the trade — not at the
        // top of the cycle. Everything above takes seconds, and a confirmation armed that early can
        // be satisfied by the tail of the PREVIOUS hand-in.
        _inFlight = step;
        _offered = _advanced = false;
        _handedBack = false;
        _grabNext = false;              // never let a previous window's capture spill into this one
        lock (_windowGate) { _windowLines.Clear(); _wrongOffer = ""; _wrongNpc = ""; _spilled = false; _refusal = ""; }
        Volatile.Write(ref _tradeClosedAt, 0);
        Interlocked.Exchange(ref _offersThisWindow, 0);
        _listening = true;

        if (!ClickAt(_script.Layout.GiveButton, 500, "the GIVE button")) { _listening = false; _inFlight = null; return Drop(null); }
        if (_script.Layout.Confirm.Set) ClickAt(_script.Layout.Confirm, 400);

        // NOT WAITING AT ALL is a supported answer, and on a 1,024-cycle grind it is often the
        // right one: the clicking takes about five seconds and a confirmation that doesn't land
        // costs the confirm window again, per item. The cost is honest and stated — an assumed
        // hand-in is not a counted one, so a run in this mode can never notice it has stopped
        // working. The listener stays armed through the beat anyway, so a fast acknowledgement is
        // still USED; this only stops us waiting for a slow one.
        if (!_script.WaitForConfirm)
        {
            Stats.State = "handed over";
            await Task.Delay(600 + _rng.Next(150), ct);
            bool heard = _offered || _advanced;
            _listening = false;
            _inFlight = null;
            if (heard) _maybeHolding = false;
            // ASSUMED, not confirmed — and the difference is kept. The run carries on (that is the
            // point of the switch), but the permanent completion history only ever records hand-ins
            // the server actually acknowledged. Writing an assumption into a file with no UI to
            // correct it would overstate a 1,024-item grind by however long the mode was left on.
            _assumedThisStep = !heard;
            if (!heard && _narrate)
                Log?.Invoke("· not waiting for the server (confirmation is off) — assuming that worked and moving on. "
                          + "Assumed hand-ins don't go into the completion history.");
            return true;
        }

        Stats.State = "waiting for the server";
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_script.ConfirmSeconds, 2, 60));
        bool confirmed = false;
        DateTime sawClosed = default;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_offered || _advanced) { confirmed = true; break; }
            // The trade window shut and nothing was acknowledged. Half a second of grace for an
            // offer line delivered in the same batch a fraction later, then stop: there is nothing
            // more coming, and waiting the rest of the window out is the single biggest waste in
            // the whole cycle.
            // Grace measured from when THIS LOOP first sees the closure, not from when the line was
            // delivered. Delivery time is useless here: the GIVE click's own settle sleeps run to
            // 800ms (1500 with a confirm pick), all of it before the first iteration, so a grace
            // measured from delivery was routinely spent before anyone looked. And the case that
            // actually needs grace is the acknowledgement arriving in the NEXT poll — the tailer
            // polls every 500ms — not the same batch, which is drained synchronously and would win
            // the `_offered` check above with no grace at all.
            if (Volatile.Read(ref _tradeClosedAt) != 0)
            {
                if (sawClosed == default) sawClosed = DateTime.UtcNow;
                else if ((DateTime.UtcNow - sawClosed).TotalMilliseconds > 700)
                {
                    _handedBack = true;
                    // The best cursor evidence there is: the item went into a trade window and came
                    // back out to the bag. Nothing else that clears this flag is as certain.
                    _maybeHolding = false;
                    break;
                }
            }
            await Task.Delay(120, ct);
        }
        _listening = false;
        _inFlight = null;
        return confirmed;
    }


    /// <summary>
    /// After a failed offer the runner touches NOTHING, and this says why once.
    ///
    /// The item is in one of three places — on the cursor, in a trade window the server never
    /// answered, or already taken — and nothing available here can tell which. A bag click is a
    /// TOGGLE, so every "recovery" click means opposite things in different states. I shipped
    /// three variants before accepting that, and wrote a fourth that did not survive review:
    ///
    ///   • click the square the item came from (0.10.22). With an empty cursor it picks a copy UP,
    ///     which makes the next attempt's first click a DROP — after which the NPC click has
    ///     nothing in hand and GIVE has nothing to give. Every second attempt was arithmetically
    ///     impossible, which is exactly the pattern Hayden described from outside: "the bot just
    ///     tries turning in each item twice in a row".
    ///   • press Escape. Closed the BAGS whenever no trade window was open, after which a shut bag
    ///     reads as an empty one and the run reports being out of items.
    ///   • park it in a picked empty square. That square is then no longer empty, so the NEXT miss
    ///     picks the parked item straight back up — the same bug, permanently, from the second miss
    ///     on. Probing the square afterwards cannot save it: the probe runs with the mouse on that
    ///     exact square, so a held item sits under the pointer and reads as though it were in the
    ///     slot.
    ///   • click the item's own slot, but only when the scan found no copy anywhere. Sound until
    ///     the picked slot has gone stale and something else has moved into it, or the bags are
    ///     shut so the click lands on the 3D world — both of which this file documents elsewhere
    ///     as normal.
    ///
    /// Doing nothing is the only option that cannot make things worse, and it is honest about what
    /// it costs: if the item really is on the cursor, the NEXT PASS's attempt at that step spends
    /// itself putting it back down, and if it was the LAST copy the scan cannot see it at all —
    /// see WarnPossiblyHeld, which says so out loud rather than guessing.
    /// </summary>
    private void NoteCursorRisk()
    {
        if (_notedCursor) return;
        _notedCursor = true;
        Log?.Invoke("· not clicking the bag afterwards. Nothing here can tell whether the item is on the cursor, "
                  + "in a trade window, or already gone, and a bag click means the opposite thing in each — every "
                  + "version of that \"recovery\" I wrote broke the NEXT attempt. If one is stuck to the cursor "
                  + "and there are other copies in the bag, the next attempt puts it down by itself; if it was the "
                  + "last one, I'll say so rather than guess.");
    }

    private bool _notedCursor;
    /// <summary>An attempt clicked its way through the gesture and then failed, so the item may be
    /// stuck to the cursor. Cleared the moment something proves otherwise.</summary>
    private bool _maybeHolding;

    /// <summary>
    /// The scan found nothing AND an earlier attempt may have left the item on the cursor. Say so.
    ///
    /// This is a real blind spot, not a hypothetical one: the scan captures the BAG RECTANGLE, and
    /// an item on the cursor is drawn wherever the mouse is — which after a miss is the GIVE
    /// button, nowhere near the bags. So the LAST copy of an item, stuck to the cursor, is
    /// invisible to the scan, and the run would otherwise report "no Desecrated Kejaar Totem found
    /// in the bag area" while holding one.
    ///
    /// I wrote a click for this and talked myself into calling it provably safe: with no copy in
    /// the bag, clicking the item's own slot either puts a held item down or does nothing. The
    /// proof is wrong in three ordinary ways — the picked slot goes stale and something else moves
    /// into it (this file's own note: "totems migrate through the bag as each one is consumed"); a
    /// shut bag is indistinguishable from an empty one, so the click can land on the 3D world; and
    /// the scan only ruled out THIS item's signature, not everything else. Every one of those ends
    /// with a foreign item on the cursor and no way left to shed it. That is the same bug I have
    /// now shipped three variants of, so this variant does not get written either. It tells the
    /// user, who can see the cursor, and lets them decide.
    /// </summary>
    private void WarnPossiblyHeld()
    {
        // The FLAG is not cleared here. Only a confirmed hand-in or a successful put-back clears
        // it, because it is also what carries "clicks demonstrably happened" into later passes —
        // and in the very case this warns about, the last copy being the one on the cursor, the
        // scan can never find a copy again, so nothing could ever set it a second time. Consuming
        // it here meant one ⚠ scrolled past and then the run ENDED on "you're out of these items",
        // contradicting itself a pass later. A separate gate keeps the line to once.
        // Not in assumed mode: nothing there ever clears the flag, so this would fire on the first
        // empty scan of every run and then read as boilerplate — worse than silence in the one mode
        // with no other error signal.
        if (!_maybeHolding || _warnedHeld || !_script.WaitForConfirm) return;
        _warnedHeld = true;
        // No item name: the flag is set by whichever step last got through its pick-up click, which
        // may not be the step now in flight. Naming this one sends the user hunting the wrong icon.
        Log?.Invoke("⚠ note: an earlier attempt may have left an item stuck to the cursor, and an item on the "
                  + "cursor is invisible to the bag scan (it's drawn under the mouse, not in a slot). So \"none in "
                  + "the bags\" might mean \"the last one is in your hand\". Look at the cursor — if something is "
                  + "on it, click it into an empty bag square and run again. I won't click it back myself: after a "
                  + "miss nothing here can tell a held item from an empty cursor, and every version of that guess I "
                  + "have written broke the next attempt.");
    }

    private bool _warnedHeld;

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
        string wrong, badNpc;
        List<string> seen;
        lock (_windowGate) { wrong = _wrongOffer; badNpc = _wrongNpc; seen = new List<string>(_windowLines); }

        if (badNpc.Length > 0)
        {
            Log?.Invoke($"✖ THAT WENT TO THE WRONG CREATURE — \"{badNpc}\". The NPC pick is a fixed point on the "
                      + $"screen, so anything that walks through it gets the click, and {step.Item} has just been "
                      + "given away. Not counted as a hand-in. The click goes to a fixed point unless smart "
                      + "find can see his nameplate, so: stand where nothing patrols between you and him, and "
                      + $"re-pick the NPC while {_script.Npc} is TARGETED — that learns the nameplate anchor — "
                      + "and turn ON \"target by name\" as well. BOTH are needed: the anchor is what she reads, "
                      + "and that switch is what makes her read it. With either one off the click stays on a spot.");
            return;
        }

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
        // Only now. "The trade closed and nothing was acknowledged" is an ABSENCE, and the two
        // branches above are positive identifications — a named wrong recipient, a named wrong
        // item. Printing this first suppressed both and told the user nothing was lost while the
        // item was inside a patrolling tiger.
        string refused;
        lock (_windowGate) refused = _refusal;
        if (refused.Length > 0 || _handedBack)
        {
            Log?.Invoke(refused.Length > 0
                ? $"↩ he handed it straight back — \"{StripStamp(refused)}\". Nothing was lost. That is not a "
                  + "missed click: either the task isn't assigned right now (the phrase may not re-assign it until "
                  + "the journal's request timer is up, or he may need hailing first), or what was picked up wasn't "
                  + $"what I meant to pick up (it scored {(_lastMatch.Length > 0 ? _lastMatch : "unknown")} — "
                  + "anything near the limit is a guess)."
                : "↩ the trade closed and he kept nothing. Nothing was lost, and there was nothing more to wait for "
                  + "— either the task isn't assigned right now, or the item picked up wasn't the right one.");
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
            if (!_script.WaitForConfirm && _script.Repeat <= 0)
            {
                Finish("Can't start — \"wait for the server to confirm\" is OFF and repeat is 0 (\"until the items "
                     + "run out\"). Without confirmation nothing can ever tell that they HAVE run out, so that pair "
                     + "means clicking at an empty bag until you stop it. Set a cycle count, or turn confirmation "
                     + "back on.");
                return;
            }
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
            if ((_script.OpenBagsKey ?? "").Trim().Length == 0)
                Log?.Invoke("⚠ no open-bags key is set on this card, so I can't make sure your bags are open — and "
                          + "a SHUT bag looks exactly like an empty one to the scan, which is why a run can stop "
                          + "with \"you're out of items\" while your bag is full. Bind one in game (Hayden's is "
                          + "alt+b, set to OPEN rather than toggle) and type the same chord into \"open bags\" here.");
            if (await WaitFocus(ct)) OpenBags("starting the run");

            // Per step, and with MaxStepMisses at 1 it never exceeds 1 — kept because the count is
            // what the skip decision reads, so raising MaxStepMisses is a one-line change if a
            // reason to retry ever turns up. Keyed by the step object; the list is only edited from
            // the UI while the run is stopped.
            var stepMisses = new Dictionary<TurnInStep, int>();
            int gestureFails = 0;
            // Consecutive passes that got part way and then stuck. Reset by a complete cycle.
            int partialRun = 0;
            // Consecutive passes where NOTHING was confirmed. One attempt per item means a single
            // pass is thin evidence — the old three-attempts-per-item stop needed six unanswered
            // offers to conclude "nothing gets through" and this would have needed two, which is
            // ~24 seconds. Two fruitless passes is the same standard of proof at the new cadence.
            int fruitlessPasses = 0;
            // Items offered toward a step's Qty since its last recorded completion.
            var offersToward = new Dictionary<TurnInStep, int>();

            while (!ct.IsCancellationRequested)
            {
                if (_script.Repeat > 0 && Stats.Cycles >= _script.Repeat)
                { Finish($"Done — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s){ConfirmTail}"); return; }

                if (!await WaitFocus(ct)) break;

                // Every pass, not just the first. A bag that was open at the start can be shut by
                // hand fifty cycles in, and the key Hayden bound is an OPEN, not a toggle — so
                // pressing it when they are already open costs one keystroke and nothing else.
                OpenBags("top of the cycle", quiet: !_narrate);

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
                // True while every step that gave up did so because the SCAN found nothing — the
                // ordinary "you're out of items" ending, where no click ever happened.
                bool scanFoundNothing = true;
                int refusedSteps = 0;
                _assumedAnyThisPass = false;
                List<TurnInStep> steps = _script.Steps.ToList();
                for (int i = 0; i < steps.Count && !abort; i++)
                {
                    TurnInStep step = steps[i];
                    // Per STEP, not per miss: refusedSteps is compared against stepsSkipped, and
                    // the two are only 1:1 while MaxStepMisses is 1. Counting at the give-up site
                    // keeps them in step whatever that constant becomes.
                    bool refusedThisStep = false;
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
                            scanFoundNothing = false;     // clicks happened; this was not an empty bag
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
                            // Read the burst FIRST. When the trade window commits a pile, that many
                            // items really did go, and a quest wanting four of something is
                            // four-fifths done, not one-quarter — counting one would stamp the
                            // completion late by exactly the number of items already lost.
                            int burst = Interlocked.Exchange(ref _offersThisWindow, 0);
                            // CLAMPED before it touches the durable history. The burst is counted
                            // over a window of live zone log, and questcompletions.json is
                            // permanent with no UI to correct it — one stray line must not be able
                            // to overstate progress on a 1,024-item grind. The ⚠ below still reports
                            // the raw number, because that is a diagnosis, not a tally.
                            // ONLY confirmed offers move this counter. Mixing assumed ones in and
                            // then deciding at the boundary got it wrong in both directions: three
                            // assumptions plus one real offer recorded a completion that never
                            // happened, and three real ones plus an assumption threw them away.
                            // An assumption isn't partial progress, it is no evidence at all.
                            int credit = Math.Clamp(burst, 1, 4);
                            int toward = offersToward.TryGetValue(step, out int t) ? t : 0;
                            int need = Math.Max(1, step.Qty);
                            if (!_assumedThisStep)
                            {
                                toward += credit;
                                while (toward >= need)
                                {
                                    QuestCompletions.Record(step.Quest);
                                    toward -= need;       // carry the remainder, don't discard it
                                }
                            }
                            offersToward[step] = toward;
                            Log?.Invoke(_assumedThisStep
                                ? $"✔ {step.Item} handed over (assumed — nothing confirmed it)"
                                : $"✔ {step.Item} accepted — {Stats.LastLine}");
                            if (!_suggestedShown && _successLines.Count == 0 && _suggestedSuccess is { Length: > 0 } sug)
                            {
                                _suggestedShown = true;
                                Log?.Invoke($"· tip: the line right after that hand-in was \"{sug}\". Paste "
                                          + "it into \"also count as success\" on this card and the runner stops "
                                          + "waiting the moment it sees it — which is most of the time a cycle costs.");
                            }
                            if (burst > 1)
                                Log?.Invoke($"⚠ the server took {burst} × {(step.Item.Length > 0 ? step.Item : "that item")} in that one moment, not one. "
                                          + "Earlier offers had gone into a trade window that hadn't committed yet, "
                                          + "and this GIVE committed the lot. Counting it as ONE hand-in, but "
                                          + $"{burst} left your bag. If this keeps happening, re-pick the GIVE "
                                          + "button — a GIVE press that doesn't land is what leaves items waiting.");
                            if (_assumedThisStep) _assumedAnyThisPass = true;
                            // Only a REAL acknowledgement proves the cursor is clear. Clearing it on
                            // an assumed hand-in put the stuck-cursor warning permanently out of
                            // reach in the one mode that can't otherwise notice anything is wrong.
                            if (!_assumedThisStep) _maybeHolding = false;
                            stepsDone++;
                            await Task.Delay(700 + _rng.Next(250), ct);   // ~1s, Hayden's measure
                            break;                            // next step
                        }

                        int misses = stepMisses.TryGetValue(step, out int m) ? m + 1 : 1;
                        stepMisses[step] = misses;
                        Stats.Misses++;
                        if (!_emptyBagMiss) scanFoundNothing = false;
                        if (_emptyBagMiss)
                        {
                            Log?.Invoke($"✖ {step.Item}: bag scan found none.");
                            // "None in the bag" does not mean "none anywhere" while an item may be
                            // on the cursor — it is drawn under the mouse, which is parked over the
                            // GIVE button, not the bags. So this is not the ordinary "you're out of
                            // items" ending, whatever the scan says, and the ending must not claim
                            // it is.
                            // Only while the server has never acknowledged anything. If hand-overs
                            // HAVE registered, the items really are going somewhere and "you're out
                            // of them" is the accurate half — suppressing it would send someone to
                            // re-pick an NPC that was working.
                            if (_maybeHolding && !_sawAnyOffer && Stats.HandIns == 0) scanFoundNothing = false;
                            WarnPossiblyHeld();
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
                            // Same precedence ReportWindow uses. "He didn't take it / nothing was
                            // lost" is an ABSENCE, and it must never outrank a named wrong item or
                            // a named wrong recipient — fixing that in ReportWindow alone left the
                            // identical false claim in the headline and, worse, in the Finish
                            // string, which is the one that stays on the card after the console has
                            // scrolled.
                            bool refusedNow, positive;
                            lock (_windowGate)
                            {
                                positive = _wrongOffer.Length > 0 || _wrongNpc.Length > 0;
                                refusedNow = !positive && _refusal.Length > 0;
                            }
                            bool gaveBack = !positive && (_handedBack || refusedNow);
                            Log?.Invoke(gaveBack
                                ? $"✖ {step.Item}: he didn't take it."
                                : $"✖ {step.Item}: nothing came back from the server within "
                                  + $"{_script.ConfirmSeconds}s. Moving on rather than trying again — if the item "
                                  + "did go into a trade window that hasn't committed, offering another one just "
                                  + "adds it to the pile and gives them all away at once.");
                            if (gaveBack) refusedThisStep = true;
                            ReportWindow(step);
                            NoteCursorRisk();
                        }
                        if (misses >= MaxStepMisses)
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
                            if (refusedThisStep) refusedSteps++;
                            cycleComplete = false;
                            stepMisses[step] = 0;             // next pass gets its own attempts
                            Log?.Invoke(i + 1 < steps.Count
                                ? $"↷ giving up on {step.Item} for now and trying {steps[i + 1].Item} — the NPC "
                                  + "only takes the item his current quest stage asks for, so if this stage is "
                                  + "already done, the next item is the one he wants."
                                : $"↷ {step.Item} went unanswered and it's the last item in the cycle.");
                            break;                            // next STEP, not the end of the run
                        }
                        await Task.Delay(1500, ct);           // retry THIS step
                    }
                }

                // Hushed HERE, above the stop blocks: the retry branch below ends in a `continue`
                // that jumped straight over this, so a fruitless first pass replayed the whole
                // opening narration — and every one of those lines is a blocking hop to the UI
                // thread, at the moment the runner is about to start clicking again.
                _narrate = false;                         // the first pass told the story; hush now

                // Nothing at all got through this pass. Now — and only now — is stopping right:
                // every item in the cycle has been offered and refused, so retrying the same list
                // forever would be a loop, not persistence.
                if (!abort && stepsDone == 0 && stepsSkipped > 0 && ++fruitlessPasses < 2)
                {
                    Log?.Invoke("↷ nothing got through that pass. Trying the whole cycle once more before giving up "
                              + "— one attempt per item is deliberate, so a single unlucky pass is not evidence.");
                    await Task.Delay(1200, ct);
                    continue;
                }
                if (!abort && stepsDone == 0 && stepsSkipped > 0)
                {
                    // The advice is only worth printing if the branch matches what actually
                    // happened. "Nothing was acknowledged" covers an empty bag — where no click was
                    // ever made — just as well as it covers a refused hand-in, and telling the
                    // first group to re-pick their NPC sends them to fix picks that were fine.
                    string why;
                    // EVERY give-up has to have been a refusal before saying "everything". A pass
                    // where the totem was refused and the Orders had simply run out is not a quest
                    // -state problem, and pointing at the journal's request timer would bury the
                    // one answer that was true.
                    if (refusedSteps > 0 && refusedSteps == stepsSkipped)
                        why = "He handed everything back. Nothing was lost and no click went astray — this is the "
                            + "quest's state, not the bot's aim: the task isn't assigned right now. The journal's "
                            + "request timer may not be up yet, or this NPC may want hailing before the phrase will "
                            + "re-assign it. If the journal DOES show the task, then what was picked up wasn't the "
                            + $"right item — the last scan scored {(_lastMatch.Length > 0 ? _lastMatch : "unknown")}, "
                            + "so lower \"icon match\" on the card until the wrong one stops qualifying.";
                    else if (scanFoundNothing)
                        why = "Nothing was ever offered: the bag scan couldn't find these items to pick up. Either "
                            + "you're out of them, or the bags aren't open (set an open-bags key on this card), or "
                            + "the icon signatures need re-taking — re-pick each item's slot with a tight box.";
                    else if (!_sawAnyOffer && Stats.HandIns == 0)
                        why = "The log IS being read — chat and buffs came through — and in all that time it never "
                            + "printed a hand-over line, not even a wrong one. So no trade ever completed, which "
                            + "points at the gesture rather than at the items: stand exactly where you picked him, "
                            + "re-pick the NPC on his body, then open a give window by hand and re-pick GIVE on its "
                            + "button. Raising \"give wait\" gives the trade window longer to appear.";
                    else
                        why = "The log IS being read and a hand-over line DID appear, so the gesture works. Three "
                            + "things look like this. If the lines above show the server taking something under a "
                            + "different name, my name for that item is wrong — fix it on the card and these become "
                            + "confirmed hand-ins. If a hand-over line arrived just after a miss, the server is "
                            + $"slower than the {_script.ConfirmSeconds}s confirm wait — raise it. Otherwise this NPC "
                            + "is declining these particular items: you're out of them, or the quest you're holding "
                            + "is at a stage that wants none of them. Check the journal.";
                    // The console line about the cursor may have scrolled past hours ago; the Finish
                    // string is what stays on the card. If the most actionable thing the runner
                    // knows is "something may be in your hand", the message that persists has to
                    // carry it.
                    if (_maybeHolding && _script.WaitForConfirm)
                        why += " Also check the cursor: an item may be stuck to it, which the bag scan cannot see.";
                    Finish($"Stopped after {Stats.Cycles} cycle(s) / {Stats.HandIns} hand-in(s). " + why);
                    HumanizedMouse.MoveInstant(home.x, home.y);
                    return;
                }

                // A PASS is one trip round the list; a CYCLE is a pass where every step confirmed.
                // They used to be the same thing, and once a step could be skipped they stopped
                // being: a script whose second item is exhausted (or whose name doesn't match the
                // server's wording) would hand item one over forever, never counting a cycle, so
                // "3 cycles" never ended, the pacing delay never ran, and the first-cycle narration
                // never hushed — a run that quietly became infinite and chatty.
                if (stepsDone > 0) fruitlessPasses = 0;
                if (cycleComplete)
                {
                    partialRun = 0;
                    Stats.Cycles++;
                    if (!_assumedAnyThisPass) _script.LifetimeCompleted++;
                    Log?.Invoke($"— cycle {Stats.Cycles} complete —");
                }
                else if (stepsDone > 0)
                {
                    // Something worked and something didn't. Worth another go — but not forever:
                    // a step that can NEVER succeed would otherwise be offered every pass, for
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
            Finish($"Stopped — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s){ConfirmTail}");
        }
        catch (OperationCanceledException)
        {
            try { HumanizedMouse.MoveInstant(home.x, home.y); } catch { }
            Finish($"Stopped — {Stats.Cycles} cycle(s), {Stats.HandIns} hand-in(s){ConfirmTail}");
        }
        catch (Exception ex)
        {
            Diag.BotLog.Log("quest", "runner error: " + ex);
            Finish("Quest Runner error: " + ex.Message);
        }
    }
}
