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
    private ConsiderDifficulty _lastCon = ConsiderDifficulty.Unknown;
    private ConsiderAttitude _lastAttitude = ConsiderAttitude.Unknown;
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
    private long _prevSegTicks;

    // facing + bard state fed from the log
    private volatile bool _cantSee, _tooFar;
    private int _ourSwings;                                  // count of OUR outgoing combat lines
    private volatile bool _singing;                          // bard melody believed active
    private DateTime _melodyAt = DateTime.MinValue;

    // --- compass, levitation + fall recovery (0.9.16) --------------------------------------
    private readonly CompassReader? _compass;                // real heading reads off the game UI
    private DateTime _levAt = DateTime.MinValue;
    private volatile bool _levNeeded;
    private DateTime _lastPitchFix = DateTime.MinValue;
    private double? _z, _zGood, _goodX, _goodY;              // last altitude that counted as "ground"
    private volatile bool _fell, _dip;

    // resolved binds
    private readonly InputKey _fwd, _left, _right, _back, _target, _con, _loc;

    public bool Running => _cts is { IsCancellationRequested: false };

    public HuntRole(IInputSink sink, List<(InputKey, int)> rotation, string? logPath, AppSettings s, HeatmapModel heat, CompassReader? compass = null)
    {
        _sink = sink; _rotation = rotation; _s = s; _heat = heat; _compass = compass;
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath);
        _fwd = InputKey.Parse(s.HuntForwardKey);
        _left = InputKey.Parse(s.HuntLeftKey);
        _right = InputKey.Parse(s.HuntRightKey);
        _back = InputKey.Parse(s.HuntBackKey);
        _target = InputKey.Parse(s.HuntTargetKey);
        _con = InputKey.Parse(s.HuntConsiderKey);
        _loc = InputKey.Parse(s.HuntLocKey);
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

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        if (_watcher != null) { _watcher.LineRead += OnLine; _watcher.Start(fromStart: false); }
        Log?.Invoke($"HUNT started — target={_target.Display}, con={_con.Display}, move={_fwd.Display}/{_left.Display}/{_right.Display}/{_back.Display}"
                    + (_loc.IsNone ? "" : $", /loc key={_loc.Display}") + ". Keep EQ focused; F12 stops. Watch it.");
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
        else if (raw.Contains("too far away", StringComparison.OrdinalIgnoreCase)) _tooFar = true;
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
                    // 1) SEEK — wander within explored bounds (and the tether), then target + consider
                    Stats.State = "seeking";
                    await MaybeLoc(ct);
                    await Wander(ct);
                    if (!_sink.Ready) continue;
                    _sink.Send(_target);                    // target nearest NPC (Tab by default)
                    await Task.Delay(Vary(350), ct);
                    _lastCon = ConsiderDifficulty.Unknown;
                    _lastAttitude = ConsiderAttitude.Unknown;
                    _lastConText = "";
                    _sink.Send(_con);                       // /consider the target (key or mouse5)
                    await Task.Delay(Vary(750), ct);        // wait for the con line to land

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

                // 2) FIGHT — run the rotation until the mob dies / we die / timeout
                Stats.State = "fighting"; Stats.Fights++;
                _mobDead = false; _cantSee = false; _tooFar = false;
                DateTime fightStart = DateTime.Now, lastOut = DateTime.Now;
                int i = 0, sweep = 0, swingsSeen = _ourSwings;
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
                    if (_ourSwings != swingsSeen) { swingsSeen = _ourSwings; sweep = 0; lastOut = DateTime.Now; }
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
                if (_mobDead) { Stats.Kills++; Log?.Invoke($"Kill #{Stats.Kills}."); }
                _attacked = false;                          // a fight resolves the defensive trigger

                // 3) REST — time-based (no HP/mana in the log; see class notes)
                Stats.State = "resting";
                int rest = Math.Max(0, _s.HuntRestSeconds);
                for (int t = 0; t < rest && !ct.IsCancellationRequested; t++) await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("Hunt error: " + ex.Message); }
        finally { ReleaseKeys(); }
    }

    /// <summary>Periodically fire the user's /loc macro key so position stays live for bounds/heatmap.
    /// A tight tether needs a fast fix: position refreshes every ~2–3s when the radius is small.</summary>
    private async Task MaybeLoc(CancellationToken ct)
    {
        if (_loc.IsNone || !_sink.Ready) return;
        int every = Math.Max(2, _s.HuntLocEverySeconds);
        if (_s.HuntTetherEnabled && _s.HuntTetherRadius <= 100) every = Math.Min(every, 3);
        if ((DateTime.Now - _lastLoc).TotalSeconds < every) return;
        _sink.Send(_loc);
        _lastLoc = DateTime.Now;
        await Task.Delay(Vary(120), ct);
    }

    /// <summary>Run forward with strafes, an occasional back-step, and right-mouse look-around;
    /// turn back if we reach the explored edge.</summary>
    private async Task Wander(CancellationToken ct)
    {
        if (!_sink.Ready) return;

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

    private int Vary(int ms) => _s.Vary(ms, _rng);
}
