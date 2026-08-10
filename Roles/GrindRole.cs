using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Roles;

public sealed class GrindStats
{
    public int KeysSent;
    public int Kills;
    public int XpGains;
    public int Loops;
    public bool Paused;
}

/// <summary>
/// The first real automation role. It cycles a user-defined key rotation through an
/// <see cref="IInputSink"/> and watches the EQL log for safety + stats. Because the
/// foreground sink only fires when EQL is focused, the role auto-pauses the instant you
/// tab away and resumes when you tab back — no keys ever leak into other apps.
///
/// This is intentionally engine-only: no game knowledge is hard-coded beyond the log
/// wording, so the same engine will drive smarter rotations (and other sinks) later.
/// </summary>
public sealed class GrindRole
{
    public event Action<string>? Log;
    public event Action? Stopped;

    private readonly IInputSink _sink;
    private readonly List<(InputKey key, int delayMs)> _rotation;
    private readonly bool _stopOnDeath;
    private readonly EqLogWatcher? _watcher;
    private readonly AppSettings _settings;
    private readonly Random _rng = new();
    private DateTime _pausedUntil = DateTime.MinValue;
    private CancellationTokenSource? _cts;

    public GrindStats Stats { get; } = new();
    public bool Running => _cts is { IsCancellationRequested: false };

    public GrindRole(IInputSink sink, List<(InputKey, int)> rotation, bool stopOnDeath, string? logPath, AppSettings settings)
    {
        _sink = sink;
        _rotation = rotation;
        _stopOnDeath = stopOnDeath;
        _settings = settings;
        if (!string.IsNullOrEmpty(logPath))
            _watcher = new EqLogWatcher(logPath);
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        if (_watcher != null)
        {
            _watcher.LineRead += OnLine;
            _watcher.Start(fromStart: false);
        }
        Log?.Invoke($"Grind started via {_sink.Name}. Rotation of {_rotation.Count} key(s).");
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_watcher != null) { _watcher.LineRead -= OnLine; _watcher.Dispose(); }
        Log?.Invoke("Grind stopped.");
        Stopped?.Invoke();
    }

    private async Task Loop(CancellationToken ct)
    {
        int i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_rotation.Count == 0) { await Task.Delay(250, ct); continue; }

                // Tell-pause: an incoming /tell holds the role for a while.
                if (DateTime.Now < _pausedUntil)
                {
                    Stats.Paused = true;
                    await Task.Delay(500, ct);
                    continue;
                }

                if (!_sink.Ready)
                {
                    // Game not focused → paused. Poll until it comes back.
                    if (!Stats.Paused) { Stats.Paused = true; Log?.Invoke("Paused — EQL is not the focused window."); }
                    await Task.Delay(300, ct);
                    continue;
                }
                if (Stats.Paused) { Stats.Paused = false; Log?.Invoke("Resumed."); }

                var (key, delay) = _rotation[i % _rotation.Count];
                if (_sink.Send(key))
                {
                    Stats.KeysSent++;
                    if (i % _rotation.Count == _rotation.Count - 1) Stats.Loops++;
                    i++;
                }
                // Random variance so timings are never metronomic.
                await Task.Delay(_settings.Vary(Math.Max(50, delay), _rng), ct);
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex) { Log?.Invoke("Loop error: " + ex.Message); }
    }

    private void OnLine(string raw)
    {
        LogEvent ev = LogEventParser.Parse(raw);
        switch (ev.Kind)
        {
            case LogEventKind.Kill: Stats.Kills++; break;
            case LogEventKind.Experience: Stats.XpGains++; break;
            case LogEventKind.Tell:
                if (_settings.PauseOnTell)
                {
                    _pausedUntil = DateTime.Now.AddMinutes(_settings.TellPauseMinutes);
                    Log?.Invoke($"Incoming /tell — pausing {_settings.TellPauseMinutes} min (until {_pausedUntil:T}).");
                    // Preset / AI auto-replies land here in the next build (delay {_settings.TellResponseDelaySeconds}s).
                }
                break;
            case LogEventKind.Death:
                Log?.Invoke("Death detected in the log.");
                if (_stopOnDeath) { Log?.Invoke("Stop-on-death is on — halting."); Stop(); }
                break;
        }
    }

    /// <summary>Parse "4,1400" style lines into (InputKey, delayMs) pairs. Keys: 0-9, A-Z, F1-F24,
    /// Tab/Space/Enter, arrows, and mouse1..mouse5 (so "mouse5,1200" works).</summary>
    public static List<(InputKey key, int delayMs)> ParseRotation(string text)
    {
        var list = new List<(InputKey, int)>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split(',');
            string key = parts[0].Trim();
            int delay = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int d) ? d : 1000;
            InputKey ik = InputKey.Parse(key);
            if (ik.IsNone) continue;
            list.Add((ik, delay));
        }
        return list;
    }
}
