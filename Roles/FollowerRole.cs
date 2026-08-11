using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Roles;

public sealed class FollowerStats
{
    public int Assists, Kills, Refollows, Deaths;
    public string State = "idle";
}

/// <summary>
/// FOLLOWER role — the group-play half of the app. Run this instance on the PC (or window) where
/// the SECOND character plays. It keeps that character glued to a leader and joins their fights:
///
///   • FOLLOW  — types "/target &lt;leader&gt;" + "/follow" into chat (no in-game socials needed),
///     and re-issues it on a timer and after every fight so a missed follow self-heals.
///   • ASSIST  — watches this character's own log; when the leader's swings/casts show up
///     ("Leader hits…", "Leader begins casting…"), it types "/assist &lt;leader&gt;" + "/attack on"
///     and runs your rotation until the mob dies or the fight goes quiet.
///   • SAFETY  — same rules as Grind: input only fires while EQ is focused (tab away = instant
///     pause), F12 stops it, and it stops itself if YOU die.
///
/// Honest limits: assist detection rides on the log, so the leader must be close enough that their
/// melee/cast lines land in this client's log (normal grouping range is fine). /follow is EQ's own
/// — it can still lose the leader on cliffs/water; the re-follow timer is the recovery.
/// </summary>
public sealed class FollowerRole
{
    public event Action<string>? Log;
    public event Action? Stopped;
    public FollowerStats Stats { get; } = new();

    private readonly IInputSink _sink;
    private readonly AppSettings _s;
    private readonly List<(InputKey key, int delayMs)> _rotation;
    private readonly EqLogWatcher? _watcher;
    private readonly Random _rng = new();
    private readonly string _leader;
    private readonly Regex _leaderSwing;
    private static readonly Regex SelfSwing = new(
        @"^You (hit|slash|pierce|crush|kick|bash|punch|strike|backstab|bite|maul|try to)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private CancellationTokenSource? _cts;
    private volatile bool _selfDead, _mobDead;
    private DateTime _lastSwing = DateTime.MinValue;    // last leader/self combat line seen
    private DateTime _lastFollow = DateTime.MinValue;

    public bool Running => _cts is { IsCancellationRequested: false };

    public FollowerRole(IInputSink sink, List<(InputKey, int)> rotation, string? logPath, AppSettings s)
    {
        _sink = sink; _rotation = rotation; _s = s;
        _leader = (s.FollowerLeader ?? "").Trim();
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath);
        _leaderSwing = new Regex(
            "^" + Regex.Escape(_leader) +
            @" (hits|slashes|pierces|crushes|kicks|bashes|punches|strikes|backstabs|bites|mauls|gores|frenzies|tries to \w+|begins casting|begins to cast)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        if (_watcher != null) { _watcher.LineRead += OnLine; _watcher.Start(fromStart: false); }
        Log?.Invoke($"FOLLOWER started — leader: {_leader}. Keep EQ focused on this PC; F12 stops. "
                    + (_watcher is null ? "NO LOG FOUND — auto-assist is blind until the log folder is set." : "Watching the log for the leader's fights."));
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_watcher != null) { _watcher.LineRead -= OnLine; _watcher.Dispose(); }
        Stats.State = "stopped";
        Log?.Invoke("Follower stopped.");
        Stopped?.Invoke();
    }

    private void OnLine(string raw)
    {
        // Strip the "[Tue Aug 11 ...] " stamp so the name-anchored regexes line up.
        int i = raw.IndexOf("] ", StringComparison.Ordinal);
        string txt = i >= 0 ? raw[(i + 2)..] : raw;

        if (_leaderSwing.IsMatch(txt) || SelfSwing.IsMatch(txt)) _lastSwing = DateTime.Now;

        LogEvent ev = LogEventParser.Parse(raw);
        switch (ev.Kind)
        {
            case LogEventKind.Kill: _mobDead = true; break;
            case LogEventKind.Death: _selfDead = true; break;
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
            // Take the leash immediately so the first thing the character does is fall in line.
            await EnsureFollow(ct, "initial follow");

            while (!ct.IsCancellationRequested)
            {
                if (_selfDead) { Stats.Deaths++; Log?.Invoke("Death detected — stopping follower for safety."); break; }
                if (!_sink.Ready) { Stats.State = "paused (EQ not focused)"; await Task.Delay(400, ct); continue; }

                bool leaderFighting = (DateTime.Now - _lastSwing).TotalSeconds <= 3;
                if (_s.FollowerAutoAssist && leaderFighting)
                {
                    await Fight(ct);
                    continue;
                }

                Stats.State = "following";
                if ((DateTime.Now - _lastFollow).TotalSeconds >= Math.Max(10, _s.FollowerRefollowSeconds))
                    await EnsureFollow(ct, "periodic re-follow");

                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("Follower error: " + ex.Message); }
    }

    /// <summary>Join the leader's fight: /assist, /attack on, rotation until it dies or goes quiet.</summary>
    private async Task Fight(CancellationToken ct)
    {
        Stats.State = "assisting";
        Stats.Assists++;
        await Task.Delay(Vary(Math.Max(200, _s.FollowerAssistDelayMs)), ct);   // human-ish reaction time
        if (!_sink.Ready || ct.IsCancellationRequested) return;

        Chat($"/assist {_leader}");
        await Task.Delay(Vary(420), ct);
        Chat("/attack on");
        Log?.Invoke($"Assisting {_leader} — attacking.");

        _mobDead = false;
        DateTime fightStart = DateTime.Now;
        int i = 0;
        Stats.State = "fighting";
        while (!ct.IsCancellationRequested && !_mobDead && !_selfDead)
        {
            if (!_sink.Ready) { Stats.State = "paused (EQ not focused)"; await Task.Delay(300, ct); continue; }
            Stats.State = "fighting";
            if ((DateTime.Now - fightStart).TotalSeconds > Math.Max(5, _s.FollowerMaxFightSeconds))
            { Log?.Invoke("Fight timed out — breaking off."); break; }
            if ((DateTime.Now - _lastSwing).TotalSeconds > Math.Max(3, _s.FollowerCombatLingerSeconds))
            { Log?.Invoke("Fight went quiet — breaking off."); break; }

            if (_rotation.Count > 0)
            {
                (InputKey key, int delay) = _rotation[i % _rotation.Count];
                _sink.Send(key);
                i++;
                await Task.Delay(Vary(Math.Max(50, delay)), ct);
            }
            else await Task.Delay(250, ct);
        }
        if (_mobDead) { Stats.Kills++; Log?.Invoke($"Kill #{Stats.Kills} (with {_leader})."); }

        if (_sink.Ready && !ct.IsCancellationRequested)
        {
            Chat("/attack off");
        }

        // Brief rest, then pick the leader back up before they wander off.
        Stats.State = "resting";
        int rest = Math.Max(0, _s.FollowerRestSeconds);
        for (int t = 0; t < rest && !ct.IsCancellationRequested; t++) await Task.Delay(1000, ct);
        _lastSwing = DateTime.MinValue;                 // don't instantly re-enter Fight on stale lines
        await EnsureFollow(ct, "after the fight");
    }

    /// <summary>Target the leader and /follow them. Self-heals a lost follow.</summary>
    private async Task EnsureFollow(CancellationToken ct, string why)
    {
        if (!_sink.Ready || ct.IsCancellationRequested || string.IsNullOrEmpty(_leader)) return;
        Stats.State = "re-following";
        Chat($"/target {_leader}");
        await Task.Delay(Vary(420), ct);
        if (!_sink.Ready || ct.IsCancellationRequested) return;
        Chat("/follow");
        _lastFollow = DateTime.Now;
        Stats.Refollows++;
        Log?.Invoke($"/follow {_leader} ({why}).");
    }

    private void Chat(string cmd) => ChatTyper.SendCommand(cmd, Vary);

    private int Vary(int ms) => _s.Vary(ms, _rng);
}
