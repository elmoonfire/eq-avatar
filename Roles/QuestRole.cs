using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Roles;

public sealed class QuestStats
{
    public int Completed, Attempts, Misses;
    public string State = "idle";
    public string LastLine = "";
}

/// <summary>
/// The Quest Runner: repeats a quest's hand-in for as long as you have the item.
///
/// WHAT IT AUTOMATES, AND WHY ONLY THAT. A quest is mostly travel, dialogue and killing — the
/// Grind role already does the killing, and travel is the Maps role's waypoints. What is left,
/// and what is genuinely tedious when a quest is farmed, is the hand-in: target, hail, pick the
/// item up, drop it on the NPC, press GIVE, and do it again. That is a fixed gesture, so that is
/// what this drives.
///
/// HOW IT KNOWS IT WORKED. It does not trust its own clicks. EQ's log is silent about inventory
/// and about what is on screen, but it is NOT silent about a completed hand-in: the server prints
/// "You offered 1 &lt;item&gt; to &lt;npc&gt;", then some of "has been updated", "You have been
/// given:", "Your faction standing with … has been adjusted", "You gain experience". Every loop
/// waits for one of those lines before counting a turn-in. A loop that clicks perfectly and
/// confirms nothing is a FAILED loop, and two in a row stop the run — because the overwhelmingly
/// likely cause is that the item ran out, and the second most likely is that a picked point is
/// wrong, and neither is improved by carrying on clicking.
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

    /// <summary>Set by the log reader the moment the server acknowledges a hand-in. Armed only
    /// for the window between pressing GIVE and the confirm deadline — see <see cref="_listening"/>.</summary>
    private volatile bool _offered, _advanced;
    /// <summary>True only while a hand-in is actually in flight. Without it, a line that lands
    /// between iterations — the tail of the previous hand-in, or a kill's experience line — would
    /// pre-arm the next iteration and it would "confirm" before it had given anything away.</summary>
    private volatile bool _listening;
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
    /// Decide whether a log line is the server acknowledging THIS hand-in.
    ///
    /// Deliberately narrow. The obvious wider net — count a faction adjustment or an experience
    /// line — is wrong here, because both of those also print for every mob anyone in the group
    /// kills, and a run that "confirms" off a passing kill never notices that the item ran out.
    /// So: the definitive "You offered …" line, or a quest-state line that names this quest, or
    /// "You have been given:" naming the reward. Nothing generic.
    /// </summary>
    private void OnLine(string line)
    {
        if (line.Length == 0 || !_listening) return;
        string l = line.ToLowerInvariant();
        string item = _script.Item.ToLowerInvariant();
        string quest = _script.Quest.ToLowerInvariant();

        // The line that means the server took the item out of our hands.
        if ((l.Contains("you offered") || l.Contains("you have given"))
            && (item.Length == 0 || l.Contains(item)))
        {
            _offered = true;
            Stats.LastLine = line.Trim();
            return;
        }

        // Quest-state lines, but only when they name the quest we're actually running.
        bool questLine = l.Contains("has been updated") || l.Contains("has been assigned the task")
                      || l.Contains("you have completed") || l.Contains("your task");
        if (questLine && quest.Length > 0 && l.Contains(quest))
        {
            _advanced = true;
            Stats.LastLine = line.Trim();
            return;
        }

        // The reward handover — specific enough on its own.
        if (l.Contains("you have been given"))
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

    /// <summary>Move and click one picked point. Returns false when the game isn't focused —
    /// the caller treats that as "paused", not as a failure.</summary>
    private bool ClickAt(ScreenPoint p, int settleMs)
    {
        if (!_sink.Ready) return false;
        if (Screen(p) is not (int x, int y)) return false;
        HumanizedMouse.MoveInstant(x + _rng.Next(-2, 3), y + _rng.Next(-2, 3));
        Thread.Sleep(90 + _rng.Next(70));
        if (!_sink.Ready) return false;                 // re-check: focus can be lost mid-gesture
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

    // ---------------------------------------------------------------- the loop

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            if (!_script.Layout.Ready)
            {
                Finish("Can't start — still need a pick for: " + _script.Layout.Missing()
                     + ". Use the three ◎ buttons on the quest's automation card.");
                return;
            }
            if (_watcher is null)
                Log?.Invoke("⚠ No log file, so hand-ins cannot be confirmed — it will click and count nothing. "
                          + "Set the log folder on the Log Reader page and restart the run.");

            Log?.Invoke($"Quest Runner: handing {_script.Item} to {_script.Npc}"
                      + (_script.Repeat > 0 ? $", {_script.Repeat} time(s)." : ", until the item runs out."));

            int consecutiveMisses = 0;
            (int x, int y) home = HumanizedMouse.CursorPos();

            while (!ct.IsCancellationRequested)
            {
                if (_script.Repeat > 0 && Stats.Completed >= _script.Repeat)
                { Finish($"Done — {Stats.Completed} hand-in(s) confirmed."); return; }

                if (!await WaitFocus(ct)) break;

                Stats.Attempts++;

                // 1. make sure the right NPC is selected
                if (_script.TargetByName && _script.Npc.Length > 0)
                {
                    Stats.State = "targeting";
                    if (!Say("/target " + _script.Npc)) { await Task.Delay(500, ct); continue; }
                    await Task.Delay(700 + _rng.Next(250), ct);
                }

                // 2. wake it up, and say anything the quest dialogue needs
                if (_script.HailFirst && _script.Npc.Length > 0)
                {
                    Stats.State = "hailing";
                    if (!Say("/say Hail, " + _script.Npc)) { await Task.Delay(500, ct); continue; }
                    await Task.Delay(900 + _rng.Next(350), ct);
                }
                foreach (string phrase in _script.SayPhrases)
                {
                    if (ct.IsCancellationRequested || phrase.Trim().Length == 0) continue;
                    if (!Say("/say " + phrase.Trim())) break;
                    await Task.Delay(900 + _rng.Next(300), ct);
                }

                // 3. the hand-in gesture itself
                Stats.State = "handing over";
                if (!ClickAt(_script.Layout.ItemSlot, 260)) { await Task.Delay(500, ct); continue; }
                if (!ClickAt(_script.Layout.Npc, 620)) { await Task.Delay(500, ct); continue; }

                // Arm the listener HERE, immediately before the button that commits the trade —
                // not at the top of the loop. Everything above takes seconds, and a confirmation
                // armed that early can be satisfied by the tail of the previous hand-in.
                _offered = _advanced = false;
                _listening = true;

                if (!ClickAt(_script.Layout.GiveButton, 500)) { _listening = false; await Task.Delay(500, ct); continue; }
                if (_script.Layout.Confirm.Set) ClickAt(_script.Layout.Confirm, 400);

                // 4. believe the log, not the clicks
                Stats.State = "waiting for the server";
                bool confirmed = false;
                DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_script.ConfirmSeconds, 3, 60));
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (_offered || _advanced) { confirmed = true; break; }
                    await Task.Delay(250, ct);
                }
                _listening = false;

                if (confirmed)
                {
                    consecutiveMisses = 0;
                    Stats.Completed++;
                    _script.LifetimeCompleted++;
                    Log?.Invoke($"✔ hand-in {Stats.Completed} confirmed — {Stats.LastLine}");
                    await Task.Delay(1200 + _rng.Next(600), ct);
                }
                else
                {
                    consecutiveMisses++;
                    Stats.Misses++;
                    Log?.Invoke($"✖ nothing came back from the server within {_script.ConfirmSeconds}s "
                              + $"(miss {consecutiveMisses} of 2).");
                    // Drop whatever might still be stuck to the cursor before trying again.
                    ClickAt(_script.Layout.ItemSlot, 300);
                    if (consecutiveMisses >= 2)
                    {
                        Finish($"Stopped after {Stats.Completed} confirmed hand-in(s): two in a row went unanswered. "
                             + "Most likely you're out of " + _script.Item
                             + " — if you're not, re-pick the three points and check the NPC is in reach.");
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        return;
                    }
                    await Task.Delay(1500, ct);
                }
            }

            HumanizedMouse.MoveInstant(home.x, home.y);
            Finish($"Stopped — {Stats.Completed} hand-in(s) confirmed this run.");
        }
        catch (OperationCanceledException)
        {
            Finish($"Stopped — {Stats.Completed} hand-in(s) confirmed this run.");
        }
        catch (Exception ex)
        {
            Diag.BotLog.Log("quest", "runner error: " + ex);
            Finish("Quest Runner error: " + ex.Message);
        }
    }
}
