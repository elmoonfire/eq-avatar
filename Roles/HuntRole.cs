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
        if (_watcher != null) { _watcher.LineRead += OnLine; _watcher.Start(fromStart: false); }
        Log?.Invoke($"HUNT started — target={_target.Display}, con={_con.Display}, move={_fwd.Display}/{_left.Display}/{_right.Display}/{_back.Display}"
                    + (_loc.IsNone ? "" : $", /loc key={_loc.Display}") + ". Keep EQ focused; F12 stops. Watch it.");
        if (_s.GrindCastOnly)
            Log?.Invoke("Cast/sing only — no facing turns and no closing in during a fight; unreachable targets get dropped instead.");
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null) return;
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
        // Facing/range feedback + bard interrupts arrive as plain lines the parser doesn't type.
        if (raw.Contains("You cannot see your target", StringComparison.OrdinalIgnoreCase)) _cantSee = true;
        else if (raw.Contains("too far away", StringComparison.OrdinalIgnoreCase)
                 || raw.Contains("out of range", StringComparison.OrdinalIgnoreCase)) _tooFar = true;

        // Spell/song damage is attributed to the VICTIM ("a rat was hit by non-melee for 42 points
        // of damage."), so the melee "did one of OUR lines print?" test never fires for a caster or
        // a bard. Count these as our output too — a resist still proves the cast reached the mob,
        // which is exactly what the facing/reach logic wants to know.
        if (raw.Contains("hit by non-melee", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("resisted your", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Your target resisted", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _ourSwings);

        // DID THE WEAPON MOVE? Counted from the RAW line, before anything classifies it, because
        // the Combat classifier needs "points of damage" or the word "hit" or "slash" — so a MISS
        // only registers for a slashing weapon. A monk punching, a paladin with a mace, a rogue
        // piercing: auto attack running perfectly, every swing in the first exchange missing, not
        // one line reaching the counter — and then this class taps a TOGGLE and switches off the
        // attack it was checking on. "You try to …" is the one phrase every miss, dodge, parry,
        // riposte and block shares, for every weapon skill there is.
        // ONE GATE OVER BOTH, because both are facts read out of a public document. Other people's
        // chat starts with their name so the "You " anchor already blocks it — but the local
        // player's own speech starts with "You " too, and "You say, 'you try to crush it'" would
        // otherwise be counted as a swing. Same guard the position parser uses, same reason.
        if (!LogEventParser.SpokenByAPlayer(raw))
        {
            // DID THE WEAPON MOVE? Counted from the RAW line, before anything classifies it,
            // because the Combat classifier needs "points of damage" or the word "hit" or "slash"
            // — so a MISS only registers for a slashing weapon. A monk punching, a paladin with a
            // mace, a rogue piercing: auto attack running perfectly, every swing in the first
            // exchange missing, not one line reaching the counter — and then this class taps a
            // TOGGLE and switches off the attack it was checking on. "You try to …" is the one
            // phrase every miss, dodge, parry, riposte and block shares, for every weapon skill.
            //
            // Damage TAKEN is excluded: fall damage and poison ticks read as "You have taken 12
            // points of damage", and letting those count would suppress the nudge in precisely the
            // situation it exists for — taking damage while dealing none.
            if (raw.StartsWith("You ", StringComparison.Ordinal)
                && !raw.StartsWith("You have taken", StringComparison.OrdinalIgnoreCase)
                && (raw.Contains("points of damage", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("You try to ", StringComparison.OrdinalIgnoreCase)))
            { Interlocked.Increment(ref _meleeSwings); _autoAttackOn = true; }

            // And what the CLIENT says about auto attack, when it says anything. A guildmate typing
            // "turn auto-attack off before you pull" must not clear the flag that suppresses the
            // toggle press — the same class of bug as a stranger's chat moving the character.
            if (AutoAtkOn.IsMatch(raw)) _autoAttackOn = true;
            else if (AutoAtkOff.IsMatch(raw)) _autoAttackOn = false;
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
                            if (_wildLocs < 4) break;
                            if (fresh)
                                Log?.Invoke($"That's {_wildLocs} positions in a row I couldn't accept, so I'll "
                                          + "believe this one and carry on from here.");
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
                    { _startX = nx; _startY = ny; if (_s.HuntTetherEnabled) Log?.Invoke($"Tether anchored at /loc {ny:0}, {nx:0} — radius {_s.HuntTetherRadius}."); }

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
                            else { _zGood = nz; _goodX = nx; _goodY = ny; }   // normal ground tracks us
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
                else if (ev.Text.StartsWith("You ", StringComparison.Ordinal)
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
            if (it == 0 && Math.Abs(NormDeg(target - now)) > Math.Abs(err) + 10)
            { dir = -dir; _turnSign = dir; Log?.Invoke("Compass shows the drag turned the wrong way — flipped direction."); }
            cur = now;
        }
        if (_compass.ReadLocDeg() is double fin) { _hdg = fin * Math.PI / 180.0; _hdgValid = true; }
        return true;
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
        await PitchTo(55, ct);
        while (!ct.IsCancellationRequested && _sink.Ready && (DateTime.Now - began).TotalSeconds < 45)
        {
            RefreshHeadingFromCompass();
            if (_goodX is double gx && _goodY is double gy && _hdgValid)
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
        Log?.Invoke("Couldn't climb out the way we came — accepting this level as the new ground (watch me).");
        if (_z is double nz) { _zGood = nz; _goodX = _x; _goodY = _y; }
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

    /// <summary>Signed degrees to turn so the current heading points at the tether anchor.</summary>
    private double HomeErrorDeg()
        => _startX is double sx && _startY is double sy ? BearingErrorDegTo(sx, sy) : 0;

    /// <summary>Walk STRAIGHT back inside the tether: learn heading from /loc pairs if needed,
    /// turn toward the anchor, run a distance-sized burst, re-measure, correct. Closed loop —
    /// no more drifting further away on a blind turn.</summary>
    private async Task GoHome(CancellationToken ct, double r)
    {
        Stats.State = "tether — homing";
        Log?.Invoke($"Past the tether ({TetherDistance():0} > {r:0}) — walking straight back.");
        bool blind = _loc.IsNone && (DateTime.Now.Ticks - Interlocked.Read(ref _locTicks)) > 12L * TimeSpan.TicksPerSecond;
        if (blind)
        {
            // No /loc source → we can't steer. Old behavior as a last resort, once.
            Log?.Invoke("Tether homing needs a /loc key (Grind settings) or a repeating /loc macro — doing a blind turn instead.");
            await TurnBy(150 * (_rng.Next(2) == 0 ? 1 : -1), ct);
            await HoldKey(_fwd, Vary(1100), ct);
            return;
        }
        for (int leg = 0; leg < 8 && !ct.IsCancellationRequested && _sink.Ready; leg++)
        {
            double before = TetherDistance();
            if (before <= r * 0.55) { Log?.Invoke($"Back inside the tether ({before:0} ≤ {r:0})."); Stats.State = "seeking"; return; }
            RefreshHeadingFromCompass();                     // the compass makes this instant + exact
            if (!_hdgValid)
            {
                // Learn heading: a short forward stride bracketed by two fresh locs.
                await FreshLoc(ct);
                await HoldKey(_fwd, 700, ct);
                if (!await FreshLoc(ct)) { await Task.Delay(300, ct); }
                if (!_hdgValid) continue;                    // try another stride
            }
            await TurnBy(HomeErrorDeg(), ct);
            int run = (int)Math.Clamp(before / Math.Max(20, _speed) * 1000 * 0.8, 450, 2400);
            await HoldKey(_fwd, run, ct);
            await FreshLoc(ct);
            double after = TetherDistance();
            if (after > before + 5 && _hdgValid)
            {
                Log?.Invoke($"Homing leg went the wrong way ({before:0} → {after:0}) — reversing.");
                _hdg += Math.PI;                             // stale heading; flip and re-run the loop
            }
        }
        Log?.Invoke("Homing paused this pass (wall or bad reads) — will keep trying.");
    }

    /// <summary>Does the last con line name a mob on the directive target list?</summary>
    private bool ConMatchesTargets()
    {
        if (_targets.Count == 0) return false;
        string con = _lastConText.ToLowerInvariant();
        foreach (string t in _targets) if (con.Contains(t)) return true;
        return false;
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
                if (_selfDead) { Log?.Invoke("Death detected — stopping hunt for safety."); Stats.Deaths++; break; }
                if (!_sink.Ready) { Stats.State = "paused (EQ not focused)"; await Task.Delay(400, ct); continue; }

                await MaybeLev(ct);                          // keep Levitate up + view above horizon
                if (_fell) { await Recover(ct); continue; }  // fell into a pit / water → climb out first

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
                int meleeAtStart = _meleeSwings;
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
                    if (_ourSwings != swingsSeen) { swingsSeen = _ourSwings; sweep = 0; unreachable = 0; lastOut = DateTime.Now; }

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
                        && _meleeSwings == meleeAtStart && _autoAttackOn != true
                        && (DateTime.Now - meleeGraceFrom).TotalSeconds > AutoAttackGrace)
                    {
                        // THE INDICATOR DECIDES WHEN IT CAN. Everything above is inference from an
                        // absence; this is a look at the thing itself. A clear "it's on" cancels the
                        // press outright — and cancels it for the rest of the fight, because the
                        // reason won't have changed.
                        // Costs about a second, once per fight, and only in a fight that has
                        // already gone wrong — nothing has swung for two and a half seconds while
                        // in range. Cheap at that price for the only signal that actually knows.
                        bool? lamp = await FlashSaysAttackOn(ct);
                        if (lamp == true)
                        {
                            _autoAttackTried = true;
                            if (_narrateLamp)
                            {
                                _narrateLamp = false;
                                Log?.Invoke("Nothing has swung, but the attack border IS flashing — so auto attack is "
                                          + "already on and this is facing or reach. Leaving your key alone.");
                            }
                        }
                        else NudgeAutoAttack(lamp is false);
                    }

                    if (castOnly)
                    {
                        // CAST / SING ONLY: spells and songs don't care which way we're pointing, so
                        // every melee correction is dead time — no facing sweeps, no closing bursts.
                        // The rotation just keeps firing. When EQ says the target is out of reach or
                        // out of sight we don't fix it, we drop it: the seek phase will walk us to a
                        // mob we can actually hit, which is faster than pivoting at this one.
                        bool blocked = _cantSee || _tooFar;
                        _cantSee = false; _tooFar = false;
                        if (blocked) unreachable++;
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
        catch (Exception ex) { Log?.Invoke("Hunt error: " + ex.Message); }
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
        FlashLook look = await FlashSpread(ct);
        // COULDN'T WATCH is not "wasn't flashing". The caller has to be able to tell those apart,
        // because one of them justifies pressing a key and the other justifies nothing at all.
        if (!look.Watched) { _flashUnwatched = true; return null; }
        _lastDuty = look.Duty;
        _flashUnwatched = false;
        if (look.Spread >= FlashBar()) return true;
        // "NOT FLASHING" IS ONLY AN ANSWER IF A MISS WOULD HAVE BEEN UNLIKELY.
        //
        // Seeing the flash is easy and the failure is harmless — a false "it's on" merely declines
        // to press. Concluding it ISN'T flashing is the one verdict that presses a toggle, and its
        // reliability depends entirely on how long the border stays lit. Simulated against a
        // symmetric blink the miss rate is zero; against a border that lights for a quarter of its
        // cycle it is about one in four, and a twentieth, one in two. Those misses all read as
        // "attack is off" and switch off the attack this exists to protect.
        //
        // So the run only takes that verdict from a border whose measured duty can support it.
        // Below that it says "I can't tell" and falls back to the budgeted guess — which is the
        // truth, and which is bounded at three presses a run rather than one per fight.
        return _s.AttackFlashProven && _s.AttackFlashDuty >= MinTrustedDuty ? false : null;
    }

    /// <summary>How far the strip's average colour has to travel to count as a flash. Derived from
    /// the flash actually measured during setup so a subtle border and a vivid one both work, with
    /// a floor that screen noise and a scrolling chat reflection cannot reach.</summary>
    private double FlashBar() => Math.Max(MinFlashSpread, _s.AttackFlashSeen * 0.35);

    /// <summary>Noise floor. Two blits of a still region differ by a fraction of one colour step;
    /// this is far above that and far below any real flash.</summary>
    internal const double MinFlashSpread = 7.0;

    /// <summary>How much of its cycle the border has to spend LIT before "it isn't flashing" is
    /// worth acting on. A steady blink is around a half; anything under a fifth is a wink, and a
    /// watch that misses a wink is indistinguishable from one that watched a dark border.</summary>
    internal const double MinTrustedDuty = 0.2;

    /// <summary>
    /// How many samples the NEGATIVE case is worth. A flash is only seen if the window contains a
    /// transition, so the window has to outlast the longest phase — and nobody has measured this
    /// border's period. At 110 ms apart, thirty samples span 3.3 s, which simulation puts at zero
    /// misses for any period up to six seconds; the nine-sample, 0.9 s window it replaces missed a
    /// two-second flash two thirds of the time. Every one of those misses reads as "not flashing",
    /// which presses the toggle and turns off the attack this whole mechanism exists to protect.
    ///
    /// The POSITIVE case never pays for it: the loop stops the moment it has seen enough, which for
    /// a flashing border is five or six samples — half a second or so. Only the answer that is about
    /// to press a key spends the full three and a bit seconds, which is the right way round.
    /// </summary>
    private const int FlashSamples = 30;
    /// <summary>Fewest usable reads before an answer means anything — and the same floor the early
    /// bail respects, or a detection made in five samples would be discarded as unreadable.</summary>
    private const int MinSamples = 6;
    private const int FlashGapMs = 110;

    /// <summary>What a look at the border came to. Watched=false means it could not be observed at
    /// all — the game lost focus, the run was stopped — which is a different answer from "it was
    /// still", and must not be mistaken for one.</summary>
    internal readonly record struct FlashLook(double Spread, bool Watched, double Duty);

    /// <summary>
    /// Watch the strip and report how far its average colour travelled. Shared by the run and by
    /// the Grind page's check, so what the page proves is exactly what the run measures.
    ///
    /// The spread is a TRIMMED range — second-lowest to second-highest per channel — because a
    /// plain min/max is set by its single most extreme sample, and one torn capture or one frame of
    /// a tooltip drifting past would report a flash that never happened. Trimming discards one
    /// outlier at each end while leaving a genuine two-level signal untouched, since a flashing
    /// border spends many samples at each level.
    /// </summary>
    internal static async Task<FlashLook> SampleFlash(Ocr.VitalsReader vitals, AppSettings s, double bar,
                                                      Func<bool> keepGoing, Func<int, Task> wait)
    {
        var rs = new List<double>(); var gs = new List<double>(); var bs = new List<double>();
        for (int i = 0; i < FlashSamples; i++)
        {
            if (!keepGoing()) break;
            if (i > 0) await wait(FlashGapMs);
            if (vitals.MeanOf(s.AttackFlashX, s.AttackFlashY, s.AttackFlashW, s.AttackFlashH)
                is not (double r, double g, double b)) continue;
            rs.Add(r); gs.Add(g); bs.Add(b);
            int t = TrimFor(s.AttackFlashDuty, rs.Count);
            // ENOUGH IS ENOUGH — but measured the SAME WAY the final answer is, or the shortcut
            // undoes itself. Bailing on the untrimmed spread stopped at the first sample of a new
            // phase, and the trim then threw that lone sample away and reported a spread of zero:
            // simulated against a one-second flash it missed a third of them, and against a
            // three-second one three quarters. Requiring the TRIMMED spread to clear the bar means
            // two samples have landed in the new phase, which is exactly what survives trimming.
            // MinSamples as well as the trim's own minimum: the floor below rejects anything
            // shorter as unreadable, so bailing at five would have thrown away the very detection
            // it had just made and reported "couldn't watch".
            if (rs.Count >= Math.Max(MinSamples, 2 * t + 3) && Spread(rs, gs, bs, t) >= bar) break;
        }
        // SIX, WHATEVER STOPPED IT. The floor used to apply only when the loop was cut short, so a
        // capture failing on twenty-six of thirty attempts still returned an answer — computed
        // untrimmed, from three samples, where one low reading is the whole result. A high one
        // reads "flashing" and merely declines to press; a low one presses the key and announces
        // that the border wasn't flashing.
        if (rs.Count < MinSamples) return new FlashLook(-1, false, 0);
        int trim = TrimFor(s.AttackFlashDuty, rs.Count);
        return new FlashLook(Spread(rs, gs, bs, trim), true, LitFraction(rs, gs, bs));
    }

    /// <summary>
    /// Whether one outlier can be afforded, given how briefly this border is known to light.
    ///
    /// Trimming discards the extreme sample at each end, which is what stops a single torn capture
    /// reading as a flash — but it also discards the ONLY lit sample when the border merely winks,
    /// turning a real flash into "still" and pressing the toggle. So it is spent only when the
    /// measured duty says three or more samples should be lit. An unmeasured duty (0) keeps the
    /// trim, because that is the case the proving click is about to measure.
    /// </summary>
    private static int TrimFor(double duty, int n) => duty > 0 && duty * n < 3 ? 0 : 1;

    /// <summary>How much of the watch this region spent at the bright end of its own range — the
    /// duty cycle, measured rather than assumed.</summary>
    private static double LitFraction(List<double> r, List<double> g, List<double> b)
    {
        int n = r.Count, lit = 0;
        double lo = double.MaxValue, hi = double.MinValue;
        var sum = new double[n];
        for (int i = 0; i < n; i++)
        { sum[i] = r[i] + g[i] + b[i]; lo = Math.Min(lo, sum[i]); hi = Math.Max(hi, sum[i]); }
        if (hi - lo < 1e-6) return 0;
        double mid = (lo + hi) / 2;
        for (int i = 0; i < n; i++) if (sum[i] > mid) lit++;
        return (double)lit / n;
    }

    private static double Spread(List<double> r, List<double> g, List<double> b, int trim)
    {
        // A trim that would leave nothing to measure is no trim. At three samples, trim 1 compares
        // the middle value with itself and returns zero for ever — i.e. "not flashing", i.e. press
        // — so the guard is here rather than in the agreement of two call sites.
        if (r.Count < 2 * trim + 2) trim = 0;
        double d = 0;
        foreach (List<double> ch in new[] { r, g, b })
        {
            var v = new List<double>(ch);
            v.Sort();
            int lo = Math.Min(trim, (v.Count - 1) / 2), hi = v.Count - 1 - lo;
            double delta = v[hi] - v[lo];
            d += delta * delta;
        }
        return Math.Sqrt(d);
    }

    private Task<FlashLook> FlashSpread(CancellationToken ct)
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
    private void NudgeAutoAttack(bool lampSaysOff)
    {
        // The decision has been taken either way — a fight that couldn't send is not a fight that
        // should try again three seconds later.
        _autoAttackTried = true;
        if (_autoAtk.IsNone || !_sink.Ready) return;
        // CONSUMED FIRST, ahead of every early return. Clearing it after one of them left the flag
        // set on a path that had already decided nothing — true today only because of which
        // conditions happen to be constant mid-run, which is not a reason.
        bool unwatched = _flashUnwatched;
        _flashUnwatched = false;
        // A LOOK THAT NEVER HAPPENED BUYS NOTHING. Losing focus mid-sample used to spend one of the
        // three per-run guesses and then press the key anyway — on no evidence, because the
        // measurement was abandoned before it produced any. The decision still stands for this
        // fight (_autoAttackTried is set above), it just isn't acted on.
        if (unwatched) return;
        if (!lampSaysOff && ++_blindNudges > MaxBlindNudges)
        {
            // SAID WHEN IT STOPS, like every other cap in this app. Going quiet is
            // indistinguishable from the feature never having worked.
            if (_blindCapSaid) return;
            _blindCapSaid = true;
            Log?.Invoke($"That's {MaxBlindNudges} guesses at auto attack this run, so I'll stop pressing "
                      + $"{_autoAtk.Display}. It is a toggle and I can't see whether attack is on — " + LampAdvice());
            return;
        }
        _sink.Send(_autoAtk);
        Log?.Invoke(lampSaysOff
            ? $"The attack border isn't flashing, so auto attack is OFF — tapping {_autoAtk.Display} to turn it on."
            : $"Nothing has swung since the rotation started, so auto attack MIGHT not be on — tapping "
              + $"{_autoAtk.Display} once. This is a guess: the combat log never says when a song or a spell "
              + "engaged attack, and a character facing the wrong way swings at nothing and prints nothing. "
              + LampAdvice());
    }

    /// <summary>Say the "indicator says it's already on" line once a run, not once a fight.</summary>
    private bool _narrateLamp = true;

    /// <summary>
    /// How many times a whole RUN may press the toggle on a guess rather than on the indicator.
    ///
    /// Proof gets a press per fight; a guess does not. Without the lamp picked — the default —
    /// "nothing has swung" fires on every fight that meets the conditions, so a night's grinding is
    /// hundreds of presses at a key that turns attack OFF as readily as on. A handful is enough to
    /// correct a systemic "the rotation never engages attack"; anything beyond that is a recurring
    /// facing quirk being answered with the wrong tool, over and over.
    /// </summary>
    private const int MaxBlindNudges = 3;
    private int _blindNudges;
    /// <summary>The last look at the border was abandoned rather than answered.</summary>
    private bool _flashUnwatched;
    private double _lastDuty;
    private bool _blindCapSaid;

    /// <summary>The ONE thing worth doing about it, and which one depends on how far through the
    /// setup they are. Telling somebody who has already picked the indicator to go and pick it is
    /// how good advice gets ignored.</summary>
    private string LampAdvice()
        => !_s.AttackFlashSet
            ? "Pick the flashing attack border on the Grind page — a thin strip of the edge that flashes red "
              + "while you're attacking — and I'll watch it instead of inferring from silence."
            : !_s.AttackFlashProven
              ? "You have picked the attack border but I have never seen it flash, so I can't tell an idle border "
                + "from a strip of screen where nothing ever happens. Turn auto attack ON in game and click the "
                + "readout beside the pick button once — that's all it needs."
              : "The border couldn't be watched just then — the game may not have been in front.";

    private bool _autoAttackTried;
}
