using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;
using EQAvatar.Spike.Map;

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

    private volatile bool _mobDead, _selfDead, _rmbDown;
    private ConsiderDifficulty _lastCon = ConsiderDifficulty.Unknown;
    private double? _x, _y;
    private DateTime _lastLoc = DateTime.MinValue;

    // resolved binds
    private readonly InputKey _fwd, _left, _right, _back, _target, _con, _loc;

    public bool Running => _cts is { IsCancellationRequested: false };

    public HuntRole(IInputSink sink, List<(InputKey, int)> rotation, string? logPath, AppSettings s, HeatmapModel heat)
    {
        _sink = sink; _rotation = rotation; _s = s; _heat = heat;
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath);
        _fwd = InputKey.Parse(s.HuntForwardKey);
        _left = InputKey.Parse(s.HuntLeftKey);
        _right = InputKey.Parse(s.HuntRightKey);
        _back = InputKey.Parse(s.HuntBackKey);
        _target = InputKey.Parse(s.HuntTargetKey);
        _con = InputKey.Parse(s.HuntConsiderKey);
        _loc = InputKey.Parse(s.HuntLocKey);
    }

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
        Stats.State = "stopped";
        Log?.Invoke("Hunt stopped.");
        Stopped?.Invoke();
    }

    private void OnLine(string raw)
    {
        LogEvent ev = LogEventParser.Parse(raw);
        switch (ev.Kind)
        {
            case LogEventKind.Location: _x = ev.X; _y = ev.Y; break;
            case LogEventKind.Consider:
                _lastCon = LogEventParser.ConsiderReading(ev.Text);
                Stats.LastCon = ev.Text; Stats.MobsConsidered++;
                Log?.Invoke($"con: {_lastCon} — {ev.Text}");
                break;
            case LogEventKind.Kill: _mobDead = true; break;
            case LogEventKind.Death: _selfDead = true; break;
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_selfDead) { Log?.Invoke("Death detected — stopping hunt for safety."); Stats.Deaths++; break; }
                if (!_sink.Ready) { Stats.State = "paused (EQ not focused)"; await Task.Delay(400, ct); continue; }

                // 1) SEEK — wander within explored bounds, then target + consider
                Stats.State = "seeking";
                await MaybeLoc(ct);
                await Wander(ct);
                if (!_sink.Ready) continue;
                _sink.Send(_target);                        // target nearest NPC (Tab by default)
                await Task.Delay(Vary(350), ct);
                _lastCon = ConsiderDifficulty.Unknown;
                _sink.Send(_con);                           // /consider the target (key or mouse5)
                await Task.Delay(Vary(750), ct);            // wait for the con line to land

                // No con line came back → nothing was targeted. Don't flail at empty air: roam again.
                if (_lastCon == ConsiderDifficulty.Unknown)
                { Stats.State = "no target — roaming"; Log?.Invoke("No target — roaming for a mob."); continue; }

                if (_s.HuntSkipHardCons && _lastCon == ConsiderDifficulty.Suicidal)
                { Stats.Skipped++; Log?.Invoke("Skipping a too-hard target."); continue; }

                // 2) FIGHT — run the rotation until the mob dies / we die / timeout
                Stats.State = "fighting"; Stats.Fights++;
                _mobDead = false;
                DateTime fightStart = DateTime.Now;
                int i = 0;
                while (!ct.IsCancellationRequested && !_mobDead && !_selfDead)
                {
                    if (!_sink.Ready) { await Task.Delay(300, ct); continue; }
                    if ((DateTime.Now - fightStart).TotalSeconds > _s.HuntMaxFightSeconds)
                    { Log?.Invoke("Fight timed out — moving on."); break; }
                    (InputKey key, int delay) = _rotation.Count > 0 ? _rotation[i % _rotation.Count] : (InputKey.FromVk(0x34), 1400); // default '4'
                    _sink.Send(key);
                    i++;
                    await Task.Delay(Vary(Math.Max(50, delay)), ct);
                }
                if (_mobDead) { Stats.Kills++; Log?.Invoke($"Kill #{Stats.Kills}."); }

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

    /// <summary>Periodically fire the user's /loc macro key so position stays live for bounds/heatmap.</summary>
    private async Task MaybeLoc(CancellationToken ct)
    {
        if (_loc.IsNone || !_sink.Ready) return;
        if ((DateTime.Now - _lastLoc).TotalSeconds < Math.Max(2, _s.HuntLocEverySeconds)) return;
        _sink.Send(_loc);
        _lastLoc = DateTime.Now;
        await Task.Delay(Vary(120), ct);
    }

    /// <summary>Run forward with strafes, an occasional back-step, and right-mouse look-around;
    /// turn back if we reach the explored edge.</summary>
    private async Task Wander(CancellationToken ct)
    {
        if (!_sink.Ready) return;

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

        if (_s.HuntLookAround && _rng.NextDouble() < 0.45)
            await LookAround(ct, big: false);
        if (_rng.NextDouble() < 0.35)
            await HoldKey(_rng.Next(2) == 0 ? _left : _right, Vary(220), ct);   // small strafe
        if (_rng.NextDouble() < 0.12)
            await HoldKey(_back, Vary(240), ct);                                // occasional back-step

        int lo = Math.Max(200, _s.HuntRunMsMin), hi = Math.Max(lo + 1, _s.HuntRunMsMax);
        await HoldKey(_fwd, _rng.Next(lo, hi), ct);
    }

    /// <summary>Hold right-mouse and nudge the cursor sideways to pan the view — human-like looking around.</summary>
    private async Task LookAround(CancellationToken ct, bool big)
    {
        if (!_s.HuntLookAround || !_sink.Ready) return;
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        _rmbDown = true;
        try
        {
            int steps = big ? _rng.Next(6, 12) : _rng.Next(3, 6);
            int dir = _rng.Next(2) == 0 ? -1 : 1;
            for (int i = 0; i < steps && !ct.IsCancellationRequested && _sink.Ready; i++)
            {
                InputProbe.MouseMoveRelative(dir * _rng.Next(16, 40), _rng.Next(-3, 4));
                await Task.Delay(28, ct);
            }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); _rmbDown = false; }
    }

    /// <summary>Hold a keyboard key for ms, releasing immediately if EQ loses focus or we're cancelled.
    /// Mouse binds are ignored for held movement (they're used as taps elsewhere).</summary>
    private async Task HoldKey(InputKey key, int ms, CancellationToken ct)
    {
        if (key.IsNone || key.IsMouse || !_sink.Ready) return;
        InputProbe.KeyDown(key.Vk);
        try
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end && !ct.IsCancellationRequested && _sink.Ready)
                await Task.Delay(50, ct);
        }
        finally { InputProbe.KeyUp(key.Vk); }
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
