using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;
using EQAvatar.Spike.Map;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike.Roles;

public sealed class HuntStats
{
    public int Kills, Fights, MobsConsidered, Skipped, Deaths;
    public string State = "idle";
    public string LastCon = "";
}

/// <summary>
/// Grind "Hunt" engine. A supervised state machine that roams within the zone bounds you've
/// explored, targets + considers a mob, fights it with your rotation until it dies, then rests and
/// hunts again. Foreground only (same <see cref="IInputSink"/> as the simple grind), so it pauses
/// the instant EQ loses focus and F12 stops it.
///
/// Movement is human-like: forward bursts with strafe (A/D), the occasional back-step (S), and
/// right-mouse look-around to pan the camera while running. Target/consider can be bound to keys OR
/// mouse buttons (e.g. target = Tab, con = mouse5).
///
/// Honest limits — read before trusting it unsupervised:
///  • EQ's log carries NO HP/mana, so "rest" is TIME-BASED here, not %-based. True %-gating needs
///    OCR of the health/mana bars (the planned inventory/HUD scan).
///  • Movement is dead-reckoning from /loc; it can walk into walls or off ledges. Bind a /loc macro
///    key (or keep one running) so position stays live, explore the area once on foot, and WATCH it.
/// </summary>
public sealed class HuntRole
{
    public event Action<string>? Log;
    public event Action? Stopped;
    /// <summary>The character died. Raised BEFORE the role tears itself down, so the owner can run
    /// the respawn/hold handling (click the respawn window, keep the session alive) — the two
    /// instrumented nights proved that a client left inputless after a death is AFK-flagged and
    /// then exits with END_GAME about an hour later. Stopping the hunt is still right — the
    /// character respawns at bind, and "walk back to camp" from there is how it drowned — but
    /// stopping must never mean surrendering the client to the idle kick.</summary>
    public event Action? Died;
    /// <summary>The role stopped ITSELF for safety — a teleport it will not walk back from, or
    /// water under the character — with the character alive and in no danger. The owner keeps the
    /// SESSION alive for it: parking without a keep-alive just moves the loss from a drowning to
    /// an idle kick half an hour later, and the user still wakes up to a closed game.</summary>
    public event Action? Parked;
    public HuntStats Stats { get; } = new();

    private readonly IInputSink _sink;
    private readonly AppSettings _s;
    private readonly List<(InputKey key, int delayMs)> _rotation;
    private readonly EqLogWatcher? _watcher;
    private readonly HeatmapModel _heat;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;

    private volatile bool _mobDead, _selfDead, _rmbDown, _attacked;
    // VOLATILE now that the engage path SPINS on them. They are written on the log thread and were
    // only ever read after a fixed sleep, which happens to force a fresh read; a poll loop does not,
    // and the rest of this file is scrupulous about exactly this (volatile _cantSee/_tooFar,
    // Interlocked on the swing counts, Volatile.Read on _locTicks).
    private volatile ConsiderDifficulty _lastCon = ConsiderDifficulty.Unknown;
    private volatile ConsiderAttitude _lastAttitude = ConsiderAttitude.Unknown;
    private string _lastConText = "";
    private double? _x, _y;
    private double? _startX, _startY;                       // tether anchor: first /loc after start
    /// <summary>Altitude the run started at. The reference for "am I underwater?" — an absolute
    /// depth is meaningless across zones, but "far below where I was standing" never is.</summary>
    private double? _startZ;

    /// <summary>Absolute backstop, scaled off the user's own leash — see refuseAt in GoHome. The
    /// leash defaults to 300 and the UI allows 1500, so a bare constant small enough to catch an
    /// eject would refuse ordinary roaming and stop every run on its first breach.</summary>
    private const double FarTetherStop = 250;
    /// <summary>A position jump too large to have been walked: the instance expired and put the
    /// character at the zone-in. Cleared only by a fresh run.</summary>
    private bool _teleported;
    /// <summary>Consecutive passes spent well below the run's starting altitude. One is a ravine;
    /// three is water.</summary>
    private int _deepReadings;
    /// <summary>This far below the run's starting altitude is water or a pit, never new ground.
    /// Both drownings sat at z −33 against an anchor of z 11–22.</summary>
    private const double DeepBelowAnchor = 25;
    private DateTime _lastLoc = DateTime.MinValue;
    private readonly List<string> _targets = new();          // directive mode: lowercase mob names

    // --- heading, homing + calibration (0.9.15) -------------------------------------------
    // The log gives POSITION but not FACING. Heading is derived from the vector between two
    // /loc points taken while running forward; turns are made with right-mouse mouselook drags
    // (pixels ∝ degrees) and the px/degree ratio self-calibrates from measured heading changes.
    private double _hdg;                                     // radians in loc space (x=EW, y=NS)
    private bool _hdgValid;
    private double _pxPerDeg;                                // mouselook px per degree (self-tuning)
    private int _turnSign = 1;                               // drag direction ↔ angle sign (auto-detected)
    private int _signMisses;
    /// <summary>The drag→compass polarity has been OBSERVED, so stop re-testing it. It is a
    /// property of the mouse and the client and cannot change mid-session; treating it as an open
    /// question on every turn is what allowed a single overshoot to invert every subsequent
    /// turn.</summary>
    private bool _turnSignProven;
    /// <summary>The <see cref="CompassReader.Mirror"/> the polarity was proven under. The latch's
    /// premise — "this is a property of the mouse and the client, fixed for the session" — is
    /// FALSE, and review caught it: the reader assumes +1 until it learns the real mirror, and a
    /// spin recalibration resets it to unlearned. Proving a polarity under one mirror and keeping
    /// it after the mirror flips inverts every turn for the rest of the run with no way back, so
    /// the latch is stamped with the mapping it was measured under and dies with it.</summary>
    private int _turnSignMirror;
    /// <summary>Consecutive compass observations that the drag went the wrong way. Kept apart from
    /// <see cref="_signMisses"/>, which belongs to the open-loop /loc calibration — two different
    /// measurements of the same quantity, and sharing a counter would let one vote in the other's
    /// election.</summary>
    private int _compassSignMisses;
    /// <summary>Consecutive compass observations that the drag went the RIGHT way. Corroboration
    /// is required in both directions — see the note where it is counted.</summary>
    private int _compassSignHits;
    private double _cmdTurnDeg;                              // commanded turn sum since last measured heading
    private double _preTurnHdg;
    private int _turnsSinceMeasure;
    private double _fwdMsSinceLoc, _sideMsSinceLoc;          // motion mix between locs (heading quality gate)
    private double _speed = 50;                              // measured run speed, units/sec (closed-loop)
    private long _locTicks;                                  // last /loc line time (for FreshLoc waits)
    /// <summary>Consecutive position readings refused as physically impossible. Bounded, because a
    /// guard that can never be overruled is its own failure mode.</summary>
    private int _wildLocs;
    /// <summary>A zone line just landed, so the next big jump is real.</summary>
    private bool _justZoned;
    /// <summary>How long after pressing the /loc key a position line still counts as OUR answer.
    /// The heartbeat fires every 2–3 seconds at its tightest, so this has to be comfortably wider
    /// than a round trip and still narrow enough that a stranger's line rarely lands inside it.</summary>
    private const double LocAnswerWindow = 3.5;
    /// <summary>When the refusal was last narrated. See the rate limit at its call site.</summary>
    private DateTime _wildSaid = DateTime.MinValue;
    private long _prevSegTicks;

    // facing + bard state fed from the log
    private volatile bool _cantSee, _tooFar;
    private int _ourSwings;                                  // count of OUR outgoing combat lines
    /// <summary>MELEE output only — "You slash a rat…" and friends. Kept apart from _ourSwings,
    /// which deliberately counts spell and song damage too so a caster's facing logic works. Auto
    /// attack is a melee question, and a landing nuke says nothing about whether the sword is
    /// moving.</summary>
    private int _meleeSwings;
    /// <summary>CONTINUOUS attack only — the four verbs a player's auto attack prints. A rotation
    /// that fires kick lands melee lines every fight without auto attack ever engaging, so the
    /// fallback that exists to notice exactly that cannot be counted with _meleeSwings.</summary>
    private int _autoSwings;

    /// <summary>How many 400 ms polls a walk will wait for the game to come back to the front
    /// before it gives up — two minutes, which is a long look at another window and nowhere near
    /// the ~30 minutes it takes the client to raise the A.F.K. flag.</summary>
    private const int PausedPollsBeforeGivingUp = 300;

    /// <summary>How close together two "can't see it / too far" passes have to be before they are
    /// read as one target being genuinely unreachable, rather than two unrelated blips in a long
    /// fight. Four seconds is a little over one cast.</summary>
    private const double BlockedTogetherSeconds = 4;
    /// <summary>What the log last said about auto attack, when it said anything. Null = it has not
    /// mentioned it, which is the usual state and is why the swing count is the primary signal.</summary>
    private bool? _autoAttackOn;
    private readonly InputKey _autoAtk;
    private volatile bool _singing;                          // bard melody believed active
    private DateTime _melodyAt = DateTime.MinValue;

    // --- compass, levitation + fall recovery (0.9.16) --------------------------------------
    private readonly CompassReader? _compass;                // real heading reads off the game UI
    private readonly VitalsReader? _vitals;                  // HP/mana bar reads off the game UI
    private readonly Func<string?>? _fallbackZone;           // map page's zone, when the log hasn't said
    private DateTime _levAt = DateTime.MinValue;
    private volatile bool _levNeeded;
    private DateTime _lastPitchFix = DateTime.MinValue;
    private double? _z, _zGood, _goodX, _goodY;              // last altitude that counted as "ground"
    private volatile bool _fell, _dip;

    // resolved binds
    private readonly InputKey _fwd, _left, _right, _back, _target, _con, _loc;

    public bool Running => _cts is { IsCancellationRequested: false };

    public HuntRole(IInputSink sink, List<(InputKey, int)> rotation, string? logPath, AppSettings s, HeatmapModel heat,
                    CompassReader? compass = null, VitalsReader? vitals = null, Func<string?>? fallbackZone = null)
    {
        _sink = sink; _rotation = rotation; _s = s; _heat = heat; _compass = compass;
        _vitals = vitals; _fallbackZone = fallbackZone;
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath);
        _fwd = InputKey.Parse(s.HuntForwardKey);
        _left = InputKey.Parse(s.HuntLeftKey);
        _right = InputKey.Parse(s.HuntRightKey);
        _back = InputKey.Parse(s.HuntBackKey);
        _target = InputKey.Parse(s.HuntTargetKey);
        _con = InputKey.Parse(s.HuntConsiderKey);
        _loc = InputKey.Parse(s.HuntLocKey);
        _autoAtk = InputKey.Parse(s.HuntAutoAttackKey);
        foreach (string line in (s.GrindTargetMobs ?? "").Split('\n'))
        {
            string t = line.Trim().ToLowerInvariant();
            if (t.Length > 1 && !_targets.Contains(t)) _targets.Add(t);
        }
        _pxPerDeg = Math.Clamp(s.HuntTurnPxPerDegree <= 0 ? 3.5 : s.HuntTurnPxPerDegree, 0.8, 12);
    }

    /// <summary>Tether anchor (loc coords) for the map circle; null until the first /loc lands.</summary>
    public double? AnchorEw => _startX;
    public double? AnchorNs => _startY;

    /// <summary>Where the character was when this role last accepted a position line — accepted
    /// meaning it survived the chat guard AND the "could it physically have got there" test, so
    /// it is the same number the navigation is steering by, not a raw log read.</summary>
    public double? LastX => _x;
    public double? LastY => _y;
    public double? LastZ => _z;

    private string Stance => (_s.GrindStance ?? "aggressive").Trim().ToLowerInvariant();
    private string Mode => (_s.GrindMode ?? "hunt").Trim().ToLowerInvariant();

    // --- zone plan (waypoints + hunting-zone shape drawn on the Maps page) -------------------
    private ZonePlan? _plan;
    private string? _planZone;
    private int _wpIndex = -1, _wpStep = 1;
    private double _wpTx, _wpTy;
    private bool _wpHave, _noPlanWarned, _noLocWarned;
    private int _noTargetRuns;                               // consecutive passes with nothing selected

    private ZonePlan? CurrentPlan()
    {
        // The log only names the zone on a "You have entered …" line, so a bot started while
        // already standing in the zone has NO zone at all — which used to silently mean "no plan",
        // and waypoint routes just never ran. Fall back to whatever zone the Maps page has open,
        // which is by definition the zone the route was drawn on.
        //
        // The fallback is consulted ONLY until a plan zone sticks (_planZone). Otherwise browsing
        // to another zone's map mid-run would swap the route under the bot's feet and send it
        // walking toward another zone's coordinates.
        string? stem = ZoneTable.ShortFor(_heat.Current ?? "") ?? _planZone ?? _fallbackZone?.Invoke();
        if (stem is null) return _plan;                      // zone unknown yet — keep last known
        if (stem != _planZone)
        {
            _planZone = stem;
            _plan = ZonePlan.Load(stem);
            _wpIndex = -1; _wpHave = false; _noPlanWarned = false;
            if (_plan != null)
                Log?.Invoke($"Plan loaded for {stem}: {_plan.Waypoints.Count} waypoint(s)"
                          + (_plan.HasShape ? $" + a {_plan.ShapeType} hunting zone." : "."));
        }
        return _plan;
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _reInstanceTried = false;                            // a new run gets a fresh attempt
        if (_watcher != null) { _watcher.LineRead += OnLine; _watcher.Start(fromStart: false); }
        Log?.Invoke($"HUNT started — target={_target.Display}, con={_con.Display}, move={_fwd.Display}/{_left.Display}/{_right.Display}/{_back.Display}"
                    + (_loc.IsNone ? "" : $", /loc key={_loc.Display}") + ". Keep EQ focused; F12 stops. Watch it.");
        if (_s.GrindCastOnly)
            Log?.Invoke("Cast/sing only — no facing turns and no closing in during a fight; unreachable targets get dropped instead.");
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        // Idempotent on purpose: the death path calls this from the loop's own thread, and the
        // user's F12 can land moments later. Two Stops must not raise Stopped twice — the second
        // EndRoleSession would tear down a session bookkeeping that is already closed.
        if (_cts is not { IsCancellationRequested: false }) return;
        _cts.Cancel();
        if (_watcher != null) { _watcher.LineRead -= OnLine; _watcher.Dispose(); }
        ReleaseKeys();
        if (Math.Abs(_s.HuntTurnPxPerDegree - _pxPerDeg) > 0.05)
        { _s.HuntTurnPxPerDegree = Math.Round(_pxPerDeg, 2); _s.Save(); }   // keep the calibration
        Stats.State = "stopped";
        Log?.Invoke("Hunt stopped.");
        Stopped?.Invoke();
    }

    private void OnLine(string raw)
    {
        // THE WATCHER HANDS OUT THE FILE'S LINE, STAMP AND ALL — "[Wed Aug 26 08:46:54 2026] You
        // slash …". Every Contains() below is unaffected; every ANCHORED test needs this, and the
        // melee counter went years without it (see LogEventParser.StripStamp).
        string msg = LogEventParser.StripStamp(raw);

        // Facing/range feedback + bard interrupts arrive as plain lines the parser doesn't type.
        // GATED, which they never were: two blocked passes end a cast-only fight, and "lol he's
        // too far away" in /ooc is two words a stranger types. It used to be absorbed by the reset
        // on our own damage landing; that reset is now correctly narrower, so the hole is real.
        if (!LogEventParser.SpokenAloud(msg))
        {
            if (msg.Contains("You cannot see your target", StringComparison.OrdinalIgnoreCase)) _cantSee = true;
            else if (msg.Contains("too far away", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("out of range", StringComparison.OrdinalIgnoreCase)) _tooFar = true;
        }

        // OUR SPELL / SONG OUTPUT. Attributed to the VICTIM ("a rat was hit by non-melee for 42
        // points of damage"), so the melee "did one of OUR lines print?" test never fires for a
        // caster or a bard, and the facing/give-up logic reads a perfect melody as total silence.
        //
        // ⚠ THIS IS THE 10-12 SECONDS A KILL. What this client actually prints is
        //
        //     [Wed Aug 26 09:08:33 2026] A kerran `amir has taken 66 damage from your Fufil's Curtailing Chant V.
        //
        // and that matched NOTHING the old code looked for: not "hit by non-melee", not "resisted
        // your", not the melee block's "by your " (this says FROM your), not the classifier's
        // "points of damage" (this says "damage from"). So the 8-second "did anything land?"
        // window expired on every single kill, the target was dropped and re-conned, and the mob
        // died on the third attempt from the song that had been ticking since the first. The field
        // logs are wall-to-wall "Cast-only: nothing landed in 8s" between kills because of it.
        //
        // The matching lives in LogEventParser (OurSpellLanded) with the rest of the log grammar,
        // and it is ANCHORED, which is also why it is handed the STRIPPED message: the first
        // attempt at this fix gated on SpokenByAPlayer, and that call refuses any line containing
        // an apostrophe — which is every spell in the game. It would have shipped as a no-op and
        // taken the two working patterns down with it. Read the comment there before touching it.
        //
        // STILL OPEN, and bounded rather than guessed at: a song ticks on EVERY mob it hit, so a
        // dropped or fleeing mob keeps printing damage and keeps `lastOut` fresh while the CURRENT
        // target is unreachable. The give-up window can therefore run to HuntMaxFightSeconds
        // instead of the 8s cast-only cap. Fixing it properly means scoping the damage to the
        // target's NAME, which this class does not track yet — and a name test that mismatches
        // reintroduces exactly the bug above, so it waits for a measurement rather than a guess.
        if (LogEventParser.OurSpellLanded(msg))
            Interlocked.Increment(ref _ourSwings);

        // ONE GATE OVER BOTH, because both are facts read out of a public document — and it is
        // SpokenALOUD, not SpokenByAPlayer: the latter refuses any line containing an apostrophe,
        // and mob names have them ("a gnoll's pet"), so it threw real combat lines away. The
        // anchor inside OurWeaponMoved is what actually keeps other people's chat out — a
        // stranger's line starts with their name — and the chat-verb test catches the one case an
        // anchor cannot, the LOCAL player's own speech: "You say, 'you try to crush it'".
        if (!LogEventParser.SpokenAloud(msg))
        {
            // DID THE WEAPON MOVE? Grammar lives in LogEventParser.OurWeaponMoved with the rest of
            // it, and it is fed the STRIPPED message — this test used to be anchored against the
            // watcher's raw line, which still carries "[Wed Aug 26 08:46:54 2026] ", so it had
            // never matched once, for any user, on any client.
            if (LogEventParser.OurWeaponMoved(msg))
            {
                Interlocked.Increment(ref _meleeSwings);
                // AND ONLY A CONTINUOUS SWING IS EVIDENCE THAT CONTINUOUS ATTACK IS ON. A kick is
                // the rotation's doing; treating it as proof retires the auto-attack fallback for
                // the whole run, silently. See LogEventParser.AutoSwing.
                if (LogEventParser.AutoAttackSwung(msg))
                { Interlocked.Increment(ref _autoSwings); _autoAttackOn = true; }
            }

            // And what the CLIENT says about auto attack, when it says anything. A guildmate typing
            // "turn auto-attack off before you pull" must not clear the flag that suppresses the
            // toggle press — the same class of bug as a stranger's chat moving the character.
            if (AutoAtkOn.IsMatch(msg))
            {
                _autoAttackOn = true;
                // A PRESS THAT WORKED IS NOT EVIDENCE OF A STALE STRIP, and without this the cap
                // fired on the feature working perfectly. EQ drops auto attack when the target
                // dies, so on the very setup this fallback exists for — a rotation that does not
                // engage attack — every single fight begins with attack genuinely off, the border
                // correctly reads silent, the key is pressed, and it engages. Ten of those is one
                // hour of an overnight run, after which the old code stopped pressing and told the
                // user their strip had probably moved. The cap is for a strip that is WRONG, and a
                // press the client confirms turned attack on is proof it was right.
                Interlocked.Exchange(ref _borderNudges, 0);
            }
            else if (AutoAtkOff.IsMatch(msg))
            {
                _autoAttackOn = false;
                // THE BORDER JUST GOT CAUGHT LYING, and this is the only moment it can be.
                //
                // EQ prints its own "auto attack is off" for a synthesized press exactly as for a
                // hand press. So if the client says attack went OFF within a couple of seconds of a
                // press this class made BECAUSE the border read silent, then the border was silent
                // while attack was running — and it was this class that stopped the fight. Reading
                // that and doing nothing was the worst thing in the previous version: the same
                // wrong press then repeated once a fight, all night, each time announcing
                // confidently that attack had been off.
                //
                // The strip is not un-proven on disk — the user picked it and one bad night may be
                // a moved window rather than a bad pick — but it is not believed again this run.
                // TickCount64, not DateTime. This app grinds overnight, and wall-clock arithmetic
                // across a daylight-saving step produces a negative difference that sails under any
                // "was it recent" test.
                long pressed = Interlocked.Read(ref _borderPressedTicks);
                // A DEAD MOB TURNS ATTACK OFF TOO, and the client prints the same sentence for it.
                // Without this the detector fires on the first fight whose mob happens to die inside
                // the window after a correct press, and then disables the border for the whole run
                // while apologising for something that worked.
                if (pressed != 0 && !_borderLied && !_mobDead && Environment.TickCount64 - pressed <= 2500)
                {
                    _borderLied = true;
                    Log?.Invoke("The game says auto attack just went OFF right after I pressed "
                              + $"{_autoAtk.Display} — so the border read silent while attack was running, and that "
                              + "press stopped your fight. I'm sorry. I won't trust that strip again this run; "
                              + "re-pick it on the Grind page (it may have moved) and run both checks.");
                }
            }
        }

        if (_s.GrindBardMode && _singing && LogEventParser.MelodyStopped(raw))
        { _singing = false; Log?.Invoke("Melody stopped (log) — will recast."); }
        if (_s.LevEnabled && (raw.Contains("float gently to the ground", StringComparison.OrdinalIgnoreCase)
            || (raw.Contains("has worn off", StringComparison.OrdinalIgnoreCase)
                && _s.LevBuffName is { Length: > 1 } lb && raw.Contains(lb, StringComparison.OrdinalIgnoreCase))))
            _levNeeded = true;

        LogEvent ev = LogEventParser.Parse(raw);
        switch (ev.Kind)
        {
            case LogEventKind.Location:
                if (ev.X is double nx && ev.Y is double ny)
                {
                    long now = DateTime.Now.Ticks;
                    // COULD THE CHARACTER ACTUALLY HAVE GOT THERE?
                    //
                    // Belt and braces behind the parser's chat guard, and the braces matter: a
                    // position is the one input where a single wrong number does not degrade the
                    // run, it INVERTS it. A camped character that is told it is a thousand units
                    // from its anchor walks a thousand units to "come back" — and in the field it
                    // walked into water and drowned while nobody was watching.
                    //
                    // The bound is this class's own speed model: _speed is clamped to 130 units a
                    // second, so 260 plus a floor for jitter is generous by a factor of two and
                    // still refuses everything a chat line can invent. Zoning legitimately
                    // teleports, so a zone line disarms this for a moment.
                    // DID WE ASK FOR THIS ONE?
                    //
                    // The parser refuses anything a player SAID, but no test over the text alone can
                    // catch every forgery: an emote is printed as "<name> <text>", so a character
                    // actually named "Your" typing `/em Location is 1, 2, 3` produces a line that is
                    // byte-identical to the client's own. This is the test that needs no text at all.
                    // We fire the /loc key ourselves every couple of seconds and stamp when; an
                    // answer arriving in that window is ours, and one arriving outside it is
                    // somebody else's line landing between our asks. A forger would have to hit a
                    // window they cannot see.
                    //
                    // Only when a /loc KEY is configured. Users who instead keep a repeating macro
                    // running in game have no asks to correlate with, and this file promises that
                    // setup works — so for them the test is skipped rather than quietly breaking it.
                    // DERIVED FROM THE HEARTBEAT, not a constant. 3.5s is right when the key fires
                    // every 2-3 seconds (camp, waypoints, a tight tether) — but the default interval
                    // is 6, and a flat 3.5 would drop every reading from a user's own repeating
                    // in-game macro into the dead zone between our asks, cap it at 60 units, and
                    // throw away three quarters of the extra resolution they set it up for.
                    double window = Math.Max(LocAnswerWindow, Math.Max(2, _s.HuntLocEverySeconds) + 1.0);
                    bool solicited = _loc.IsNone || (DateTime.Now - _lastLoc).TotalSeconds <= window;
                    if (_x is double px && _y is double py && !_justZoned)
                    {
                        double gap = Math.Sqrt((nx - px) * (nx - px) + (ny - py) * (ny - py));
                        double secs = Math.Max(0.25, (now - Interlocked.Read(ref _locTicks)) / (double)TimeSpan.TicksPerSecond);
                        // An unsolicited line may report a small drift and nothing more. That is not
                        // a claim it is fake — a heartbeat can land a moment late — it is a refusal
                        // to let a line we did not ask for MOVE the character any distance worth
                        // walking. The three-strike escape below still overrules it.
                        double could = solicited ? 260 * secs + 60 : 60;
                        if (gap > could)
                        {
                            // NOT silently. A reading refused is a fact about the log, and the raw
                            // line is the only thing that identifies what is producing them.
                            // SAID, but not on a loop. The reason does not change between repeats,
                            // and the three-strike counter re-arms — so without a rate limit this
                            // writes a line for every reading, for ever, and buries the run it is
                            // trying to explain.
                            bool fresh = (DateTime.Now - _wildSaid).TotalSeconds > 30;
                            if (++_wildLocs <= 3 && fresh)
                            {
                                _wildSaid = DateTime.Now;
                                Log?.Invoke(solicited
                                    ? $"Ignoring a position that can't be real — it jumped {gap:0} units in "
                                      + $"{secs:0.0}s, and nothing moves that fast. The line was: {ev.Text}"
                                    : $"Ignoring a position I didn't ask for that moves me {gap:0} units — I last "
                                      + $"pressed the /loc key {(DateTime.Now - _lastLoc).TotalSeconds:0.0}s ago, so "
                                      + "this isn't an answer to it. Anything a player types is refused; this is the "
                                      + $"line: {ev.Text}");
                            }
                            // …but not for ever. Three in a row means the refusal is now the thing
                            // that is wrong — a real teleport, a zone I missed — so take it and say so.
                            if (_wildLocs < 4)
                            {
                                // AND THE MOVEMENT LEDGER RESETS WITH IT. This `break` skips the
                                // bookkeeping at the bottom of the case, so the forward/strafe
                                // milliseconds kept banking across every refusal — and then the
                                // fourth reading, the one accepted BECAUSE it is a teleport, was
                                // handed to the heading learner as though the character had walked
                                // that thousand units. It set _hdg to the camp→zone-in bearing,
                                // taught the COMPASS that mapping (which is saved to disk), and
                                // re-fit the walking speed from it. A compass confidently wrong by
                                // fifteen degrees then steers every leg of the walk home, and the
                                // only correction downstream is a 180° flip the next compass read
                                // undoes. Nothing was measured here, so nothing is learned here.
                                _fwdMsSinceLoc = 0; _sideMsSinceLoc = 0;
                                Interlocked.Exchange(ref _prevSegTicks, now);
                                break;
                            }
                            // The accepted-teleport reading is the same story with the volume up.
                            _fwdMsSinceLoc = 0; _sideMsSinceLoc = 0;
                            // AND THAT IS THE TELEPORT SIGNAL. Three readings in a row that were
                            // physically impossible, and then one accepted anyway, is precisely
                            // what an instance eject looks like — this guard already measures
                            // distance against elapsed time, already stands aside for a real zone
                            // line, and is already tuned. A second, parallel "did it jump?" test
                            // written beside it was strictly worse: it compared against a `_x`
                            // this guard may have left stale for twenty seconds, so ordinary
                            // walking could trip it and park the run for the night.
                            _teleported = true;
                            if (fresh)
                                Log?.Invoke($"That's {_wildLocs} positions in a row I couldn't accept, so I'll "
                                          + "believe this one and carry on from here — and a jump like that is a "
                                          + "teleport, so I will not try to walk home from it.");
                            // RE-ARMED. Leaving the counter above the threshold meant every later
                            // reading sailed through too — the guard would have switched itself off
                            // for the rest of the run and printed "that's 47 in a row" while doing it.
                            _wildLocs = 0;
                        }
                        else _wildLocs = 0;
                    }
                    _justZoned = false;
                    // Heading = direction of the last movement segment, but only when the motion
                    // between the two locs was forward-dominant (strafes/backsteps corrupt it).
                    if (_x is double ox && _y is double oy
                        && _fwdMsSinceLoc >= 300 && _sideMsSinceLoc <= _fwdMsSinceLoc * 0.4)
                    {
                        double dx = nx - ox, dy = ny - oy;
                        double seg = Math.Sqrt(dx * dx + dy * dy);
                        if (seg >= 5)
                        {
                            double measured = Math.Atan2(dy, dx);
                            CalibrateTurn(measured);
                            _hdg = measured; _hdgValid = true;
                            _compass?.LearnFromMovement(Deg(measured));   // teaches the compass→loc mapping
                            double dt = (now - Interlocked.Read(ref _prevSegTicks)) / (double)TimeSpan.TicksPerSecond;
                            if (dt > 0.4 && dt < 30)
                                _speed = Math.Clamp(0.7 * _speed + 0.3 * (seg / dt), 20, 130);
                        }
                    }
                    Interlocked.Exchange(ref _prevSegTicks, now);
                    Interlocked.Exchange(ref _locTicks, now);
                    _fwdMsSinceLoc = 0; _sideMsSinceLoc = 0;
                    _x = nx; _y = ny;
                    if (_startX is null)
                    {
                        _startX = nx; _startY = ny;
                        _startZ = ev.Z;      // the reference for "am I underwater?"
                        if (_s.HuntTetherEnabled) Log?.Invoke($"Tether anchored at /loc {ny:0}, {nx:0} — radius {_s.HuntTetherRadius}.");
                    }

                    // Altitude watch: a sharp Z drop = fell into a pit or water → recovery mode.
                    if (ev.Z is double nz)
                    {
                        _z = nz;
                        if (_zGood is not double zg) { _zGood = nz; _goodX = nx; _goodY = ny; }
                        else
                        {
                            double drop = zg - nz;
                            if (drop > 18)
                            { if (!_fell) Log?.Invoke($"Dropped {drop:0} units (z {zg:0} → {nz:0}) — pit/water recovery mode."); _fell = true; }
                            else if (drop > 8) _dip = true;               // shallow dip → steer away
                            // NORMAL GROUND TRACKS US — but only ABOVE the water. _zGood
                            // re-baselined on any drop of 8 or less with no floor at all, so a
                            // shelving beach walked the baseline down step by step: _fell never
                            // set, the recovery never ran, and the depth guard was never
                            // consulted. That is a drowning the guard cannot see, and it is the
                            // shape of a beach, not an edge case.
                            else if (_startZ is not double sz0 || nz > sz0 - DeepBelowAnchor)
                            { _zGood = nz; _goodX = nx; _goodY = ny; }
                        }
                    }
                }
                break;
            case LogEventKind.Zone:
                // A zone really does teleport you, so the impossible-jump guard has to stand aside
                // for exactly one reading — otherwise it would spend three refusals and a warning
                // on the one movement in the game that is genuinely instant.
                _justZoned = true;
                _wildLocs = 0;
                break;
            case LogEventKind.Consider:
                _lastCon = LogEventParser.ConsiderReading(ev.Text);
                _lastAttitude = LogEventParser.AttitudeReading(ev.Text);
                _lastConText = ev.Text;
                Stats.LastCon = ev.Text; Stats.MobsConsidered++;
                Log?.Invoke($"con: {_lastCon}/{_lastAttitude} — {ev.Text}");
                break;
            case LogEventKind.Combat:
                // Something swinging at US wakes the defensive stance.
                if (ev.Text.Contains(" YOU ", StringComparison.Ordinal) || ev.Text.Contains(" YOU!", StringComparison.Ordinal)
                    || ev.Text.Contains(" YOU for ", StringComparison.Ordinal))
                    _attacked = true;
                // OurWeaponMoved, NOT StartsWith("You ") — "You have taken 12 points of damage."
                // starts with "You " and is classified Combat, so the old test counted being HURT
                // as output. Harmless while _ourSwings had four consumers; not harmless now that
                // it is the cast-only give-up clock and nothing else: a damage shield or an add's
                // DoT ticking on the character would hold a hopeless fight open for its full
                // timeout. Same exclusion the raw-line counter uses, in the one place it was missed.
                else if (LogEventParser.OurWeaponMoved(ev.Text)
                         || ev.Text.Contains("by your ", StringComparison.OrdinalIgnoreCase))
                    // _meleeSwings is NOT incremented here. It is counted from the raw line
                    // instead, because this classifier drops every non-slashing miss and a miss is
                    // exactly the case that matters.
                    Interlocked.Increment(ref _ourSwings);   // OUR output is landing → facing is fine
                break;
            case LogEventKind.Kill: _mobDead = true; break;
            case LogEventKind.Death: _selfDead = true; break;
        }
    }

    // ---------------- heading + turning (mouselook) ----------------

    private static double Deg(double rad) => rad * 180.0 / Math.PI;
    private static double NormDeg(double d) { while (d > 180) d -= 360; while (d < -180) d += 360; return d; }

    /// <summary>Compare a freshly measured heading against the turns commanded since the last
    /// measurement; tune px/degree (and the drag-direction sign) so turns converge.</summary>
    private void CalibrateTurn(double measuredHdg)
    {
        if (_turnsSinceMeasure < 1 || !_hdgValid) { _cmdTurnDeg = 0; _turnsSinceMeasure = 0; return; }
        double actual = NormDeg(Deg(measuredHdg) - Deg(_preTurnHdg));
        double cmd = _cmdTurnDeg;
        _cmdTurnDeg = 0; _turnsSinceMeasure = 0;
        if (Math.Abs(cmd) < 25 || Math.Abs(actual) < 8) return;
        if (Math.Sign(actual) != Math.Sign(cmd))
        {
            // NOT OVER THE COMPASS'S HEAD. This is the open-loop /loc estimate, and it is the
            // weaker witness of the two: `cmd` is an UNNORMALIZED sum of commanded turns while
            // `actual` is wrapped, so two 120° turns between /loc fixes read as a sign mismatch
            // that never happened. Left ungated it would silently invert a polarity the compass
            // had measured directly — and the compass, having settled, would never look again.
            if (_turnSignProven) { _signMisses = 0; return; }
            if (++_signMisses >= 2)
            { _turnSign = -_turnSign; _signMisses = 0; Log?.Invoke("Turn direction was inverted — flipped mouselook sign."); }
            return;
        }
        _signMisses = 0;
        double ratio = Math.Clamp(cmd / actual, 0.34, 3.0);
        _pxPerDeg = Math.Clamp(_pxPerDeg * (0.7 + 0.3 * ratio), 0.8, 12);
    }

    /// <summary>Turn the character by ~degrees using a right-mouse mouselook drag (positive =
    /// toward increasing loc-space angle once calibrated). Optimistically updates the heading;
    /// the next measured /loc segment corrects and calibrates.</summary>
    /// <summary>Raw mouselook drag of |px| horizontal pixels in the sign's direction.</summary>
    private async Task DragTurn(int signedPx, CancellationToken ct)
    {
        if (signedPx == 0 || !_sink.Ready) return;
        int dir = Math.Sign(signedPx), left = Math.Abs(signedPx);
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        _rmbDown = true;
        try
        {
            while (left > 0 && !ct.IsCancellationRequested && _sink.Ready)
            {
                int step = Math.Min(left, _rng.Next(14, 27));
                InputProbe.MouseMoveRelative(dir * step, _rng.Next(-2, 3));
                left -= step;
                await Task.Delay(16, ct);
            }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); _rmbDown = false; }
    }

    private async Task TurnBy(double degrees, CancellationToken ct)
    {
        degrees = NormDeg(degrees);
        if (Math.Abs(degrees) < 5 || !_sink.Ready) return;

        // Compass available → feedback turn: drag, read the needle, correct. Exact and immune
        // to sensitivity drift; also self-fixes an inverted drag direction on the first try.
        if (_compass is { Ready: true } && await TurnByCompass(degrees, ct)) return;

        int totalPx = (int)Math.Round(Math.Abs(degrees) * _pxPerDeg);
        int dir = (degrees >= 0 ? 1 : -1) * _turnSign;
        if (_turnsSinceMeasure == 0 && _hdgValid) _preTurnHdg = _hdg;
        _turnsSinceMeasure++;
        _cmdTurnDeg += degrees;
        await DragTurn(dir * totalPx, ct);
        if (_hdgValid) _hdg += degrees * Math.PI / 180.0;    // optimistic; measurement corrects
        await Task.Delay(Vary(90), ct);
    }

    /// <summary>Closed-loop turn against the live compass. Returns false if the needle can't be
    /// read right now (occluded, bad light) so the caller falls back to the open-loop turn.</summary>
    private async Task<bool> TurnByCompass(double degrees, CancellationToken ct)
    {
        if (_compass!.ReadLocDeg() is not double cur) return false;
        double target = cur + degrees;
        double px = _compass.PxPerDeg > 0.5 ? _compass.PxPerDeg : _pxPerDeg;
        int dir = _turnSign;
        for (int it = 0; it < 4 && !ct.IsCancellationRequested && _sink.Ready; it++)
        {
            double err = NormDeg(target - cur);
            if (Math.Abs(err) < 4) break;
            await DragTurn(Math.Sign(err) * dir * (int)Math.Round(Math.Abs(err) * px), ct);
            await Task.Delay(80, ct);
            if (_compass.ReadLocDeg() is not double now) break;
            // DID THE NEEDLE MOVE THE WRONG WAY — not "is the residual error bigger".
            //
            // THE OLD TEST CONFLATED TWO DIFFERENT FAULTS. It asked whether |target - now| had
            // grown, which is true both when the drag went the wrong way AND when it went the
            // RIGHT way and overshot: command 30°, turn 90° correctly, and the residual is 60 —
            // bigger, so the sign was flipped on a turn that was aimed perfectly and merely too
            // strong. Because the flip was written straight into the persistent `_turnSign`, that
            // poisoned every later turn (including the open-loop path, which reads the same
            // field) until the next overshoot flipped it back. The field log for 08-25 shows the
            // result: HUNDREDS of "flipped direction" lines in one run — an oscillation, not a
            // calibration, and roughly half of all turns aimed backwards because of it.
            //
            // Direction is disproven only by the needle DEFINITELY moving (>3°, so noise cannot
            // vote) and moving AWAY from the target. An overshoot has the right sign and is a
            // px-per-degree problem the loop's next iteration corrects on its own.
            double moved = NormDeg(now - cur);

            // ONLY ASK THE QUESTION WHERE IT HAS AN ANSWER.
            //
            // `moved` is wrapped into (-180, 180], so near a half-turn "toward the target" stops
            // meaning anything: command +180, turn 181° perfectly, and moved reads -179 — the
            // opposite sign on a turn that was aimed exactly right.
            //
            // AND THE LIMIT IS 90, NOT 150, because what wraps is the PHYSICAL rotation and not
            // the commanded one — the guard has to leave room for the overshoot as well as the
            // turn. A commanded 140° needs only a 1.29× overshoot to cross 180 and read
            // backwards, and 2× is ordinary when the spin calibration has locked onto the
            // half-period of a compass tape with north/south symmetry. The scan fires 120° and
            // ±140° turns routinely, so 90–149 was not a corner case, it was most of the
            // traffic — and the error there is SYSTEMATIC, which is exactly what a second
            // opinion cannot catch: two big turns produce two identical false readings and
            // corroborate each other.
            bool answerable = it == 0 && Math.Abs(err) < 90 && Math.Abs(moved) > 3;

            // A LATCH IS ONLY GOOD FOR AS LONG AS THE MAPPING IT WAS MEASURED UNDER. See
            // _turnSignMirror: the reader assumes mirror +1 until it learns better.
            int mirrorNow = _compass.Mirror;
            if (_turnSignProven && _turnSignMirror != mirrorNow)
            {
                _turnSignProven = false; _compassSignMisses = 0;
                Log?.Invoke("The compass re-learned its mirror, so the drag direction is an open question again.");
            }

            if (answerable && !_turnSignProven)
            {
                if (Math.Sign(moved) != Math.Sign(err))
                {
                    dir = -dir;                       // fix THIS turn immediately…
                    _compassSignHits = 0;
                    if (++_compassSignMisses >= 2)    // …and commit only on a second, independent look
                    {
                        _turnSign = dir; _compassSignMisses = 0;
                        Prove(mirrorNow, "inverted — flipped it for good");
                    }
                    else Log?.Invoke("Compass suggests the drag turned the wrong way — correcting this turn and watching.");
                }
                else
                {
                    // TWO LOOKS TO AGREE, TOO. The first version latched on a single agreeing
                    // sample, which is the more dangerous asymmetry of the two: a bad needle read
                    // agrees with a WRONG sign about half the time, and latching on it kills the
                    // in-call correction, muzzles CalibrateTurn, and gates off the px learner —
                    // every turn afterwards drags backwards with no route back.
                    _compassSignMisses = 0;
                    if (++_compassSignHits >= 2) { _compassSignHits = 0; Prove(mirrorNow, "correct — settled"); }
                }
            }
            else if (it == 0)
                // Inconclusive — a barely-moved needle or a turn too big to read. No vote may
                // survive it: a stale 1 from minutes ago would let one later reading commit alone.
                { _compassSignMisses = 0; _compassSignHits = 0; }

            // LEARN THE STRENGTH FROM THE SAME MEASUREMENT. Without this the loop can diverge:
            // px is captured once, and at an overshoot factor of 2 or more each iteration leaves
            // a residual at least as large as the one before it, so four iterations end further
            // off than they started — and can push |err| past 180 into the wrap above.
            if (Math.Abs(moved) > 8 && Math.Sign(moved) == Math.Sign(err))
            {
                double commandedDeg = Math.Abs(err);
                double ratio = Math.Clamp(commandedDeg / Math.Abs(moved), 0.34, 3.0);
                px = Math.Clamp(px * (0.7 + 0.3 * ratio), 0.8, 12);
                // AND IT HAS TO SURVIVE THE CALL. `px` is a local re-seeded on every entry, so a
                // systematically wrong PxPerDeg was re-paid in full on the first iteration of
                // every turn, for ever. Worse, while the compass path works CalibrateTurn never
                // runs, so the open-loop fallback kept its 3.5 default and inherited nothing —
                // meaning the moment the needle became unreadable the bot turned by a number
                // nothing had ever measured. This is the one place both paths can learn from.
                _pxPerDeg = px;
            }
            cur = now;
        }
        if (_compass.ReadLocDeg() is double fin) { _hdg = fin * Math.PI / 180.0; _hdgValid = true; }
        return true;
    }

    /// <summary>Record that the drag polarity has been observed, stamped with the compass mapping
    /// it was observed under.</summary>
    private void Prove(int mirror, string what)
    {
        if (_turnSignProven) return;
        // NEVER UNDER AN ASSUMED MAPPING. Mirror 0 means the reader has not learned its polarity
        // and is assuming +1; proving against an assumption can latch _turnSign in the wrong
        // space, and CalibrateTurn — the only witness that works in true /loc space — is gated
        // off the moment we do. Wait for the real mapping; it costs a few more turns.
        if (mirror == 0) return;
        _turnSignProven = true; _turnSignMirror = mirror;
        Log?.Invoke("Compass says the drag direction is " + what + " for this run.");
    }

    /// <summary>Refresh the heading straight off the compass; true when a read landed.</summary>
    private bool RefreshHeadingFromCompass()
    {
        if (_compass is not { Ready: true } || _compass.ReadLocDeg() is not double d) return false;
        _hdg = d * Math.PI / 180.0;
        _hdgValid = true;
        return true;
    }

    /// <summary>Set an ABSOLUTE view pitch without any keybind: pin the mouselook pitch at the
    /// top (it clamps there), then come down to (90° − wanted). Levitation riders sit ~10° above
    /// the horizon so they float over pits and water instead of steering down into them.</summary>
    private async Task PitchTo(double aboveHorizonDeg, CancellationToken ct)
    {
        if (!_sink.Ready) return;
        double px = _compass is { Ready: true, PxPerDeg: > 0.5 } ? _compass.PxPerDeg : _pxPerDeg;
        int up = (int)(120 * px);
        int down = (int)Math.Max(0, (90 - Math.Clamp(aboveHorizonDeg, -20, 88)) * px);
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        _rmbDown = true;
        try
        {
            for (int left = up; left > 0 && !ct.IsCancellationRequested && _sink.Ready; left -= 24)
            { InputProbe.MouseMoveRelative(0, -Math.Min(left, 24)); await Task.Delay(12, ct); }
            for (int left = down; left > 0 && !ct.IsCancellationRequested && _sink.Ready; left -= 24)
            { InputProbe.MouseMoveRelative(0, Math.Min(left, 24)); await Task.Delay(12, ct); }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); _rmbDown = false; }
    }

    /// <summary>Keep Levitate up: cast at the start of a run, when the log says it wore off, and
    /// on the safety timer — then settle the view just above the horizon.</summary>
    private async Task MaybeLev(CancellationToken ct)
    {
        if (!_s.LevEnabled || !_sink.Ready) return;
        InputKey k = InputKey.Parse(_s.LevCastKey);
        if (k.IsNone) return;
        bool timer = _s.LevRecastMinutes > 0 && _levAt != DateTime.MinValue
                     && (DateTime.Now - _levAt).TotalMinutes >= _s.LevRecastMinutes;
        if (_levAt != DateTime.MinValue && !_levNeeded && !timer) return;
        Log?.Invoke(_levAt == DateTime.MinValue ? "Casting Levitate (start of run)."
                  : _levNeeded ? "Levitate dropped — recasting." : "Levitate safety recast.");
        _sink.Send(k);
        _levNeeded = false;
        _levAt = DateTime.Now;
        await Task.Delay(Vary(3200), ct);                    // cast + settle
        await PitchTo(10, ct);
        _lastPitchFix = DateTime.Now;
    }

    /// <summary>Pit/water recovery: look well up, turn toward the last known good ground, and
    /// push forward — that swims up toward the coast we fell from and walks straight up any
    /// ladder we bump while looking upward. Gives up after 45s and accepts the new floor.</summary>
    private async Task Recover(CancellationToken ct)
    {
        Stats.State = "recovering — climbing out";
        DateTime began = DateTime.Now;
        // LOOK UP FIRST, AND STEEPLY. In EQ the swim direction follows the camera pitch, so a
        // camera left where the grind parks it — at the ground — means "forward" is DOWN, and the
        // recovery swims the character to the bottom. Hayden watched exactly that on 08-26: it
        // never tilted up. 55° was already too shallow to climb the one shelf that gets you out.
        await PitchTo(_fell ? 75 : 55, ct);
        if (ReEntryUsableHere() && _z is double z0 && _startZ is double s0 && z0 < s0 - DeepBelowAnchor)
            Log?.Invoke($"Deep water — swimming for the shore point at /loc {_s.ReEntryY:0.0}, {_s.ReEntryX:0.0} with the "
                      + "camera up, rather than back the way I fell in.");
        while (!ct.IsCancellationRequested && _sink.Ready && (DateTime.Now - began).TotalSeconds < 45)
        {
            RefreshHeadingFromCompass();
            // WHERE TO SWIM, and "back the way we came" is the wrong answer in the one place that
            // matters. Hayden: "Not all characters have levitate, so a method to get out of water
            // is also critical." The last good ground is wherever the character was standing when
            // it fell in — which, on a coastline, is a bank it may not be able to climb, and on
            // Kerra Isle is the deep side of the island. The re-entry point is the ONE spot the
            // user has told us the land is shallow enough to walk out of, so a character in the
            // water swims for that, and only falls back on the last good ground when nobody has
            // picked one. The point does double duty: shore to walk out at, and shore to come
            // back to after an eject. One number, picked once.
            bool swimToShore = ReEntryUsableHere() && _z is double zw && _startZ is double zs && zw < zs - DeepBelowAnchor;
            if (swimToShore && _hdgValid)
                await TurnBy(BearingErrorDegTo(_s.ReEntryX, _s.ReEntryY), ct);
            else if (_goodX is double gx && _goodY is double gy && _hdgValid)
                await TurnBy(BearingErrorDegTo(gx, gy), ct);
            await HoldKey(_fwd, 1000, ct);
            await FreshLoc(ct);
            if (_z is double z && _zGood is double zg && z >= zg - 12)
            {
                Log?.Invoke("Climbed back out — resuming the hunt.");
                _fell = false;
                await PitchTo(_s.LevEnabled ? 10 : 2, ct);
                Stats.State = "seeking";
                return;
            }
        }
        // WATER IS NOT GROUND, AND ADOPTING IT AS GROUND IS HOW THE CHARACTER DROWNS.
        //
        // The old line here shrugged and wrote the current altitude down as the new floor. On
        // Kerra Isle that floor was z −33 — the sea — recorded on 08-23 and again on 08-26, both
        // times immediately before the character died in it. Once the sea is "ground", every
        // later depth check passes, the recovery never fires again, and the bot happily walks the
        // seabed until it suffocates.
        //
        // A level far BELOW where the run started is water or a pit, never a new floor. The
        // honest move is to stop: the character is somewhere the navigator has no model of, and
        // every extra step is taken blind.
        if (_z is double nz && _startZ is double sz && nz < sz - DeepBelowAnchor)
        {
            Log?.Invoke($"I am at z {nz:0}, {sz - nz:0} below where this run started, and I could not climb out. "
                      + "That is water or a pit, not new ground — treating it as ground is what drowned the "
                      + "character before. Stopping here so it stays alive.");
            await PitchTo(_s.LevEnabled ? 10 : 2, ct);
            // UNLESS IT DIDN'T. This method spends up to 45 seconds in water, which is the single
            // most likely place in the run for the character to drown, and parking a CORPSE raises
            // Parked instead of Died: the session is held but the respawn window is never clicked,
            // so the character sits in it with no input until the server AFK-kicks the client an
            // hour later. That is the measured 08-24 chain, arrived at from a new direction.
            if (_selfDead) { Log?.Invoke("…and it didn't stay alive. Handing this to the death path."); return; }
            ParkSafely();
            return;
        }
        Log?.Invoke("Couldn't climb out the way we came — accepting this level as the new ground (watch me).");
        if (_z is double nz2) { _zGood = nz2; _goodX = _x; _goodY = _y; }
        _fell = false;
        await PitchTo(_s.LevEnabled ? 10 : 2, ct);
    }

    /// <summary>Fire the /loc key and wait for a fresh position line (max ~1.4s).</summary>
    private async Task<bool> FreshLoc(CancellationToken ct, int timeoutMs = 1400)
    {
        if (_loc.IsNone || !_sink.Ready) return false;
        long mark = DateTime.Now.Ticks;
        _sink.Send(_loc);
        _lastLoc = DateTime.Now;
        for (int i = 0; i < timeoutMs / 100; i++)
        {
            await Task.Delay(100, ct);
            if (Interlocked.Read(ref _locTicks) > mark) return true;
        }
        return false;
    }

    /// <summary>Signed degrees to turn so the current heading points at (tx, ty).</summary>
    private double BearingErrorDegTo(double tx, double ty)
    {
        if (_x is not double x || _y is not double y) return 0;
        double bearing = Math.Atan2(ty - y, tx - x);
        return NormDeg(Deg(bearing) - Deg(_hdg));
    }

    /// <summary>Stop the run, but hand the character over as a LIVE session rather than an
    /// abandoned one.</summary>
    private void ParkSafely()
    {
        // Stop() in a finally. The handler marshals with Dispatcher.Invoke, which throws once the
        // dispatcher is shutting down — and an exception between the two calls would leave _cts
        // uncancelled, Running true and Stopped never raised: the same zombie the death path was
        // fixed for in 0.10.55.
        try { Parked?.Invoke(); }
        catch (Exception ex) { Log?.Invoke("Park handler failed (stopping anyway): " + ex.Message); }
        finally { Stop(); }
    }

    /// <summary>
    /// Get the character into a NEW instance. Supplied by the owner, because everything it does is
    /// clicking buttons on the game's own UI and this class does not do windows — it drives a
    /// character. Returns true only on PROOF (a zone line), never on "I clicked something".
    ///
    /// Null, or ReInstanceEnabled off, and an eject parks the run exactly as 0.10.59 shipped it.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? ReInstance;

    /// <summary>One attempt per ejection. A failed re-entry parks; it does not sit in a loop
    /// clicking at a window that is not answering, unattended, for hours. Cleared in Start() as
    /// well as being fresh per instance, so this stays true if the class is ever reused.</summary>
    private bool _reInstanceTried;

    /// <summary>
    /// The whole point of the feature: an instance expired, the character has been dropped at the
    /// zone-in of the PUBLIC zone, and instead of standing there all night among other players it
    /// makes a new instance and walks back to camp.
    ///
    /// Order matters and each step is a refusal point:
    ///
    ///  1. Ask the owner to work the instance UI. It returns true only when the log has printed a
    ///     zone line — clicking "Enter" proves nothing, being somewhere else does.
    ///  2. LEVITATE FIRST, THEN PITCH. Hayden: "the camera just needs to be pointed above the
    ///     horizon and have levitate on. The character will never enter the water if this is the
    ///     case." Both halves are load-bearing and the pitch is the half this app kept getting
    ///     wrong: swim and float direction follow the camera, and the grind leaves the camera on
    ///     the ground, so "walk home" reads as "go down".
    ///  3. THE SHORE POINT FIRST, CAMP SECOND. The camp is inland; the straight line to it from
    ///     the zone-in crosses the sea. The re-entry point is the one place the land is shallow
    ///     enough to walk out, so it is a waypoint, not a nicety — going straight for camp is
    ///     precisely what drowned the character on 08-23 and 08-26.
    ///  4. Anything unproven parks. A character standing still in a new instance is a bad night;
    ///     a character walking somewhere nobody modelled is a dead one.
    /// </summary>
    private async Task ReInstanceAndReturn(CancellationToken ct)
    {
        _reInstanceTried = true;
        Stats.State = "re-instancing";
        Log?.Invoke("The instance expired and the character is at the zone-in of the public zone. Making a new "
                  + "instance rather than standing here — this takes a moment.");

        bool inside;
        try { inside = await ReInstance!(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log?.Invoke("Re-Instance failed with an error, so I'm stopping with the character alive: " + ex.Message);
            ParkSafely();
            return;
        }
        if (ct.IsCancellationRequested) return;

        if (!inside)
        {
            // The owner has already said WHY in its own words — a missing charge, a window it
            // could not read, a button it refused to press. Repeating a guess here would only
            // compete with the true reason.
            Log?.Invoke("I could not get into a new instance, so I'm stopping here with the character alive rather "
                      + "than hunting in the public zone. The reason is in the line above this one.");
            ParkSafely();
            return;
        }

        // A new instance is a new world: everything measured about the old one is now a lie. The
        // heading is stale, the last position is a thousand units away, and the fall detector's
        // idea of "ground" belongs to a zone we are no longer in.
        _teleported = false;
        _hdgValid = false;
        _fell = false;
        _deepReadings = 0;
        _goodX = null; _goodY = null; _zGood = null;
        _wildLocs = 0;
        // A ZONE SILENCES THE MELODY. _singing is cleared only by a log line saying the song
        // stopped, and a zone does not print one — so a bard would come back into the new instance
        // still believing it was singing, never press the melody key again, and run every fight to
        // the timeout with no output at all, for the rest of the night.
        _singing = false;
        // Nothing has been walked in this instance yet, so there is no movement to learn a heading
        // from, and the /loc that arrives next is a teleport's worth of distance away.
        _fwdMsSinceLoc = 0; _sideMsSinceLoc = 0;
        // A ledge dodge recorded in the old instance is not a fact about this one.
        _dip = false;

        Log?.Invoke("In a new instance. Getting levitate and the camera sorted before moving.");
        _levNeeded = true;                                   // a zone strips buffs; do not assume
        await MaybeLev(ct);
        await PitchTo(Math.Clamp(_s.ReturnPitchDeg, 2, 45), ct);
        _lastPitchFix = DateTime.Now;

        if (!await FreshLoc(ct))
        {
            Log?.Invoke("I'm in a new instance but I can't get a position fix, and walking without one is how the "
                      + "character ended up in the sea. Stopping here — it is safe and it is in an instance.");
            ParkSafely();
            return;
        }
        // _startZ IS DELIBERATELY LEFT ALONE. An instance is a copy of the same zone, so the camp
        // altitude this run started at is still the right reference for "am I under water?" — and
        // the zone-in is not: it is a dock or a rise somewhere else entirely. Re-baselining to it
        // was in the first draft and it breaks the depth guard in both directions. A zone-in 30
        // units above camp means every reading at camp reads as 30 units under water and the run
        // parks on dry land three passes after a successful return; 30 units below camp leaves the
        // guard that much less sensitive for the rest of the night, and Recover would refuse to
        // swim for the shore while actually drowning. _zGood, which means "the last ground I stood
        // on", is a different question and repopulates from the next reading on its own.

        if (ReEntryUsableHere())
        {
            Log?.Invoke($"Walking to the shore point at /loc {_s.ReEntryY:0.0}, {_s.ReEntryX:0.0} first — camp is inland "
                      + "and the straight line to it crosses water.");
            if (!await LegDone(await WalkTo(_s.ReEntryX, _s.ReEntryY, 25, 20, "Re-entry", ReturnWaterFloor(), ct),
                               "reach the shore point", ct)) return;
        }
        else
        {
            Log?.Invoke(_s.ReEntrySet
                ? $"The re-entry point that's saved belongs to {_s.ReEntryZone}, and this isn't it — so I'm not "
                  + "walking at those numbers here. Going straight for camp instead; set a re-entry point for "
                  + "this zone if there's water in the way."
                : "No re-entry point is picked, so I'll go straight for camp — pick one on the Grind page if "
                  + "there is water between the zone-in and your camp, because a straight line will cross it.");
        }

        if (_startX is not double cx || _startY is not double cy)
        { Log?.Invoke("Back in an instance, but this run has no camp anchor to return to — stopping here."); ParkSafely(); return; }

        Log?.Invoke("Now walking back to camp.");
        Stats.State = "re-instancing — returning to camp";
        double leash = Math.Max(30, _s.HuntTetherRadius * 0.5);
        if (!await LegDone(await WalkTo(cx, cy, leash, 30, "Return", ReturnWaterFloor(), ct),
                           $"close the last {DistanceTo(cx, cy):0} units to camp", ct)) return;

        _reInstanceTried = false;                            // a clean return re-arms it for next time
        Stats.State = "seeking";
        Log?.Invoke("Back at camp in a fresh instance — carrying on.");
    }

    /// <summary>
    /// Turn one leg of the walk home into "carry on" or "we're finished here", and — the part
    /// that matters — end it the RIGHT WAY.
    ///
    /// A death during the return is not a park. Parking raises <c>Parked</c>, which holds the
    /// session; a death has to raise <c>Died</c>, which clicks the respawn window as well. Report
    /// a drowning as a park and the character sits in the respawn window with no input until the
    /// server AFK-kicks the client an hour later — the measured chain from 08-24, arrived at by a
    /// different road.
    /// </summary>
    private async Task<bool> LegDone(WalkResult r, string what, CancellationToken ct)
    {
        switch (r)
        {
            case WalkResult.Arrived:
                return true;
            case WalkResult.Stopped:
                return false;
            case WalkResult.Died:
                // Loop's own death handling narrates and raises Died; it runs on the next pass.
                return false;
            case WalkResult.InWater:
                Log?.Invoke("I ended up in water on the way back, which is the thing this was meant to avoid. "
                          + "Trying to climb out, then stopping with the character where it is.");
                await Recover(ct);
                if (!ct.IsCancellationRequested && !_selfDead) ParkSafely();
                return false;
            default:
                Log?.Invoke($"I got back into an instance, but I couldn't {what}. Stopping with the character "
                          + "alive and in an instance.");
                ParkSafely();
                return false;
        }
    }

    /// <summary>Signed degrees to turn so the current heading points at the tether anchor.</summary>
    private double HomeErrorDeg()
        => _startX is double sx && _startY is double sy ? BearingErrorDegTo(sx, sy) : 0;

    /// <summary>Walk STRAIGHT back inside the tether: learn heading from /loc pairs if needed,
    /// turn toward the anchor, run a distance-sized burst, re-measure, correct. Closed loop —
    /// no more drifting further away on a blind turn.</summary>
    private async Task GoHome(CancellationToken ct, double r)
    {
        double away = TetherDistance();
        // Scaled off the leash the USER set, never a bare constant: with the default radius of
        // 300 a flat 250 would fire on every ordinary breach.
        double refuseAt = Math.Max(FarTetherStop, r * 3);

        // A CHARACTER A THOUSAND UNITS OUT DID NOT WALK THERE.
        //
        // Watched live on 08-26, and it is the same chain as 08-23: the instance expired, an NPC
        // said "Rrrrr… I remove you from ourrr peaceful island!", the character was teleported to
        // the instance entrance ~1000 units away, and this method dutifully set off in a straight
        // line toward a camp on an ISLAND — through the sea. It drowned, twice now.
        //
        // Straight-line homing is a drift corrector. It is right for the tens of units a fight
        // wanders, and it is catastrophic for a teleport, because the one thing it cannot do is
        // know that the direct line crosses water it can neither swim nor climb out of. So past
        // this distance it does not guess: it stops, says what it thinks happened, and hands the
        // character back intact. Getting home from an eject is Pathfinding's job — a recorded
        // route — and until that exists, standing still beats drowning.
        if (_teleported || away > refuseAt)
        {
            // The teleport FLAG is handled centrally in Loop now, so what reaches here is either
            // that same flag caught a moment earlier, or a distance nothing explains. Either way
            // Re-Instance is the better answer than standing still, when it is switched on.
            if (_s.ReInstanceEnabled && ReInstance is not null && !_reInstanceTried)
            { await ReInstanceAndReturn(ct); return; }

            Log?.Invoke($"I am {away:0} units from camp — that is a teleport, not drift, and it usually means the "
                      + "instance expired and put the character at the zone-in. I will NOT try to walk back: the "
                      + "straight line from here crosses water, and that is exactly how the character drowned on "
                      + "08-23 and again on 08-26. Stopping here with the character alive. Bring it back to camp "
                      + "and start the run again.");
            ParkSafely();
            return;
        }

        Stats.State = "tether — homing";
        Log?.Invoke($"Past the tether ({away:0} > {r:0}) — walking straight back.");
        bool blind = _loc.IsNone && (DateTime.Now.Ticks - Interlocked.Read(ref _locTicks)) > 12L * TimeSpan.TicksPerSecond;
        if (blind)
        {
            // No /loc source → we can't steer. Old behavior as a last resort, once.
            Log?.Invoke("Tether homing needs a /loc key (Grind settings) or a repeating /loc macro — doing a blind turn instead.");
            await TurnBy(150 * (_rng.Next(2) == 0 ? 1 : -1), ct);
            await HoldKey(_fwd, Vary(1100), ct);
            return;
        }
        if (_startX is not double hx || _startY is not double hy) return;
        switch (await WalkTo(hx, hy, r * 0.55, 8, "Homing", _startZ - DeepBelowAnchor, ct))
        {
            case WalkResult.Arrived: Stats.State = "seeking"; return;
            case WalkResult.Stopped: return;
            // The Loop watchdogs handle both of these on the very next pass, exactly as they did
            // before this method delegated its legs — so homing just yields to them.
            case WalkResult.Died:
            case WalkResult.InWater: return;
        }
        Log?.Invoke("Homing paused this pass (wall or bad reads) — will keep trying.");
    }

    /// <summary>
    /// Walk to a point, closed loop: measure, turn, run a distance-sized burst, re-measure,
    /// correct — and reverse the heading if the burst made things worse.
    ///
    /// LIFTED OUT OF GoHome UNCHANGED, not written fresh. This loop is the one piece of navigation
    /// in this class that has been in the field long enough to trust: it learns a heading from a
    /// /loc pair when the compass can't answer, it sizes each burst off the measured walking
    /// speed, and it catches a stale heading by noticing the distance grew. The instance return
    /// needs exactly that, and the honest way to get it is to share the code rather than to write
    /// a second, younger version of it and find out what it does at four in the morning.
    /// </summary>
    /// <returns>See <see cref="WalkResult"/>.</returns>
    private async Task<WalkResult> WalkTo(double tx, double ty, double arrive, int maxLegs, string what,
                                          double? waterBelowZ, CancellationToken ct)
    {
        int paused = 0;
        for (int leg = 0; leg < maxLegs && !ct.IsCancellationRequested; leg++)
        {
            // NOT FOCUSED IS A PAUSE, NOT A FAILURE. `_sink.Ready` means "EverQuest is the front
            // window"; everywhere else in this class losing it pauses the loop, and it was in this
            // method's for-condition, which quietly turned a click on the EQ Avatar window during
            // a five-minute walk home into "I couldn't reach the shore point — stopping."
            //
            // AND A PAUSE MUST NOT SPEND A LEG. `continue` in a for-loop still runs the increment,
            // so the first version merely made the same failure take twelve seconds instead of
            // none: click this app's window during the return, watch thirty legs tick past at 400
            // ms each without the character moving an inch, and get told the walk home failed.
            // The wait gets its own budget, measured in wall time, and gives the leg back.
            if (!_sink.Ready)
            {
                if (++paused > PausedPollsBeforeGivingUp)
                {
                    Log?.Invoke($"{what}: EverQuest hasn't been the front window for "
                              + $"{PausedPollsBeforeGivingUp * 400 / 1000}s, so I can't walk.");
                    return WalkResult.OutOfLegs;
                }
                await Task.Delay(400, ct);
                leg--;                                       // this pass did nothing; give it back
                continue;
            }
            paused = 0;

            // THE WATCHDOGS LIVE AT THE TOP OF Loop, AND THIS METHOD IS NOT IN Loop.
            //
            // A 30-leg walk is minutes of held-down movement, and for all of it the death check,
            // the fall recovery and the deep-water park were suspended. That is the exact hazard
            // this whole feature exists to remove: the return line clips the sea, the character
            // drowns, _fell and _selfDead are set on the log thread with nobody reading them, and
            // the walk finishes its thirty legs and reports "stopping with the character alive
            // and in an instance" over a corpse — with no respawn click and no session hold,
            // because Parked fired instead of Died. So each leg checks for itself.
            if (_selfDead) return WalkResult.Died;

            // FALL FIRST, DEEP SECOND — the order Loop uses, and its comment says why: the fall
            // threshold is lower than the depth one, so a depth test in front pre-empts every real
            // dunk and the climb-out never runs. Reversed here in the first draft, which made the
            // "climbed out, carrying on" path unreachable for exactly the case it was written for.
            if (_fell)
            {
                Log?.Invoke($"{what}: fell on the way. Climbing out before going on.");
                await Recover(ct);
                if (ct.IsCancellationRequested || _selfDead) return _selfDead ? WalkResult.Died : WalkResult.Stopped;
                continue;                                    // re-measure; the fall moved us
            }
            if (waterBelowZ is double floorZ && _z is double zw && zw < floorZ)
            {
                Log?.Invoke($"{what}: I am at z {zw:0}, below the {floorZ:0} I treat as water on this leg — "
                          + "not walking any further through it.");
                return WalkResult.InWater;
            }

            double before = DistanceTo(tx, ty);
            if (before <= arrive) { Log?.Invoke($"{what}: arrived ({before:0} ≤ {arrive:0})."); return WalkResult.Arrived; }
            RefreshHeadingFromCompass();                     // the compass makes this instant + exact
            if (!_hdgValid)
            {
                // Learn heading: a short forward stride bracketed by two fresh locs.
                await FreshLoc(ct);
                await HoldKey(_fwd, 700, ct);
                if (!await FreshLoc(ct)) { await Task.Delay(300, ct); }
                if (!_hdgValid) continue;                    // try another stride
            }
            await TurnBy(BearingErrorDegTo(tx, ty), ct);
            int run = (int)Math.Clamp(before / Math.Max(20, _speed) * 1000 * 0.8, 450, 2400);
            await HoldKey(_fwd, run, ct);
            await FreshLoc(ct);
            double after = DistanceTo(tx, ty);
            if (after > before + 5 && _hdgValid)
            {
                Log?.Invoke($"{what}: that leg went the wrong way ({before:0} → {after:0}) — reversing.");
                _hdg += Math.PI;                             // stale heading; flip and re-run the loop
            }
        }
        return ct.IsCancellationRequested ? WalkResult.Stopped : WalkResult.OutOfLegs;
    }

    /// <summary>Why a walk ended — because "false" was three different things and the callers were
    /// telling the user the wrong one of them.</summary>
    private enum WalkResult
    {
        Arrived,
        /// <summary>Ran out of legs still short of the target: a wall, bad reads, or just far.</summary>
        OutOfLegs,
        /// <summary>Standing in water well below where the run started. Do not keep walking.</summary>
        InWater,
        /// <summary>The character died on the way. The DEATH path has to run, not the park path.</summary>
        Died,
        /// <summary>The run was cancelled (F12, or the role stopping).</summary>
        Stopped,
    }

    /// <summary>
    /// The altitude below which, on the way home, the character is in the sea rather than on the
    /// ground.
    ///
    /// NOT camp's altitude, which is what Loop's own depth guard uses and what the first draft of
    /// the return walk used. The re-entry point is BY DEFINITION at the waterline — Hayden's is
    /// z 4.60, "the one spot where the land is shallow enough to walk out" — so an inland camp
    /// more than DeepBelowAnchor above it makes the shore itself read as drowning. The walk would
    /// have aborted on dry sand, every time, and parked: a feature that never once completed a
    /// return, failing in the most confusing way available.
    ///
    /// So the floor is the LOWER of the two known-good grounds, minus the same margin. Anything
    /// under the shallowest place a character can stand is sea, and nothing above it is.
    /// </summary>
    private double? ReturnWaterFloor()
    {
        double? camp = _startZ;
        double? shore = ReEntryUsableHere() ? _s.ReEntryZ : null;
        if (camp is null && shore is null) return null;
        double lowest = camp is double c && shore is double sh ? Math.Min(c, sh) : (camp ?? shore)!.Value;
        return lowest - DeepBelowAnchor;
    }

    /// <summary>Flat distance from where we last measured ourselves to a point, or 0 when we do
    /// not know where we are — the same "0 means unknown" convention TetherDistance uses, and for
    /// the same reason: every caller treats 0 as "nothing to do".</summary>
    private double DistanceTo(double tx, double ty)
    {
        if (_x is not double x || _y is not double y) return 0;
        double dx = x - tx, dy = y - ty;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Does the last con line name a mob on the directive target list?</summary>
    private bool ConMatchesTargets()
    {
        if (_targets.Count == 0) return false;
        string con = _lastConText.ToLowerInvariant();
        foreach (string t in _targets) if (con.Contains(t)) return true;
        return false;
    }

    /// <summary>
    /// May the saved re-entry point be used where the character is standing?
    ///
    /// Yes if it was saved without a zone (an older settings file — believe the user rather than
    /// silently retiring a point they set on purpose), or if the zone matches. No if we can see
    /// that it belongs somewhere else: a coordinate from another zone is a random point on this
    /// one's map, and this app's whole recent history is about not walking at numbers it cannot
    /// vouch for.
    /// </summary>
    private bool ReEntryUsableHere()
    {
        if (!_s.ReEntrySet) return false;
        string saved = (_s.ReEntryZone ?? "").Trim();
        if (saved.Length == 0) return true;
        string? here = ZoneTable.ShortFor(_heat.Current ?? "") ?? _planZone ?? _fallbackZone?.Invoke();
        if (string.IsNullOrWhiteSpace(here)) return true;    // can't tell — the user's pick wins
        return string.Equals(saved, here, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Distance from the tether anchor, or 0 when unknown.</summary>
    private double TetherDistance()
    {
        if (_startX is not double sx || _startY is not double sy || _x is not double x || _y is not double y) return 0;
        double dx = x - sx, dy = y - sy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_selfDead)
                {
                    // Order matters: narrate, count, hand the corpse to the owner (respawn click +
                    // session hold live there — this class has no window handle and no screen), and
                    // only then tear down. The old `break` left a ZOMBIE: the loop exited but _cts
                    // was never cancelled, so Running stayed true, the grind timer kept painting,
                    // and Stopped never fired — the app spent the rest of the night insisting a
                    // dead character was hunting. Stop() does the whole teardown and is idempotent.
                    Log?.Invoke("Death detected — combat and navigation stopped.");
                    Stats.Deaths++;
                    Died?.Invoke();
                    Stop();
                    break;
                }
                if (!_sink.Ready) { Stats.State = "paused (EQ not focused)"; await Task.Delay(400, ct); continue; }

                // AN EJECT IS NOT A TETHER PROBLEM, and treating it as one hid it from two whole
                // modes. `_teleported` was only ever acted on inside GoHome, which is reached from
                // the roam tether and the camp scan — so a WAYPOINTS patrol that got ejected
                // walked its recorded route around the PUBLIC zone, among other players, all night,
                // with the flag set and nobody reading it. It belongs here, where every mode passes.
                if (_teleported)
                {
                    if (_s.ReInstanceEnabled && ReInstance is not null && !_reInstanceTried)
                    { await ReInstanceAndReturn(ct); continue; }
                    // AND IF THERE IS NOTHING LEFT TO TRY, PARK — do not fall through. The first
                    // version only parked when _reInstanceTried was false, so a second teleport
                    // after one attempt hit an empty branch: no re-instance, no park, no message,
                    // and the run carried on hunting in the PUBLIC zone with the flag set and
                    // nobody reading it. That is the exact failure this block was added to close.
                    Log?.Invoke(_reInstanceTried
                        ? "Teleported again, and I've already used my one re-instance attempt this run. Stopping "
                        + "here with the character alive rather than hunting on in the public zone."
                        : "Something teleported the character — almost always the instance expiring and putting it "
                        + "at the zone-in. I will not walk back from a teleport: the straight line crosses water "
                        + "and that is how the character drowned on 08-23 and 08-26. Stopping here, alive. Turn on "
                        + "Re-Instance if you want it to make a new instance and walk back on its own.");
                    ParkSafely();
                    break;
                }

                await MaybeLev(ct);                          // keep Levitate up + view above horizon
                if (_fell) { await Recover(ct); continue; }  // fell into a pit / water → climb out first

                // DEEP AND NOT FALLING — the gradual wade the fall detector cannot see.
                //
                // It has to sit AFTER the recovery, not before it: the fall threshold is 18 and
                // this one is 25, so in front it pre-empted every real dunk, Recover never got
                // its climb-out, and "Climbed back out — resuming the hunt" became unreachable
                // for exactly the case it was written for. What is left for this check is the
                // case Recover genuinely cannot see: `_zGood` re-baselines on any drop of 8 or
                // less, so a shelving beach walks the baseline down step by step and `_fell`
                // never sets at all.
                //
                // SUSTAINED, because one deep reading is also what a ravine or a dungeon ramp
                // looks like in passing, and parking a run on a bad step would be its own bug.
                if (_z is double zNow && _startZ is double zAnchor && zNow < zAnchor - DeepBelowAnchor)
                {
                    if (++_deepReadings >= 3)
                    {
                        Log?.Invoke($"I have been at z {zNow:0} for {_deepReadings} readings, {zAnchor - zNow:0} below "
                                  + "where this run started, without the fall detector ever firing — that is a wade "
                                  + "into water, and walking blind in it is what drowned the character before. "
                                  + "Stopping here with it alive.");
                        await PitchTo(_s.LevEnabled ? 10 : 2, ct);
                        ParkSafely();
                        break;
                    }
                }
                else _deepReadings = 0;

                // DEFENSIVE stance: hold position like a defensive pet — no roaming, no pulling.
                // The rotation only fires once something swings at us.
                if (Stance == "defensive")
                {
                    if (!_attacked)
                    {
                        Stats.State = "on guard (defensive)";
                        await MaybeLoc(ct);
                        await Task.Delay(400, ct);
                        continue;
                    }
                    _attacked = false;
                    Log?.Invoke("Attacked — fighting back (defensive stance).");
                    _sink.Send(_target);                    // target whatever is on us
                    await Task.Delay(Vary(300), ct);
                }
                else
                {
                    // 1) SEEK — move per the selected hunt mode, then target + consider
                    Stats.State = "seeking";
                    await MaybeLoc(ct);
                    switch (Mode)
                    {
                        case "camp": await CampScan(ct); break;              // hold this spot
                        case "waypoints": await NavigateWaypoints(ct); break; // patrol the route
                        default: await Wander(ct); break;                     // hunt / zone roam
                    }
                    if (!_sink.Ready) continue;
                    // The mode call above can have parked the role (a teleport, or water under
                    // us). Without this the loop sent one more Tab into the game AFTER the role
                    // had announced it stopped and torn itself down.
                    if (ct.IsCancellationRequested) break;
                    _sink.Send(_target);                    // target nearest NPC (Tab by default)
                    await Task.Delay(Vary(350), ct);

                    // Don't /consider thin air. Tab finds nothing far more often than it finds a
                    // mob, and conning anyway meant a stream of con attempts every pass. With the
                    // target window picked we can just LOOK at whether something is selected.
                    if (!await HaveTarget(ct)) continue;

                    _lastCon = ConsiderDifficulty.Unknown;
                    _lastAttitude = ConsiderAttitude.Unknown;
                    _lastConText = "";
                    _sink.Send(_con);                       // /consider the target (key or mouse5)
                    // WAIT FOR THE ANSWER, don't sleep through it — and don't guess how long that
                    // takes either. A fixed wait is wrong in both directions: too short and the
                    // pass gives up on a con that was already sitting in the log file unread, roams,
                    // and tries again — six to eight seconds to engage a mob that answered the first
                    // time. Too long and every engagement pays for the worst case.
                    //
                    // So it is measured. The round trip is whatever this machine's game, disk and
                    // log tailer add up to, the window is sized from what has actually been observed,
                    // and a machine that answers in 90 ms never waits like one that answers in 800.
                    DateTime asked = DateTime.Now;
                    while ((DateTime.Now - asked).TotalMilliseconds < _conWaitMs)
                    {
                        if (_lastCon != ConsiderDifficulty.Unknown || _lastAttitude != ConsiderAttitude.Unknown)
                        {
                            LearnConLatency((DateTime.Now - asked).TotalMilliseconds);
                            break;
                        }
                        await Task.Delay(40, ct);
                    }

                    // No con line came back → nothing was targeted. Don't flail at empty air: roam again.
                    if (_lastCon == ConsiderDifficulty.Unknown && _lastAttitude == ConsiderAttitude.Unknown)
                    { Stats.State = "no target — roaming"; Log?.Invoke("No target — roaming for a mob."); continue; }

                    if (_s.HuntSkipHardCons && _lastCon == ConsiderDifficulty.Suicidal)
                    { Stats.Skipped++; Log?.Invoke("Skipping a too-hard target."); continue; }

                    // Hostile-only: only mobs that scowl or glare threateningly are fair game.
                    if (_s.HuntHostileOnly && _lastAttitude != ConsiderAttitude.Scowls && _lastAttitude != ConsiderAttitude.Threatening)
                    { Stats.Skipped++; Log?.Invoke($"Skipping — con reads {_lastAttitude}, not hostile."); continue; }

                    // DIRECTIVE stance: only mobs on the target list get engaged.
                    if (Stance == "directive" && !ConMatchesTargets())
                    { Stats.Skipped++; Log?.Invoke("Skipping — not on the directive target list."); continue; }
                }

                // A BEAT BEFORE THE FIRST KEY, and a different one every time.
                //
                // Not politeness — the con has already landed and the mob is hostile, so this is
                // pure delay. It is here because a bot that fires its opener the same number of
                // milliseconds after every con is describing itself, and because a human takes a
                // moment. Random up to the configured cap, which is itself capped at two seconds:
                // longer than that and the mob has wandered off before the fight starts.
                if (Stance != "defensive")
                {
                    int cap = Math.Clamp(_s.HuntEngageMaxMs, 200, 2000);
                    await Task.Delay(_rng.Next(Math.Min(150, cap), cap + 1), ct);
                }

                // 2) FIGHT — run the rotation until the mob dies / we die / timeout
                Stats.State = "fighting"; Stats.Fights++;
                _mobDead = false; _cantSee = false; _tooFar = false;
                DateTime fightStart = DateTime.Now, lastOut = DateTime.Now;
                int i = 0, sweep = 0, swingsSeen = _ourSwings, unreachable = 0;
                int meleeSeen = _meleeSwings, autoAtStart = _autoSwings;
                DateTime lastBlocked = DateTime.MinValue;
                // The auto-attack grace runs from when we could actually SWING, not from when the
                // fight began — see the nudge check below.
                DateTime meleeGraceFrom = DateTime.Now;
                _autoAttackTried = false;
                _autoAttackOn = null;
                bool castOnly = _s.GrindCastOnly;
                if (castOnly) Stats.State = "fighting — casting";
                while (!ct.IsCancellationRequested && !_mobDead && !_selfDead)
                {
                    if (!_sink.Ready) { await Task.Delay(300, ct); continue; }
                    if ((DateTime.Now - fightStart).TotalSeconds > _s.HuntMaxFightSeconds)
                    { Log?.Invoke("Fight timed out — moving on."); break; }

                    if (_s.GrindBardMode)
                    {
                        // Bard melody: fire the melody hotkey ONCE and let it sing. Recast only
                        // when the log said it stopped (stun / fizzled note / song end).
                        if (!_singing)
                        {
                            (InputKey mk, int _) = _rotation.Count > 0 ? _rotation[0] : (InputKey.FromVk(0x34), 0);
                            _sink.Send(mk);
                            _singing = true; _melodyAt = DateTime.Now;
                            Log?.Invoke("Melody cast — holding until the log says it stopped.");
                        }
                        await Task.Delay(Vary(500), ct);
                    }
                    else
                    {
                        (InputKey key, int delay) = _rotation.Count > 0 ? _rotation[i % _rotation.Count] : (InputKey.FromVk(0x34), 1400); // default '4'
                        _sink.Send(key);
                        i++;
                        await Task.Delay(Vary(Math.Max(50, delay)), ct);
                    }

                    // FACING FIX: if our hits are landing, all good. If the log says we can't see
                    // the target / it's out of reach — or nothing lands for a few seconds — turn in
                    // a widening sweep (and close distance) until our swings start printing.
                    // ── WHAT EACH KIND OF EVIDENCE IS ALLOWED TO RESET ────────────────────────
                    // These used to be one branch, and merging them was safe only for as long as
                    // spell damage never actually matched anything. Now that a bard's melody
                    // reaches _ourSwings several times a fight, one branch would have handed a
                    // song TICK the authority to clear the two counters that end a hopeless fight:
                    //
                    //   • A song is not target-scoped. It keeps ticking on the mob that fled behind
                    //     a wall while we stand in front of a NEW one that nothing can reach. Every
                    //     tick zeroed `unreachable`, so the "out of range or line of sight" drop —
                    //     which fires at 2 and used to end that fight in about a second — could
                    //     never reach 2 at all.
                    //   • A tick says nothing about which way we are POINTING. Every tick zeroed
                    //     `sweep` and refreshed the clock the facing sweep runs off, so a melee
                    //     hybrid with a DoT up would stand facing a wall for the full
                    //     HuntMaxFightSeconds instead of turning to find the mob after 3.2s.
                    //
                    // Both of those are the 10-12 seconds a kill coming back in a new costume, in
                    // the exact mode this release speeds up. So: each fact resets only what it is
                    // actually evidence of.

                    // OUR SPELL OR SONG LANDED — proof the cast connected, and that we are aimed
                    // well enough to cast. It refreshes the give-up clock and calms the facing
                    // sweep, and it does NOT touch `unreachable`, which is the one thing a song
                    // tick genuinely cannot speak to: a melody keeps ticking on the mob that fled
                    // behind a wall while we stand in front of a new one nothing can reach.
                    //
                    // The clock is refreshed in BOTH modes, and the first draft of this gated it on
                    // castOnly to protect a melee hybrid from a background DoT holding the sweep
                    // off. That trade was backwards. A wizard grinds with GrindCastOnly UNCHECKED
                    // — it defaults off — so the gate left the clock with no source at all for
                    // every pure caster: the sweep fired at 3.2s, refreshed the clock itself,
                    // fired again, and escalated to turning 180° every three seconds for the whole
                    // fight while the nukes landed perfectly. Certain, universal and every fight,
                    // against a hybrid case that needs a DoT AND bad facing and is capped at
                    // HuntMaxFightSeconds anyway.
                    if (_ourSwings != swingsSeen)
                    { swingsSeen = _ourSwings; sweep = 0; lastOut = DateTime.Now; }

                    // OUR WEAPON MOVED — proof of facing AND reach, because a swing that connects
                    // (or misses, or is parried) had to be aimed at something within arm's length.
                    // That is what the sweep and the range counter are asking about, so this is the
                    // fact that is allowed to clear them.
                    if (_meleeSwings != meleeSeen)
                    {
                        meleeSeen = _meleeSwings; sweep = 0; unreachable = 0; lastOut = DateTime.Now;
                        // A STREAK, and it has to be cleared by the thing that breaks it. Left to
                        // accumulate it becomes a running total over a whole night and the message
                        // that quotes it — "that's 5 fights RUNNING" — becomes a lie that sends the
                        // user to re-check a strip that is working.
                        // AUTO swings, to match the branch that raises it: that warning counts
                        // fights where the border claimed attack was on and CONTINUOUS attack never
                        // swung, so a kick landing must not clear it.
                        if (_autoSwings != autoAtStart) _borderOnNoSwing = 0;
                    }

                    // AUTO ATTACK DIDN'T ENGAGE. The rotation is meant to start it — most clients
                    // have a "begin auto attack when you cast at a hostile" option — and when that
                    // works the melee lines start inside a swing timer. So: only in melee mode,
                    // only after long enough that silence means something, only when nothing has
                    // swung AND the log hasn't said attack is on, and only once.
                    // OUT OF REACH RESTARTS THE CLOCK. Now that every melee outcome reaches the
                    // counter — hit, miss, dodge, parry, riposte, block, every weapon skill — the
                    // only way to see no melee lines with attack RUNNING is for no swing to have
                    // been attempted, and the ordinary cause of that is standing too far away. A
                    // mob targeted at range and closed on over the first few seconds would
                    // otherwise trip this and toggle off an attack that was already engaged.
                    // Silence is only evidence once we have been in a position to swing.
                    // "YOU CANNOT SEE YOUR TARGET" IS PROOF THAT SOMETHING IS SWINGING. The client
                    // only prints it because an attack was attempted and couldn't land — so it says
                    // auto attack is ON and the facing is wrong, which is the opposite of what
                    // silence would have suggested. Same for out-of-range. Both restart the clock:
                    // silence only means anything once we are in a position to actually swing.
                    if (_tooFar || _cantSee) meleeGraceFrom = DateTime.Now;
                    if (!_autoAttackTried && !castOnly && !_s.GrindBardMode && !_autoAtk.IsNone
                        && _autoSwings == autoAtStart && _autoAttackOn != true
                        && (DateTime.Now - meleeGraceFrom).TotalSeconds > AutoAttackGrace)
                    {
                        // THE INDICATOR DECIDES WHEN IT CAN. Everything above is inference from an
                        // absence; this is a look at the thing itself. A clear "it's on" cancels the
                        // press outright — and cancels it for the rest of the fight, because the
                        // reason won't have changed.
                        // Costs up to three seconds, once per fight, and only in a fight that has
                        // already gone wrong — nothing has swung for two and a half seconds while
                        // in range. A flashing border answers in well under a second because the
                        // watch stops on the third edge; only the silent case pays the full window,
                        // and that is exactly the case that must not be rushed.
                        bool? border = await FlashSaysAttackOn(ct);
                        if (border == true)
                        {
                            _autoAttackTried = true;
                            if (_narrateBorder)
                            {
                                _narrateBorder = false;
                                Log?.Invoke("Nothing has swung, but the attack border IS flashing — so auto attack is "
                                          + "already on and this is facing or reach. Leaving your key alone.");
                            }
                            // THE OTHER WAY THIS STRIP CAN BE WRONG, and it is invisible from inside.
                            //
                            // The quiet check is taken OUT of combat; this verdict is taken IN it. So
                            // anything red that only happens while fighting — a damage tint, an aggro
                            // glow, a proc — passes the check and then reads "attack is on" for every
                            // fight of the night, and the feature is off behind a green tick.
                            //
                            // Deliberately NOT answered by pressing the key: the whole point of this
                            // branch is that we have been told attack is on, and acting against that
                            // on a suspicion is how a working fight gets switched off. What it can do
                            // is say so, once, with the one instruction that would settle it.
                            if (++_borderOnNoSwing == BorderOnNoSwingSuspect)
                                Log?.Invoke($"That's {BorderOnNoSwingSuspect} fights running where the border said attack "
                                          + "was on and nothing swung. If you ARE attacking, this is facing or reach and "
                                          + "it's fine. If you are not, that strip is picking up something else that only "
                                          + "goes red in combat — stand out of combat and press \u201Ccheck: attack OFF\u201D "
                                          + "on the Grind page to find out which.");
                        }
                        else NudgeAutoAttack(border is false);
                    }

                    if (castOnly)
                    {
                        // CAST / SING ONLY: spells and songs don't care which way we're pointing, so
                        // every melee correction is dead time — no facing sweeps, no closing bursts.
                        // The rotation just keeps firing. When EQ says the target is out of reach or
                        // out of sight we don't fix it, we drop it: the seek phase will walk us to a
                        // mob we can actually hit, which is faster than pivoting at this one.
                        // TWO BLOCKS CLOSE TOGETHER, not two blocks all fight. Nothing resets this
                        // counter in cast-only any more (there are no melee swings to reset it, and
                        // a song tick is not evidence the CURRENT target is reachable), so without a
                        // decay it is a running total: a mob that pathed behind a pillar at t+2s and
                        // again at t+15s, damage landing the whole time, would be dropped at 20%
                        // health with the log claiming it was out of range.
                        bool blocked = _cantSee || _tooFar;
                        _cantSee = false; _tooFar = false;
                        if (blocked)
                        {
                            if ((DateTime.Now - lastBlocked).TotalSeconds > BlockedTogetherSeconds) unreachable = 0;
                            unreachable++; lastBlocked = DateTime.Now;
                        }
                        double giveUp = Math.Max(3, _s.GrindCastGiveUpSeconds);
                        bool quiet = (DateTime.Now - lastOut).TotalSeconds > giveUp;
                        if (unreachable >= 2 || quiet)
                        {
                            Log?.Invoke(unreachable >= 2
                                ? "Cast-only: target is out of range or line of sight — dropping it and finding another."
                                : $"Cast-only: nothing landed in {giveUp:0}s — dropping this target.");
                            Stats.Skipped++;
                            break;
                        }
                    }
                    else
                    {
                        if (_tooFar)
                        { _tooFar = false; Stats.State = "fighting — closing in"; await HoldKey(_fwd, Vary(430), ct); }
                        // Bard songs tick slower than melee swings — give them a longer quiet window.
                        double noOut = _s.GrindBardMode ? 6.5 : 3.2;
                        if (_cantSee || (DateTime.Now - lastOut).TotalSeconds > noOut)
                        {
                            bool sawIt = _cantSee; _cantSee = false;
                            double[] scan = { 60, -90, 120, -150, 180, 180 };
                            double a = scan[Math.Min(sweep, scan.Length - 1)]; sweep++;
                            Stats.State = "fighting — facing target";
                            Log?.Invoke((sawIt ? "Can't see the target" : "No hits landing") + $" — turning {a:0}° to face it.");
                            await TurnBy(a, ct);
                            lastOut = DateTime.Now;
                            Stats.State = "fighting";
                        }
                    }
                }
                if (_mobDead) { Stats.Kills++; Log?.Invoke($"Kill #{Stats.Kills}."); }
                _attacked = false;                          // a fight resolves the defensive trigger

                // 3) REST — need-based when the HUD bars are set up, blind timer when they aren't.
                await Rest(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // AND STOP, or the role is a ZOMBIE: _cts uncancelled, Running still true, Stopped
            // never raised, the grind timer painting a character that is doing nothing, and
            // Start() refusing to begin another run for the rest of the session. The same shape
            // the death path was fixed for in 0.10.55, and reachable again the moment this loop
            // started calling out to code that touches windows.
            Log?.Invoke("Hunt error: " + ex.Message);
            Stop();
        }
        finally { ReleaseKeys(); }
    }

    /// <summary>Is anything actually targeted right now?
    ///
    /// Answers from the target window on screen when it's been picked, because EQ's log says
    /// nothing at all about targeting — pressing Tab into empty air is indistinguishable from
    /// pressing it at a mob until something else gives it away. An unreadable window (or one that
    /// was never picked) returns TRUE, so the role keeps its old behaviour of conning to find out
    /// rather than standing there refusing to try.
    ///
    /// Also gives the target key a second chance: Tab often needs a moment after a turn, so a
    /// negative read is retried once before we give up and go roam.</summary>
    private async Task<bool> HaveTarget(CancellationToken ct)
    {
        if (!_s.TargetGateEnabled || _vitals is not { HasTargetBox: true } v) return true;
        double need = TargetNeed(_s);

        if (v.HasTarget(need) is not bool got) return true;   // can't read it — carry on as before
        if (got) { _noTargetRuns = 0; return true; }

        // One retry: press again and give the UI a beat to draw.
        _sink.Send(_target);
        await Task.Delay(Vary(320), ct);
        if (v.HasTarget(need) is not bool again || again) { _noTargetRuns = 0; return true; }

        _noTargetRuns++;

        // SAFETY VALVE. A fingerprint taken with nothing actually targeted, a target window that
        // has since moved, a UI reskin, a resolution change or anything sitting over that corner
        // of the screen all read as "no target" forever — and a bot that never cons is a bot that
        // never fights. After a long dry run, stop trusting the picture and go back to conning to
        // find out, loudly, rather than roaming all night with nothing to show for it.
        if (_noTargetRuns >= NoTargetGiveUp)
        {
            _noTargetRuns = 0;
            Log?.Invoke($"No target seen in {NoTargetGiveUp} passes — that usually means the target-window pick is wrong "
                      + "(picked without a mob targeted, or the window has moved). Considering anyway; re-pick it on the Grind page.");
            return true;
        }

        Stats.State = "nothing targeted — roaming";
        // Say it occasionally, not every pass: the whole point of this gate is less noise.
        if (_noTargetRuns == 1 || _noTargetRuns % 25 == 0)
            Log?.Invoke($"Nothing in range to target — roaming{(_noTargetRuns > 1 ? $" ({_noTargetRuns} passes)" : "")}.");
        return false;
    }

    /// <summary>Consecutive empty passes before we assume the target-window pick is broken rather
    /// than the zone being empty. ~20 passes is a minute or two of roaming.</summary>
    private const int NoTargetGiveUp = 20;

    /// <summary>The match fraction that counts as "targeted". Shared with the UI's Test button so
    /// the verdict she prints can never disagree with the one she acts on.</summary>
    public static double TargetNeed(AppSettings s) => Math.Clamp(s.TargetMatchPercent, 10, 100) / 100.0;

    /// <summary>Rest between fights.
    ///
    /// With the HP/mana bars picked, this is need-based: a character that finished the fight at
    /// full simply doesn't sit around, and one that took a beating rests until it's actually
    /// recovered rather than for an arbitrary count of seconds. Without the bars (or with gating
    /// switched off) it falls back to the old blind <see cref="AppSettings.HuntRestSeconds"/> pause,
    /// because the log carries no HP or mana and guessing is worse than waiting.</summary>
    private async Task Rest(CancellationToken ct)
    {
        int blind = Math.Max(0, _s.HuntRestSeconds);
        if (!_s.RestGateEnabled || _vitals is not { Ready: true } v)
        {
            Stats.State = "resting";
            for (int t = 0; t < blind && !ct.IsCancellationRequested; t++) await Task.Delay(1000, ct);
            return;
        }

        double hpWant = Math.Clamp(_s.RestHpPercent, 0, 100) / 100.0;
        double manaWant = Math.Clamp(_s.RestManaPercent, 0, 100) / 100.0;

        // Both readable and both healthy → straight back to hunting, no pause at all.
        (bool need, string why, string reading) = Vitals(v, hpWant, manaWant);
        if (!need) { Stats.State = "healthy — hunting on"; return; }

        Log?.Invoke($"Resting — {why}.");
        int cap = Math.Max(5, _s.RestMaxSeconds);
        int rested = 0;
        while (!ct.IsCancellationRequested && rested < cap)
        {
            if (_selfDead) return;
            Stats.State = "resting — " + reading;
            await Task.Delay(1000, ct);
            // Time spent tabbed away isn't rest and isn't recovery — don't spend the cap on it.
            if (!_sink.Ready) continue;
            rested++;
            if (_attacked) { Log?.Invoke("Something attacked while resting — back up."); return; }
            (need, _, reading) = Vitals(v, hpWant, manaWant);
            if (!need) { Log?.Invoke($"Recovered ({reading}) — hunting on."); return; }
        }
        Log?.Invoke($"Rest hit the {cap}s cap at {reading} — carrying on anyway.");
    }

    /// <summary>One read of both bars: is either below its threshold, why, and a printable form.
    /// A bar that can't be read right now counts as fine, so an occluded or mis-picked bar can
    /// never wedge the bot in a permanent rest — it just falls back to hunting.</summary>
    private static (bool need, string why, string reading) Vitals(Ocr.VitalsReader v, double hpWant, double manaWant)
    {
        double? hp = v.HealthFraction(), mana = v.ManaFraction();
        string reading = (hp is double a ? $"hp {a * 100:0}%" : "hp —") + " · " + (mana is double b ? $"mana {b * 100:0}%" : "mana —");
        if (hpWant > 0 && hp is double h && h < hpWant)
            return (true, $"health {h * 100:0}% is under {hpWant * 100:0}%", reading);
        if (manaWant > 0 && mana is double m && m < manaWant)
            return (true, $"mana {m * 100:0}% is under {manaWant * 100:0}%", reading);
        return (false, "", reading);
    }

    /// <summary>Periodically fire the user's /loc macro key so position stays live for bounds/heatmap.
    /// A tight tether, a camp, or a waypoint run needs a fast fix (~2–3s).</summary>
    private async Task MaybeLoc(CancellationToken ct)
    {
        if (_loc.IsNone || !_sink.Ready) return;
        int every = Math.Max(2, _s.HuntLocEverySeconds);
        if ((_s.HuntTetherEnabled && _s.HuntTetherRadius <= 100) || Mode is "camp" or "waypoints")
            every = Math.Min(every, 3);
        if ((DateTime.Now - _lastLoc).TotalSeconds < every) return;
        _sink.Send(_loc);
        _lastLoc = DateTime.Now;
        await Task.Delay(Vary(120), ct);
    }

    /// <summary>CAMP mode: hold this exact spot (hazards all around) — never stride away. Turn in
    /// place to scan for spawns; only ever MOVE to shuffle back inside a ~12-unit circle around
    /// where the run started. Targeting/fighting (or the bard melody) does the rest.</summary>
    private async Task CampScan(CancellationToken ct)
    {
        if (_startX is not null && TetherDistance() > 12)
        {
            Stats.State = "camp — shuffling back";
            await GoHome(ct, 12);
            return;
        }
        Stats.State = "camp — holding position";
        if (_rng.NextDouble() < 0.6)
            await TurnBy((_rng.Next(2) == 0 ? 1 : -1) * _rng.Next(35, 95), ct);   // scan for the respawn
        await Task.Delay(Vary(500), ct);
    }

    /// <summary>WAYPOINTS mode: run the route drawn on the Maps page — closely but never exactly
    /// (each leg aims at the waypoint ± a wobble, speeds vary, sometimes it just pauses like a
    /// person checking the area). Fights still happen along the way per stance.</summary>
    private async Task NavigateWaypoints(CancellationToken ct)
    {
        ZonePlan? plan = CurrentPlan();
        if (plan is null || plan.Waypoints.Count < 2)
        {
            if (!_noPlanWarned)
            {
                _noPlanWarned = true;
                string where = _planZone ?? ZoneTable.ShortFor(_heat.Current ?? "") ?? _fallbackZone?.Invoke() ?? "an unidentified zone";
                Log?.Invoke($"Waypoints mode, but \"{where}\" has {(plan?.Waypoints.Count ?? 0)} saved waypoint(s) — a route needs at least 2. "
                          + "Open the Maps page, make sure the zone shown is the one you're standing in, and draw the route there. Roaming instead.");
            }
            await Wander(ct);
            return;
        }
        if (_x is not double x || _y is not double y)
        {
            // No position = no navigation. This is THE thing that quietly turns a waypoint run
            // into aimless walking, so say it out loud (once) instead of shuffling forward.
            if (!_noLocWarned)
            {
                _noLocWarned = true;
                Log?.Invoke(_loc.IsNone
                    ? "Waypoints need to know where you are, and no /loc key is set — put your /loc macro key in the Grind settings (or keep a repeating /loc macro running in-game). Walking blind until then."
                    : "Waypoints are waiting on the first /loc fix — check that your /loc key really prints a location line to the log.");
            }
            await FreshLoc(ct);
            await HoldKey(_fwd, Vary(500), ct);
            return;
        }
        _noLocWarned = false;

        if (!_wpHave) NextWaypoint(plan, announce: true);
        double dist = Math.Sqrt((x - _wpTx) * (x - _wpTx) + (y - _wpTy) * (y - _wpTy));
        if (dist < 18)
        {
            Log?.Invoke($"Waypoint {_wpIndex + 1}/{plan.Waypoints.Count} reached.");
            NextWaypoint(plan, announce: false);
            if (_rng.NextDouble() < 0.35) { Stats.State = "waypoints — pausing"; await Task.Delay(Vary(900), ct); }
        }
        Stats.State = $"waypoints — to #{_wpIndex + 1}";
        RefreshHeadingFromCompass();
        if (!_hdgValid)
        { await FreshLoc(ct); await HoldKey(_fwd, 600, ct); await FreshLoc(ct); }
        if (_hdgValid)
        {
            double err = BearingErrorDegTo(_wpTx, _wpTy);
            if (Math.Abs(err) > 22) await TurnBy(err * (0.75 + _rng.NextDouble() * 0.35), ct);
        }
        int run = (int)Math.Clamp(dist / Math.Max(20, _speed) * 1000 * 0.7, 400, 1600);
        await HoldKey(_fwd, Vary(run), ct);
        await MaybeLoc(ct);
    }

    private void NextWaypoint(ZonePlan plan, bool announce)
    {
        int n = plan.Waypoints.Count;
        string order = (_s.WaypointOrder ?? "sequence").Trim().ToLowerInvariant();
        if (order.StartsWith("rand"))
        {
            int nxt;
            do { nxt = _rng.Next(n); } while (n > 1 && nxt == _wpIndex);
            _wpIndex = nxt;
        }
        else if (order.StartsWith("loop"))
        {
            // Closed circuit: …N-1, N, 1, 2… The last leg walks back to the first waypoint, so a
            // route drawn as a ring is patrolled as a ring instead of being retraced backwards.
            _wpIndex = _wpIndex < 0 ? 0 : (_wpIndex + 1) % n;
        }
        else
        {
            if (_wpIndex < 0) { _wpIndex = 0; _wpStep = 1; }
            else
            {
                if (_wpIndex + _wpStep >= n || _wpIndex + _wpStep < 0) _wpStep = -_wpStep;
                _wpIndex += _wpStep;
            }
        }
        double[] wp = plan.Waypoints[Math.Clamp(_wpIndex, 0, n - 1)];
        _wpTx = wp[0] + (_rng.NextDouble() * 2 - 1) * 10;    // human wobble — never the exact point
        _wpTy = wp[1] + (_rng.NextDouble() * 2 - 1) * 10;
        _wpHave = true;
        if (announce)
            Log?.Invoke($"Patrolling {n} waypoints ({(order.StartsWith("rand") ? "random order" : order.StartsWith("loop") ? "looping 1→N→1" : "in sequence, ping-pong")}).");
    }

    /// <summary>Run forward with strafes, an occasional back-step, and right-mouse look-around;
    /// turn back if we reach the explored edge.</summary>
    private async Task Wander(CancellationToken ct)
    {
        if (!_sink.Ready) return;

        // HUNTING ZONE: outside the drawn shape → walk back toward its middle before anything else.
        if (Mode == "zone" && CurrentPlan() is { HasShape: true } zp
            && _x is double zx && _y is double zy && !zp.Contains(zx, zy))
        {
            (double cx2, double cy2) = zp.Center();
            Stats.State = "zone — returning inside";
            Log?.Invoke("Outside the hunting zone — heading back in.");
            RefreshHeadingFromCompass();
            if (_hdgValid) await TurnBy(BearingErrorDegTo(cx2, cy2), ct);
            await HoldKey(_fwd, Vary(1100), ct);
            return;
        }

        double r = Math.Max(10, _s.HuntTetherRadius);

        // Shallow drop just ahead (small Z dip) → back off and pick a new line BEFORE it
        // becomes a swim. Levitation riders mostly float over these entirely.
        if (_dip)
        {
            _dip = false;
            Stats.State = "edge — backing off";
            Log?.Invoke("Ground fell away a little — backing up and turning.");
            await HoldKey(_back, Vary(550), ct);
            await TurnBy((_rng.Next(2) == 0 ? 1 : -1) * 140, ct);
        }

        // Tether breached → closed-loop homing straight back to the anchor.
        if (_s.HuntTetherEnabled && TetherDistance() > r)
        {
            await GoHome(ct, r);
            return;
        }

        // Pre-emptive containment: past ~70% of the leash, curve the wander back toward the
        // anchor BEFORE crossing the line — the circle becomes a wall, not a rubber band.
        if (_s.HuntTetherEnabled && TetherDistance() > r * 0.7)
        {
            RefreshHeadingFromCompass();
            if (_hdgValid)
            {
                double err = HomeErrorDeg();
                if (Math.Abs(err) > 35)
                {
                    Stats.State = "tether — curving back";
                    await TurnBy(err * 0.8, ct);
                }
            }
        }

        var bounds = _heat.BoundsFor(_heat.Current);
        if (bounds is { } b && _x is double x && _y is double y)
        {
            const double pad = 15;
            if (x <= b.minX + pad || x >= b.maxX - pad || y <= b.minY + pad || y >= b.maxY - pad)
            {
                Log?.Invoke("Near explored edge — turning back.");
                await LookAround(ct, big: true);            // pan the camera to reorient
                await HoldKey(_rng.Next(2) == 0 ? _left : _right, Vary(650), ct);
                return;
            }
        }

        // Mouselook jitter slowly drifts the pitch — re-level periodically (above horizon on lev).
        if (_s.LevEnabled && (DateTime.Now - _lastPitchFix).TotalSeconds > 45)
        { await PitchTo(10, ct); _lastPitchFix = DateTime.Now; }

        if (_s.HuntLookAround && _rng.NextDouble() < 0.45)
            await LookAround(ct, big: false);
        if (_rng.NextDouble() < 0.35)
            await HoldKey(_rng.Next(2) == 0 ? _left : _right, Vary(220), ct);   // small strafe
        if (_rng.NextDouble() < 0.12)
            await HoldKey(_back, Vary(240), ct);                                // occasional back-step

        int lo = Math.Max(200, _s.HuntRunMsMin), hi = Math.Max(lo + 1, _s.HuntRunMsMax);
        if (_s.HuntTetherEnabled)
        {
            // Small pens need small strides: cap the forward burst so one run can't blow
            // through the whole circle between two /loc fixes (r=20 → ~350ms strides).
            int cap = (int)Math.Clamp(r * 14, 350, hi);
            lo = Math.Min(lo, Math.Max(200, cap - 250));
            hi = Math.Max(lo + 1, cap);
        }
        await HoldKey(_fwd, _rng.Next(lo, hi), ct);
    }

    /// <summary>Hold right-mouse and nudge the cursor sideways to pan the view — human-like looking
    /// around. Mouselook TURNS the character, so the heading estimate is nudged by the same amount.</summary>
    private async Task LookAround(CancellationToken ct, bool big)
    {
        if (!_s.HuntLookAround || !_sink.Ready) return;
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        _rmbDown = true;
        int sumPx = 0;
        try
        {
            int steps = big ? _rng.Next(6, 12) : _rng.Next(3, 6);
            int dir = _rng.Next(2) == 0 ? -1 : 1;
            for (int i = 0; i < steps && !ct.IsCancellationRequested && _sink.Ready; i++)
            {
                int dx = dir * _rng.Next(16, 40);
                InputProbe.MouseMoveRelative(dx, _rng.Next(-3, 4));
                sumPx += dx;
                await Task.Delay(28, ct);
            }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); _rmbDown = false; }
        if (_hdgValid) _hdg += sumPx / _pxPerDeg * _turnSign * Math.PI / 180.0;
    }

    /// <summary>Hold a keyboard key for ms, releasing immediately if EQ loses focus or we're cancelled.
    /// Mouse binds are ignored for held movement (they're used as taps elsewhere).</summary>
    private async Task HoldKey(InputKey key, int ms, CancellationToken ct)
    {
        if (key.IsNone || key.IsMouse || !_sink.Ready) return;
        InputProbe.KeyDown(key.Vk);
        DateTime began = DateTime.Now;
        try
        {
            DateTime end = began.AddMilliseconds(ms);
            while (DateTime.Now < end && !ct.IsCancellationRequested && _sink.Ready)
                await Task.Delay(50, ct);
        }
        finally
        {
            InputProbe.KeyUp(key.Vk);
            double held = (DateTime.Now - began).TotalMilliseconds;
            if (key.Vk == _fwd.Vk) _fwdMsSinceLoc += held;      // heading-quality bookkeeping
            else _sideMsSinceLoc += held;
        }
    }

    private void ReleaseKeys()
    {
        try
        {
            foreach (InputKey k in new[] { _fwd, _left, _right, _back })
                if (!k.IsNone && !k.IsMouse) InputProbe.KeyUp(k.Vk);
            if (_rmbDown) { InputProbe.MouseButtonEvent(MouseBtn.Right, false); _rmbDown = false; }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Is auto attack running, judged by WATCHING the picked strip for about a second? Null = no
    /// opinion.
    ///
    /// The combat log cannot answer this — the client announces "Auto attack is on" only when the
    /// key is pressed by hand, and a song or a spell engages it in silence. Nor is silence in the
    /// combat log evidence of off: a character facing the wrong way swings at nothing and prints
    /// nothing at all. The border that flashes around the unit frame is the one thing on screen
    /// that tracks the actual state. (The little combat icon does not: green and red there mean IN
    /// COMBAT, which is a different question and cost this feature a release to learn.)
    ///
    /// A FLASH IS A THING THAT HAPPENS OVER TIME, so it cannot be read from one frame — sample it
    /// once mid-blink and a flashing border looks exactly like a still one. So the strip is sampled
    /// repeatedly and the question is whether it MOVED while we watched. That needs no stored
    /// photograph and survives the window being moved, the UI being rescaled, or the colours being
    /// different from whatever was photographed a fortnight ago.
    ///
    /// Returns null rather than false unless a real flash has been measured at least once. "It is
    /// not flashing" and "I am watching a piece of screen where nothing ever happens" produce the
    /// identical reading, and only one of them justifies pressing a toggle.
    /// </summary>
    private async Task<bool?> FlashSaysAttackOn(CancellationToken ct)
    {
        if (!_s.AttackFlashSet || _vitals is null || !_sink.Ready) return null;
        FlashLook look = await WatchBorder(ct);
        // COULDN'T WATCH is not "wasn't flashing". The caller has to be able to tell those apart,
        // because one of them justifies pressing a key and the other justifies nothing at all.
        if (!look.Watched)
        {
            _flashUnwatched = true;
            // SAID WHEN IT STOPS WORKING. An unreadable strip returns null, and null takes the
            // early-out in NudgeAutoAttack before any message is written — so the whole fallback
            // could be dead for a night with nothing in the log. A strip covered by an overlay, or
            // one that has drifted outside the window, reads exactly like this every fight.
            if (++_flashBlind == BlindLooksBeforeSaying)
                Log?.Invoke($"I have failed to read the attack border {BlindLooksBeforeSaying} times in a row, so the "
                          + "auto-attack fallback is doing nothing. " + BorderAdvice());
            return null;
        }
        _flashUnwatched = false;
        _flashBlind = 0;
        // ANY RED MOVEMENT AT ALL MEANS HANDS OFF — not three, one.
        //
        // Three is what the SETUP bar is built on and what lets the watch stop early; it is not a
        // threshold for the verdict. A border clearing setup at six edges over six and a half
        // seconds lands two or three in the run's three-second window depending purely on where
        // the window falls, and treating two as "I can't tell" hands the decision to the blind
        // guess, which presses the key — on a strip that was visibly pulsing red two samples ago.
        // That is this feature's own failure mode arriving through its own safety check, and the
        // blind path doesn't even arm the lie detector, so it would never be caught.
        if (look.Jumps > 0) return true;

        // "NOT FLASHING" IS ONLY AN ANSWER IF THE EDGES WOULD HAVE BEEN SEEN, and only if the
        // border was SILENT rather than merely under-counted.
        //
        // Spotting the flash is easy and being wrong about it is harmless — a false "it's on"
        // merely declines to press, which is what a wildly panning camera produces. Concluding it
        // ISN'T flashing is the one verdict that presses a toggle, so it gets three separate
        // qualifications:
        //
        // ZERO, per the branch above. The setup check proves the border CAN be counted; it cannot
        // prove every later three-second window will land the same number of edges on it, so the
        // only count this verdict can safely rest on is none at all.
        //
        // FULL WINDOW. A watch cut short by the game losing focus can be half a second long, and
        // half a second cannot contain the edges of a once-a-second border. A truncated watch reads
        // silent by construction, so it isn't allowed to be the silent verdict.
        //
        // AND THE BORDER MUST NOT HAVE BEEN CAUGHT LYING this run — see _borderLied.
        if (look.Jumps == 0 && look.Full && _s.AttackFlashProven && !_borderLied) return false;
        return null;
    }

    /// <summary>
    /// How big a redness jump counts as an edge of the flash — ONE constant, and the same one
    /// during setup and during the run.
    ///
    /// It used to be scaled by the amplitude the setup measured, and that was wrong twice over.
    /// The amplitude is a RANGE across the whole window; the thing it was being compared to is a
    /// single step between two consecutive looks. Those are only equal for a border that snaps
    /// instantly — for one that fades over a few frames the step is a third of the range, the bar
    /// was over a third, and every edge fell just under it. That reads as "not flashing", which
    /// presses the key and turns attack OFF mid-fight: the exact failure this feature exists to
    /// prevent, arriving sooner the more vivid the border is.
    ///
    /// And the two ends disagreed. Setup runs with the amplitude still zero, so it certified the
    /// strip at a bar of 20 while every later run demanded 40, 60, 90 — a handshake that proved a
    /// condition nobody would afterwards test. A constant cannot drift apart from itself.
    ///
    /// Static, and the Grind page calls THIS rather than keeping its own copy, so there is one
    /// expression in the app and it cannot drift apart from itself again.
    /// </summary>
    internal static double FlashBar() => MinFlashSpread;

    /// <summary>The jump floor. Twenty units of MEAN redness across the strip needs the border to be
    /// something like a tenth of what was picked, which is what forces a tight strip — and no
    /// landscape can produce it between two looks a tenth of a second apart, because redness is
    /// invariant to the brightness changes that scenery actually makes.</summary>
    internal const double MinFlashSpread = 20.0;

    /// <summary>
    /// How many redness jumps make a flash rather than a coincidence.
    ///
    /// A flash has two edges per cycle, so a border blinking anywhere near once a second gives six
    /// or seven inside the window. The world behind it gives none: however fast the camera pans,
    /// scenery moves SMOOTHLY between looks a tenth of a second apart, so it drifts rather than
    /// jumps. Three sits comfortably above the drift and comfortably below the signal.
    /// </summary>
    internal const int MinFlashJumps = 3;

    /// <summary>
    /// How many edges an attack-ON setup check has to count before the strip is trusted — DOUBLE
    /// what the run needs, and measured over the whole window rather than stopped at the first
    /// three.
    ///
    /// The run and the setup ask different questions and so they need different bars. The run asks
    /// "is it flashing right now", and three edges answers that. The setup asks "will a
    /// three-second window RELIABLY catch this border", and a check that scraped exactly three
    /// answers no: the next window will sometimes catch two. Requiring twice the margin is what
    /// makes the run's silence test mean something, and it is why the setup check does not stop
    /// early — a truncated count cannot demonstrate headroom it was never allowed to measure.
    /// </summary>
    internal const int SetupJumpsWanted = MinFlashJumps * 2;

    private const int FlashSamples = 30;
    /// <summary>
    /// A LONGER WINDOW FOR THE SETUP CHECK, because it is asking a harder question.
    ///
    /// Thirty samples is 3.2 s, and six edges inside that needs the border to blink faster than
    /// about once a second — a hidden requirement no user could discover, and one a slower border
    /// can never satisfy however the strip is drawn. Sixty samples is 6.6 s and asks only that it
    /// blinks faster than once every two seconds or so.
    ///
    /// The run does NOT need the same window, and this is why: a watch longer than the border's
    /// longest steady phase always contains at least one edge, so a 3.2 s run window catches one
    /// from anything blinking faster than about once every six seconds. The run's press needs zero
    /// edges, not three, so what it needs is exactly that guarantee — not six edges.
    /// </summary>
    private const int SetupSamples = 60;
    private const int MinSamples = 6;
    private const int FlashGapMs = 110;
    /// <summary>How long the setup window runs, in seconds, for the messages that quote it.</summary>
    internal const double SetupWindowSeconds = (SetupSamples - 1) * FlashGapMs / 1000.0;

    /// <summary>
    /// REDNESS, not brightness: red minus the average of the other two.
    ///
    /// This is the whole trick and it comes straight from the user's correction — the border is
    /// drawn OVER the 3D world, so the pixels behind it are never still and "did this change"
    /// answers yes for ever. What the flash actually does is add RED. A cloud passing, a torch, the
    /// sun going down move all three channels together and leave this number alone; laying a red
    /// line over the same pixels moves it a long way.
    /// </summary>
    private static double Redness(double r, double g, double b) => r - (g + b) / 2;

    /// <summary>What a look at the border came to. Watched=false means it could not be observed at
    /// all — the game lost focus, the run was stopped — which is a different answer from "it was
    /// still", and must not be mistaken for one. Full=false means the watch was CUT SHORT for the
    /// same reasons after enough samples to return a number: the number is real but the window
    /// isn't long enough for silence to mean anything.</summary>
    internal readonly record struct FlashLook(int Jumps, double Amplitude, bool Watched, bool Full);

    /// <summary>
    /// Watch the strip and count how many times its redness JUMPED. Shared by the run and by the
    /// Grind page's check, so what the page proves is exactly what the run measures.
    ///
    /// Counting edges rather than measuring a range is what survives a background that is always
    /// moving — and it happens to fix the case that beat the previous version, a border that only
    /// winks: a brief flash produces MORE edges per second, not fewer, so the briefer the wink the
    /// easier this gets. What it cannot see is a pulse whose edges are further apart than the
    /// window is long, and the setup check measures that rather than letting the run draw
    /// conclusions from a border it has never managed to catch.
    /// </summary>
    /// <param name="stopEarly">Stop as soon as the answer is yes. The RUN wants this — three edges
    /// is a flash and there is nothing to learn from watching it blink twenty more times while the
    /// fight is paused. The SETUP check must not have it: its job is to measure how much headroom
    /// the border has over a full window, and a count stopped at three can only ever report three.
    /// </param>
    internal static async Task<FlashLook> SampleFlash(Ocr.VitalsReader vitals, AppSettings s, double jumpBar,
                                                      Func<bool> keepGoing, Func<int, Task> wait,
                                                      bool stopEarly = true)
    {
        var reds = new List<double>();
        int jumps = 0;
        bool full = true;
        int samples = stopEarly ? FlashSamples : SetupSamples;
        // A GRAB THAT FAILED IS NOT A QUIET SAMPLE. MeanOf returns null when the game is minimized,
        // when something is sitting over the strip, or when the blit throws — all of which are
        // perfectly steady and none of which is the border being still. But voiding the whole window
        // on the FIRST one was too brittle to ship: a single toast, tooltip or task-switch during a
        // six-second check killed it outright and sent the user off to re-pick a strip that was
        // fine. A skipped sample only shortens the window slightly and can never invent an edge, so
        // a tenth of them is affordable; past that the window is no longer long enough for silence
        // to mean anything and it is marked short.
        int misses = 0, missBudget = Math.Max(2, samples / 10);
        for (int i = 0; i < samples; i++)
        {
            // CUT SHORT, NOT FINISHED. Whatever has been counted so far is real, but the window is
            // now shorter than the border's own period might be, so silence from it means nothing.
            if (!keepGoing()) { full = false; break; }
            if (i > 0) await wait(FlashGapMs);
            if (vitals.MeanOf(s.AttackFlashX, s.AttackFlashY, s.AttackFlashW, s.AttackFlashH)
                is not (double r, double g, double b))
            { if (++misses > missBudget) full = false; continue; }
            double red = Redness(r, g, b);
            if (reds.Count > 0 && Math.Abs(red - reds[^1]) >= jumpBar) jumps++;
            reds.Add(red);
            // MinSamples as well as the jump count, so a detection is never thrown away by the
            // floor below as unreadable. Stopping here is a POSITIVE answer, so the window is not
            // marked short: nothing downstream reads silence out of a look that found three edges.
            if (stopEarly && reds.Count >= MinSamples && jumps >= MinFlashJumps) break;
        }
        if (reds.Count < MinSamples) return new FlashLook(0, 0, false, false);

        // The amplitude, as a trimmed range, so one torn capture cannot set it. Shown to the user
        // as "how far it moved" and nothing else — the verdict is the edge count, and deriving the
        // jump bar from this range is what broke the previous version.
        var v = new List<double>(reds);
        v.Sort();
        int lo = v.Count >= 5 ? 1 : 0;
        return new FlashLook(jumps, v[v.Count - 1 - lo] - v[lo], true, full);
    }

    /// <summary>One look at the border, with the run's own guards on it.</summary>
    private Task<FlashLook> WatchBorder(CancellationToken ct)
        => SampleFlash(_vitals!, _s, FlashBar(), () => !ct.IsCancellationRequested && _sink.Ready,
                       ms => Task.Delay(ms, ct));

    /// <summary>How long a fight has to go without a single melee line before the absence counts as
    /// evidence rather than as "it hasn't started yet". A melee round is a couple of seconds, so
    /// this is comfortably past one and still inside the first exchange.</summary>
    private const double AutoAttackGrace = 2.6;

    /// <summary>The client announcing its own auto-attack state. Bounded to the same sentence so a
    /// loose " on" can't match "one", "only" or "once" three clauses later — "You can only auto
    /// attack one target at a time" is not a statement that attack is running.</summary>
    private static readonly System.Text.RegularExpressions.Regex AutoAtkOn = new(
        @"(\bauto[- ]?attack\b[^.]{0,20}\bon\b|\byou begin attacking\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AutoAtkOff = new(
        @"(\bauto[- ]?attack\b[^.]{0,20}\boff\b|\byou are no longer attacking\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// How long to wait for a con line, learned from how long they actually take.
    ///
    /// Starts generous, because being slow once costs a moment and being short costs a whole seek
    /// cycle. Settles on roughly three times the observed round trip so an unlucky log poll still
    /// lands inside it, and is floored and capped so neither a freak fast answer nor a freak slow
    /// one can move it somewhere silly.
    /// </summary>
    private double _conWaitMs = 1100;
    private bool _conLatencySaid;
    private readonly Queue<double> _conSamples = new();

    private void LearnConLatency(double ms)
    {
        // A sample of nearly nothing is a con line that was already in flight when we asked — it
        // measures our own reset, not the game — and believing it would drag the window to the
        // floor and announce "your con answers land about 0 ms after I ask".
        if (ms < 20) return;
        _conSamples.Enqueue(ms);
        if (_conSamples.Count > 5) _conSamples.Dequeue();

        // THE WORST OF THE LAST FEW, not the latest one. Sized from a single sample, any answer
        // over about 780 ms pinned the window at its 2500 cap — and rise-fast/fall-slow then needed
        // seventeen good cons to walk it back down, so one hiccup reinstated most of the delay this
        // was written to remove. A running maximum is the same caution without the ratchet.
        double worst = 0;
        foreach (double v in _conSamples) if (v > worst) worst = v;
        double want = Math.Clamp(worst * 2 + 150, 450, 2500);
        _conWaitMs = want > _conWaitMs ? want : _conWaitMs * 0.8 + want * 0.2;
        if (!_conLatencySaid)
        {
            _conLatencySaid = true;
            Log?.Invoke($"Your con answers land about {ms:0} ms after I ask, so I'll wait up to "
                      + $"{_conWaitMs:0} ms for one before giving up and roaming.");
        }
    }

    private int Vary(int ms) => _s.Vary(ms, _rng);

    /// <summary>
    /// Tap the auto-attack key, once, because nothing has swung.
    ///
    /// EVERY guard here is about the same hazard: this key is a TOGGLE in every EverQuest client,
    /// so pressing it while attack is already running turns it OFF and the fight quietly stops.
    /// That is why it is blank by default, why it fires at most once per fight, why it needs the
    /// log to have gone silent rather than merely "not started yet", and why it never fires in
    /// cast-only or bard mode — where no melee swing is expected and its absence proves nothing.
    /// </summary>
    private void NudgeAutoAttack(bool borderSaysOff)
    {
        // CONSUMED FIRST, ahead of every early return, including _autoAttackTried. Clearing it
        // after one of them left the flag set on a path that had already decided nothing — a fight
        // that never looked would then inherit an abandoned look from some earlier fight.
        bool unwatched = _flashUnwatched;
        _flashUnwatched = false;
        // The decision has been taken either way — a fight that couldn't send is not a fight that
        // should try again three seconds later.
        _autoAttackTried = true;
        if (_autoAtk.IsNone || !_sink.Ready) return;
        // A LOOK THAT NEVER HAPPENED BUYS NOTHING. Losing focus mid-sample used to spend one of the
        // three per-run guesses and then press the key anyway — on no evidence, because the
        // measurement was abandoned before it produced any. The decision still stands for this
        // fight (_autoAttackTried is set above), it just isn't acted on.
        if (unwatched) return;
        if (!borderSaysOff && ++_blindNudges > MaxBlindNudges)
        {
            // SAID WHEN IT STOPS, like every other cap in this app. Going quiet is
            // indistinguishable from the feature never having worked.
            if (_blindCapSaid) return;
            _blindCapSaid = true;
            Log?.Invoke($"That's {MaxBlindNudges} guesses at auto attack this run, so I'll stop pressing "
                      + $"{_autoAtk.Display}. It is a toggle and I can't see whether attack is on — " + BorderAdvice());
            return;
        }
        // AND THE MEASURED PATH IS CAPPED TOO, just far higher. See MaxBorderNudges.
        if (borderSaysOff && Interlocked.Increment(ref _borderNudges) > MaxBorderNudges)
        {
            if (_borderCapSaid) return;
            _borderCapSaid = true;
            Log?.Invoke($"I have now pressed {_autoAtk.Display} {MaxBorderNudges} times this run because the attack "
                      + "border read silent. That is more than bad luck, so I'll stop: the strip has probably "
                      + "moved. Re-pick it on the Grind page and run both checks.");
            return;
        }
        // SENT, OR NOTHING HAPPENED. Send re-checks focus itself and no-ops if it went away in
        // between, and both the log line and the lie detector below would then be describing a
        // press that never left the building — arming the detector on it would let the user's OWN
        // next attack keypress read as "the border lied" and disable the feature for the run.
        if (!_sink.Send(_autoAtk)) return;
        if (borderSaysOff) Interlocked.Exchange(ref _borderPressedTicks, Environment.TickCount64);
        Log?.Invoke(borderSaysOff
            ? $"The attack border isn't flashing, so auto attack is OFF — tapping {_autoAtk.Display} to turn it on."
            : $"Nothing has swung since the rotation started, so auto attack MIGHT not be on — tapping "
              + $"{_autoAtk.Display} once. This is a guess: the combat log never says when a song or a spell "
              + "engaged attack, and a character facing the wrong way swings at nothing and prints nothing. "
              + BorderAdvice());
    }

    /// <summary>Say the "indicator says it's already on" line once a run, not once a fight.</summary>
    private bool _narrateBorder = true;

    /// <summary>Fights where the border said attack was on and nothing had swung. Counted, never
    /// acted on — see the comment at the point of use.</summary>
    private int _borderOnNoSwing;
    private const int BorderOnNoSwingSuspect = 5;

    /// <summary>
    /// How many times a whole RUN may press the toggle on a guess rather than on the indicator.
    ///
    /// Proof gets a press per fight; a guess does not. Without the border picked — the default —
    /// "nothing has swung" fires on every fight that meets the conditions, so a night's grinding is
    /// hundreds of presses at a key that turns attack OFF as readily as on. A handful is enough to
    /// correct a systemic "the rotation never engages attack"; anything beyond that is a recurring
    /// facing quirk being answered with the wrong tool, over and over.
    /// </summary>
    private const int MaxBlindNudges = 3;
    private int _blindNudges;

    /// <summary>
    /// How many times a whole RUN may press the toggle on the BORDER's say-so.
    ///
    /// Much more generous than the blind cap, because this one is acting on a measurement rather
    /// than on an absence — but not unlimited. A strip that has gone stale (the window moved, the
    /// UI was rescaled, the unit frame is somewhere else now) still reads "proven" on disk and
    /// still reads silent every time, so without a cap it presses once a fight for ever, flipping
    /// attack on and off and announcing each flip as fact.
    ///
    /// COUNTED CONSECUTIVELY, cleared by any press the client confirms engaged attack. A press that
    /// worked is the feature doing its job, and on a rotation that never engages attack that is
    /// every fight of the night — counting those would switch the fallback off after the first
    /// hour and blame a strip that was reading perfectly.
    /// </summary>
    private const int MaxBorderNudges = 10;
    private int _borderNudges;
    private bool _borderCapSaid;
    /// <summary>Environment.TickCount64 when the last press made on the BORDER's say-so went out.
    /// Zero until one has. Interlocked because it is written on the role thread and read on the log
    /// thread, and a 64-bit field is not atomic on every runtime this ships to.</summary>
    private long _borderPressedTicks;
    /// <summary>The border said silent, we pressed, and the client then said attack went off — so
    /// it was on. Set once on the log thread, read on the role thread, never cleared: a run that
    /// has caught it lying does not get to change its mind halfway through.</summary>
    private volatile bool _borderLied;
    /// <summary>The last look at the border was abandoned rather than answered.</summary>
    private bool _flashUnwatched;
    /// <summary>Consecutive looks that could not be read at all.</summary>
    private int _flashBlind;
    private const int BlindLooksBeforeSaying = 3;
    private bool _blindCapSaid;

    /// <summary>The ONE thing worth doing about it, and which one depends on how far through the
    /// setup they are. Telling somebody who has already picked the indicator to go and pick it is
    /// how good advice gets ignored.</summary>
    private string BorderAdvice()
        => !_s.AttackFlashSet
            ? "Pick the flashing attack border on the Grind page — a thin strip of the edge that flashes red "
              + "while you're attacking — and I'll watch it instead of inferring from silence."
            : !_s.AttackFlashProven
              ? "You have picked the attack border but it hasn't passed both checks, so I can't tell an idle border "
                + "from a strip of screen where nothing ever happens. On the Grind page: turn auto attack ON and "
                + "press \u201Ccheck: attack ON\u201D, then turn it OFF and press \u201Ccheck: attack OFF\u201D. "
                + "The readout beside them says what is still missing."
              : _borderLied
                ? "I caught that strip reading silent while auto attack was actually running earlier this run, so I "
                  + "have stopped trusting it for the rest of it. Re-pick it on the Grind page — it may have moved — "
                  + "and run both checks."
                : "The border couldn't be read clearly just then: either it pulsed too few times to be sure, or "
                  + "something was over that strip for part of the look \u2014 the EQ Avatar window itself is the "
                  + "usual culprit on one monitor, and so is the map overlay.";

    private bool _autoAttackTried;
}
