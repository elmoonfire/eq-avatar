using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Login;

namespace EQAvatar.Spike.Roles;

/// <summary>
/// Everything Auto Merge needs to know, picked once off a real frame of the game and stored
/// normalized to the window.
///
/// The bag is described as a GRID rather than as a list of picked slots: a stack of 27 duplicates
/// spans most of a rucksack, and picking 27 points by hand is worse than doing the merges by hand.
/// Two numbers and one dragged box gives the same information.
/// </summary>
public sealed class MergePlan
{
    /// <summary>The "Place Item" box on the item you are merging INTO.</summary>
    public ScreenPoint PlaceBox { get; set; } = new();
    /// <summary>The "Merge Item" button beside it.</summary>
    public ScreenPoint MergeButton { get; set; } = new();

    /// <summary>The bag area holding the copies to be consumed (normalized rect).</summary>
    public double BagX { get; set; }
    public double BagY { get; set; }
    public double BagW { get; set; }
    public double BagH { get; set; }
    public int Columns { get; set; } = 5;
    public int Rows { get; set; } = 2;

    /// <summary>The tier counter on the target item's window — the "4/32" line. This is the only
    /// thing that can tell us a merge actually happened.</summary>
    public double TierX { get; set; }
    public double TierY { get; set; }
    public double TierW { get; set; }
    public double TierH { get; set; }

    [JsonIgnore] public bool BagSet => BagW > 0.01 && BagH > 0.005 && Columns > 0 && Rows > 0;
    [JsonIgnore] public bool TierSet => TierW > 0.005 && TierH > 0.003;
    [JsonIgnore] public bool Ready => PlaceBox.Set && MergeButton.Set && BagSet;

    public string Missing()
    {
        var gaps = new List<string>();
        if (!PlaceBox.Set) gaps.Add("the Place Item box");
        if (!MergeButton.Set) gaps.Add("the Merge Item button");
        if (!BagSet) gaps.Add("the bag area holding the copies");
        return gaps.Count == 0 ? "" : string.Join(", ", gaps);
    }

    /// <summary>Slot centres, left to right then top to bottom, as normalized window points.</summary>
    public IEnumerable<(int Col, int Row, double X, double Y)> Slots()
    {
        if (!BagSet) yield break;
        double cw = BagW / Columns, ch = BagH / Rows;
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Columns; col++)
                yield return (col, row, BagX + cw * (col + 0.5), BagY + ch * (row + 0.5));
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "mergeplan.json");

    private static MergePlan? _current;
    public static MergePlan Current => _current ??= Load();

    public static MergePlan Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                MergePlan? p = JsonSerializer.Deserialize<MergePlan>(File.ReadAllText(FilePath));
                if (p is not null)
                {
                    p.PlaceBox ??= new ScreenPoint();
                    p.MergeButton ??= new ScreenPoint();
                    return p;
                }
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------------------------------------------------------------- the upgrade arithmetic

    /// <summary>
    /// How many BASE (+0) items a given plus-level is worth. A +0 is one, and every level up costs
    /// as many base items again as everything below it — so +1 is 2, +2 is 4, +10 is 1,024.
    /// </summary>
    public static long BaseWorth(int plus) => plus < 0 ? 0 : 1L << Math.Min(plus, 40);

    /// <summary>Base items needed to go from <paramref name="plus"/> to the next level.</summary>
    public static long StepCost(int plus) => BaseWorth(Math.Max(0, plus));

    /// <summary>Base items still needed to reach <paramref name="target"/> from a given level and
    /// partial progress. Progress is counted in base items already merged into the current level.</summary>
    public static long Remaining(int plus, long progress, int target)
    {
        long need = 0;
        for (int p = Math.Max(0, plus); p < target; p++) need += StepCost(p);
        return Math.Max(0, need - Math.Max(0, progress));
    }
}

public sealed class MergeStats
{
    public int Merged, Attempts, Skipped;
    public string State = "idle";
    public string Tier = "";
}

/// <summary>
/// Auto Merge: walk a bag of duplicates and fold every one of them into the copy you pointed at.
///
/// WHY THIS EXISTS. The Talisman of Kejaar Kerrath has no drop — the only source is repeating a
/// quest — and the upgrade ladder doubles at every step, so a +10 is 1,024 of the base item. At
/// three clicks each that is three thousand clicks, and it is the same three clicks every time:
/// pick a copy out of the bag, drop it in the Place Item box, press Merge Item.
///
/// HOW IT KNOWS IT WORKED. The game says nothing in the log about merging, so the only witness is
/// the item's own tier counter — the "4/32" on the target item's window. It is READ BEFORE AND
/// AFTER every merge, and a merge only counts when the number actually moved. An empty bag slot
/// is expected (the run walks the whole grid), so a single failure just moves to the next slot;
/// several in a row means the counter can't be read or the picks are wrong, and the run stops
/// rather than clicking on blindly.
///
/// If no tier region is picked at all, the run refuses to start unattended and says why: without
/// it there is no difference between merging a hundred items and clicking an empty bag a hundred
/// times.
/// </summary>
public sealed class MergeRole
{
    public event Action<string>? Log;
    public event Action? Stopped;
    public MergeStats Stats { get; } = new();
    public bool Running => Volatile.Read(ref _alive) == 1;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private static readonly Regex TierRx = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private readonly MergePlan _plan;
    private readonly IInputSink _sink;
    private readonly Func<IntPtr> _hwnd;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;
    private int _finished;
    /// <summary>1 between Start() and the loop actually ending. Running is NOT "cancel hasn't been
    /// requested": a click sequence is over a second of Thread.Sleep, so between Stop() and the
    /// worker noticing, a token-based Running reads false and the UI cheerfully starts a SECOND
    /// sweep on top of the one still clicking.</summary>
    private int _alive;

    public MergeRole(MergePlan plan, IInputSink sink, Func<IntPtr> gameWindow)
    {
        _plan = plan;
        _sink = sink;
        _hwnd = gameWindow;
    }

    public void Start()
    {
        if (Running || Volatile.Read(ref _finished) != 0) return;
        _cts = new CancellationTokenSource();
        Volatile.Write(ref _alive, 1);
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private void Finish(string why)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        Volatile.Write(ref _alive, 0);
        Stats.State = "stopped";
        try { _cts?.Cancel(); } catch { }
        try { Log?.Invoke(why); } catch { }
        try { Stopped?.Invoke(); } catch { }
    }

    // ---------------------------------------------------------------- screen

    private (int x, int y)? Screen(double nx, double ny)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || !GetWindowRect(h, out RECT r)) return null;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return null;
        return (r.Left + (int)(nx * w), r.Top + (int)(ny * ht));
    }

    private bool ClickAt(double nx, double ny, int settleMs, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || !_sink.Ready) return false;
        if (Screen(nx, ny) is not (int x, int y)) return false;
        HumanizedMouse.MoveInstant(x + _rng.Next(-2, 3), y + _rng.Next(-2, 3));
        Thread.Sleep(80 + _rng.Next(60));
        if (ct.IsCancellationRequested || !_sink.Ready) return false;
        HumanizedMouse.Click(_rng);
        Thread.Sleep(settleMs + _rng.Next(80));
        return true;
    }

    private bool ClickAt(ScreenPoint p, int settleMs, CancellationToken ct = default)
        => p.Set && ClickAt(p.X, p.Y, settleMs, ct);

    /// <summary>
    /// Put a held copy back in the slot it came from, waiting for the game to come back to the
    /// foreground first.
    ///
    /// This is the difference between a paused sweep and a corrupted bag. A plain retry click is
    /// useless here: the reason the previous click failed is that EQ lost focus, and the retry
    /// tests exactly the same condition and does exactly as little. Meanwhile the copy is still on
    /// the cursor, and the next slot the sweep clicks is where it gets dropped.
    /// </summary>
    private async Task ReturnHeldAsync(double sx, double sy, CancellationToken ct)
    {
        for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)     // ~24 s, then give up
        {
            if (_sink.Ready && ClickAt(sx, sy, 260, ct)) return;
            Stats.State = "holding an item — waiting for the game window";
            try { await Task.Delay(400, ct); } catch { return; }
        }
        Log?.Invoke("⚠ An item may still be on the cursor — check the bag before starting another sweep.");
    }

    /// <summary>Read the target item's "n/m" counter. Returns null when it can't be read.</summary>
    public async Task<(int Have, int Need)?> ReadTierAsync()
    {
        if (!_plan.TierSet) return null;
        string text = await ScreenText.ReadRectAsync(_hwnd(), _plan.TierX, _plan.TierY, _plan.TierW, _plan.TierH);
        Match m = TierRx.Match(text.Replace('l', '1').Replace('I', '1').Replace('O', '0'));
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out int have) || !int.TryParse(m.Groups[2].Value, out int need)) return null;
        if (need <= 0 || have < 0 || have > need) return null;
        return (have, need);
    }

    private async Task<bool> WaitFocus(CancellationToken ct)
    {
        bool warned = false;
        while (!ct.IsCancellationRequested && !_sink.Ready)
        {
            if (!warned) { warned = true; Stats.State = "waiting for the game window"; Log?.Invoke("Paused — EverQuest isn't the focused window."); }
            await Task.Delay(400, ct);
        }
        return !ct.IsCancellationRequested;
    }

    // ---------------------------------------------------------------- the sweep

    private async Task LoopAsync(CancellationToken ct)
    {
        (int x, int y) home = HumanizedMouse.CursorPos();
        try
        {
            if (!_plan.Ready) { Finish("Can't start — still need a pick for: " + _plan.Missing() + "."); return; }
            if (!_plan.TierSet)
            {
                Finish("Can't start — the tier counter (the \"4/32\" on the target item's window) hasn't been "
                     + "picked. Without it there is no way to tell a merge from a click on an empty slot, and a "
                     + "run that can't tell the difference will happily click all night.");
                return;
            }

            (int Have, int Need)? start = await ReadTierAsync();
            if (start is null)
            {
                Finish("Can't start — the tier counter didn't read. Keep the target item's window open and "
                     + "re-pick a tight box around just the \"n/m\" numbers.");
                return;
            }
            Stats.Tier = $"{start.Value.Have}/{start.Value.Need}";
            Log?.Invoke($"Auto Merge: target is at {Stats.Tier}. Walking {_plan.Columns}×{_plan.Rows} slots.");

            // Compared against the PREVIOUS read, never against the opening one. A level-up resets
            // the counter and doubles the denominator (31/32 -> 0/64); baselining on the run's
            // first read would make "the total changed" true for every remaining slot, so every
            // empty square after the first ladder step would be counted and logged as a merge —
            // and the put-back branch, which is the only cursor recovery in the sweep, would never
            // run again.
            int lastHave = start.Value.Have, lastNeed = start.Value.Need;
            int blindMisses = 0;
            bool cancelled = false;

            foreach ((int col, int row, double sx, double sy) in _plan.Slots())
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (!await WaitFocus(ct)) { cancelled = true; break; }

                Stats.Attempts++;
                Stats.State = $"slot {row + 1},{col + 1}";

                if (!ClickAt(sx, sy, 240, ct)) { await Task.Delay(400, ct); continue; }   // pick the copy up

                // The copy is now ON THE CURSOR. Every exit from here has to put it back in its own
                // slot first, and has to WAIT for focus to do it: the reason a click failed is that
                // EQ isn't foreground, and an immediate retry tests the same condition and does
                // exactly as little — while the copy rides the cursor into the next slot's click.
                if (!ClickAt(_plan.PlaceBox, 320, ct)) { await ReturnHeldAsync(sx, sy, ct); await Task.Delay(400, ct); continue; }
                if (!ClickAt(_plan.MergeButton, 420, ct)) { await ReturnHeldAsync(sx, sy, ct); await Task.Delay(400, ct); continue; }

                await Task.Delay(320, ct);
                (int Have, int Need)? now = await ReadTierAsync();

                if (now is null)
                {
                    blindMisses++;
                    Log?.Invoke($"slot {row + 1},{col + 1}: couldn't read the tier counter ({blindMisses} of 3).");
                    await ReturnHeldAsync(sx, sy, ct);
                    if (blindMisses >= 3)
                    {
                        Finish($"Stopped after {Stats.Merged} merge(s): the tier counter stopped reading three "
                             + "times running. Is the target item's window still open and unobstructed?");
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        return;
                    }
                    continue;
                }

                blindMisses = 0;
                Stats.Tier = $"{now.Value.Have}/{now.Value.Need}";

                bool levelledUp = now.Value.Need != lastNeed;
                bool moved = levelledUp || now.Value.Have != lastHave;
                lastHave = now.Value.Have;
                lastNeed = now.Value.Need;

                if (moved)
                {
                    Stats.Merged++;
                    Log?.Invoke(levelledUp
                        ? $"✔ merged — LEVELLED UP, now {Stats.Tier}"
                        : $"✔ merged — now {Stats.Tier}");
                }
                else
                {
                    Stats.Skipped++;
                    // Expected: the grid covers the whole bag and most runs have empty squares in
                    // it. Put anything held back and move on without ceremony.
                    await ReturnHeldAsync(sx, sy, ct);
                }

                await Task.Delay(260 + _rng.Next(200), ct);
            }

            HumanizedMouse.MoveInstant(home.x, home.y);
            Finish(cancelled
                ? $"Stopped part-way — {Stats.Merged} merged, {Stats.Skipped} empty slot(s) skipped. Target is at {Stats.Tier}."
                : $"Done — {Stats.Merged} merged, {Stats.Skipped} empty slot(s) skipped. Target is at {Stats.Tier}.");
        }
        catch (OperationCanceledException)
        {
            try { HumanizedMouse.MoveInstant(home.x, home.y); } catch { }
            Finish($"Stopped — {Stats.Merged} merged this run. Target is at {Stats.Tier}.");
        }
        catch (Exception ex)
        {
            Diag.BotLog.Log("merge", "error: " + ex);
            Finish("Auto Merge error: " + ex.Message);
        }
    }
}
