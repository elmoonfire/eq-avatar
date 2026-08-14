using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>Legacy grid, kept only as the fallback for a plan made before the icon scan. The
    /// UI no longer asks for these: counting slots was a question with no good answer, and the
    /// answer it produced was clicked whether or not anything was in the square.</summary>
    public int Columns { get; set; } = 5;
    public int Rows { get; set; } = 2;

    /// <summary>The copy's icon, learned from a tight box round one of them — the same 6×6×3
    /// signature the Quest Runner uses, matched with the same code.</summary>
    public double[]? IconSig { get; set; }
    public double IconW { get; set; }
    public double IconH { get; set; }
    /// <summary>Pictures of what each pick learned, keyed "place"/"merge"/"bag"/"tier"/"item".</summary>
    public Dictionary<string, PickShot> Shots { get; set; } = new();
    /// <summary>The item's name, used to look its real stats up so the forecast can show what the
    /// projected tier is actually WORTH, not just which number it reaches.</summary>
    public string ItemName { get; set; } = "";

    [JsonIgnore] public bool HasIcon => IconSig is { Length: 108 };
    [JsonIgnore] public bool HasIconSize => IconW > 0.002 && IconH > 0.002;
    /// <summary>True when the sweep can find copies by sight instead of walking a guessed grid.</summary>
    [JsonIgnore] public bool ScanReady => BagSet && HasIcon && HasIconSize;

    /// <summary>The tier counter on the target item's window — the "4/32" line. This is the only
    /// thing that can tell us a merge actually happened.</summary>
    public double TierX { get; set; }
    public double TierY { get; set; }
    public double TierW { get; set; }
    public double TierH { get; set; }

    [JsonIgnore] public bool BagSet => BagW > 0.01 && BagH > 0.005;
    [JsonIgnore] public bool TierSet => TierW > 0.005 && TierH > 0.003;
    /// <summary>ScanReady, not just BagSet: a plan whose icon never took is a plan that silently
    /// falls back to a 5×2 grid this version's UI no longer even shows you, clicking ten arbitrary
    /// points in your bags. Refusing is the honest answer.</summary>
    [JsonIgnore] public bool Ready => PlaceBox.Set && MergeButton.Set && ScanReady;

    public string Missing()
    {
        var gaps = new List<string>();
        if (!PlaceBox.Set) gaps.Add("the Place Item box");
        if (!MergeButton.Set) gaps.Add("the Merge Item button");
        if (!BagSet) gaps.Add("the bag area holding the copies");
        if (!HasIcon) gaps.Add("the copy's icon (drag a tight box round one)");
        else if (!HasIconSize) gaps.Add("a re-pick of the copy's icon (the old one stored no size)");
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
                    p.Shots ??= new Dictionary<string, PickShot>();
                    p.ItemName ??= "";
                    if (p.Columns <= 0) p.Columns = 5;
                    if (p.Rows <= 0) p.Rows = 2;
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

    /// <summary>
    /// Where the next copy actually is, right now. Null when the screen can't be read at all —
    /// which the caller must NOT confuse with "there are none left", because one is a reason to
    /// look again and the other is a reason to stop.
    /// </summary>
    private QuestFind.IconHit? FindCopy(List<(double X, double Y)>? skip = null)
    {
        if (!_plan.ScanReady) return null;
        using System.Drawing.Bitmap? frame = QuestFind.Capture(_hwnd());
        if (frame is null) return null;
        List<QuestFind.IconHit> all = QuestFind.FindAllIcons(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
            _plan.IconSig!, _plan.IconW, _plan.IconH, QuestFind.SlidingAcceptDistance);
        foreach (QuestFind.IconHit h in all)
        {
            bool skipped = skip is not null && skip.Any(k =>
                Math.Abs(k.X - h.X) < _plan.IconW * 0.5 && Math.Abs(k.Y - h.Y) < _plan.IconH * 0.5);
            if (!skipped) return h;
        }
        // Nothing acceptable left. Report the closest thing on screen anyway, so the caller can
        // tell "nothing here" (a number) from "couldn't look" (a null).
        return QuestFind.FindIconInRect(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
                                        _plan.IconSig!, _plan.IconW, _plan.IconH)
               ?? new QuestFind.IconHit(0, 0, -1, -1, 999);
    }

    /// <summary>
    /// Every copy visible in the bag area — the number the forecast is built on. Static so the
    /// page can ask it without starting a run: "how far does what I already have get me?" is a
    /// question you want answered BEFORE three thousand clicks, not after.
    /// </summary>
    public static int CountCopies(IntPtr hwnd, MergePlan plan)
    {
        if (!plan.ScanReady) return -1;
        using System.Drawing.Bitmap? frame = QuestFind.Capture(hwnd);
        if (frame is null) return -1;
        return QuestFind.FindAllIcons(frame, plan.BagX, plan.BagY, plan.BagW, plan.BagH,
                                      plan.IconSig!, plan.IconW, plan.IconH,
                                      QuestFind.SlidingAcceptDistance).Count;
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
        // Giving up here used to fall through and click the next bag square with the item still
        // held, dropping it wherever that landed. The one thing a role must never do is act when it
        // knows it cannot verify.
        Log?.Invoke("⚠ An item may still be on the cursor — check the bag before starting another sweep.");
        Finish($"⚠ Stopped after {Stats.Merged} merge(s): couldn't put a held item back. Check your cursor "
             + "and your bags before running again.");
    }

    /// <summary>Read the target item's "n/m" counter. Returns null when it can't be read.</summary>
    public async Task<(int Have, int Need)?> ReadTierAsync()
    {
        if (!_plan.TierSet) return null;
        string text = await ScreenText.ReadRectAsync(_hwnd(), _plan.TierX, _plan.TierY, _plan.TierW, _plan.TierH);
        return ParseTier(text);
    }

    /// <summary>
    /// Turn an OCR'd counter into numbers, with the same character repairs the sweep relies on
    /// (OCR reads "l"/"I" as ones and "O" as zero often enough to matter at this size). Public and
    /// static so the forecast can read the counter without starting a run — one parser, so the
    /// number on the page and the number the sweep trusts can never disagree.
    /// </summary>
    public static (int Have, int Need)? ParseTier(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match m = TierRx.Match(text.Replace('l', '1').Replace('I', '1').Replace('O', '0'));
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out int have) || !int.TryParse(m.Groups[2].Value, out int need)) return null;
        if (need <= 0 || have < 0 || have > need) return null;
        // The denominator IS the ladder step — 1, 2, 4 … 1024 — so anything else is OCR damage,
        // not a tier. Accepting "4/1000" once tells the forecast you are already a +10, and inside
        // a run a flickering denominator reads as "LEVELLED UP" for a merge that never happened.
        if (need > 1024 || (need & (need - 1)) != 0) return null;
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

            // Wait for the game to be in front AND to have drawn a frame before the very first
            // read. ReadTierAsync photographs the desktop at fixed coordinates: run it while the
            // app is still on top and it OCRs OUR pixels, then blames the user's pick — sending
            // them off to re-pick a box that was never wrong.
            // WaitFocus only gives up when the run was cancelled. Returning bare would skip Finish
            // — and Finish is the only thing that clears _alive, so Running would stay true with
            // nothing running and every other role would refuse to start until a restart.
            if (!await WaitFocus(ct))
            {
                ct.ThrowIfCancellationRequested();        // the handler below homes the cursor and finishes
                Finish("Stopped before the first slot — nothing was merged.");
                return;
            }
            await Task.Delay(600, ct);

            (int Have, int Need)? start = await ReadTierAsync();
            if (start is null)
            {
                Finish("Can't start — the tier counter didn't read. Keep the target item's window open and "
                     + "re-pick a tight box around just the \"n/m\" numbers.");
                return;
            }
            Stats.Tier = $"{start.Value.Have}/{start.Value.Need}";
            long lastScore = UpgradeScore.ScoreFrom(start.Value.Have, start.Value.Need) ?? -1;
            if (lastScore >= 0)
                Log?.Invoke($"· target is score {lastScore}/1024 — a +{UpgradeScore.TierFor(lastScore)}, "
                          + $"{UpgradeScore.Max - lastScore} base copies short of a +10.");
            Log?.Invoke(_plan.ScanReady
                ? $"Auto Merge: target is at {Stats.Tier}. Finding copies by their icon — every slot in the bag "
                  + "area gets looked at, and only squares that actually hold one get clicked."
                : $"Auto Merge: target is at {Stats.Tier}. Walking the old {_plan.Columns}×{_plan.Rows} grid — "
                  + "re-pick the copy's icon to switch to the precise scan.");

            // Compared against the PREVIOUS read, never against the opening one. A level-up resets
            // the counter and doubles the denominator (31/32 -> 0/64); baselining on the run's
            // first read would make "the total changed" true for every remaining slot, so every
            // empty square after the first ladder step would be counted and logged as a merge —
            // and the put-back branch, which is the only cursor recovery in the sweep, would never
            // run again.
            int lastHave = start.Value.Have, lastNeed = start.Value.Need;
            if (lastScore >= UpgradeScore.Max)
            {
                HumanizedMouse.MoveInstant(home.x, home.y);
                Finish("Nothing to do — that item is already a +10 (score 1024/1024). Merging more into it "
                     + "would consume copies for nothing.");
                return;
            }
            int blindMisses = 0;
            bool cancelled = false;

            // The bag is READ, not walked. A guessed grid clicks every square whether or not it holds
            // anything — which is how a sweep spends its night dropping the target item into empty
            // slots. When the copy's icon is known, each pass finds the best-matching square that
            // is actually there and clicks THAT; when nothing matches any more, the bag is empty of
            // copies and the run is genuinely done rather than merely finished walking.
            var grid = _plan.ScanReady ? null : new Queue<(int Col, int Row, double X, double Y)>(_plan.Slots());
            int passes = 0, blindLooks = 0, deadEnds = 0;
            // Squares that looked like a copy and did NOT move the counter. Without this the scan
            // re-finds the same square forever: the item you are merging INTO usually sits in the
            // same bag and carries the same icon, so once the real copies are gone the target
            // itself becomes the best match and gets picked up and put back until the guard trips.
            var tried = new List<(double X, double Y)>();
            while (true)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (!await WaitFocus(ct)) { cancelled = true; break; }
                if (++passes > 4000) { Log?.Invoke("⚠ stopping: 4000 passes is not a bag, it is a loop."); break; }

                double sx, sy;
                string where;
                if (grid is null)
                {
                    QuestFind.IconHit? copy = FindCopy(tried);
                    if (copy is null || copy.Dist > QuestFind.SlidingAcceptDistance)
                    {
                        // ONE bad frame is not an empty bag. A tell window, a tooltip or a capture
                        // that throws all look exactly like "no copies left" — and the old grid walk
                        // was immune to this, because a bad frame cost it one slot instead of the
                        // night. So look again before believing it, and if the LOOK is what failed,
                        // never finish with the word "Done".
                        blindLooks++;
                        if (blindLooks < 3)
                        {
                            Log?.Invoke($"· nothing matched this look ({blindLooks} of 3) — looking again.");
                            await Task.Delay(700, ct);
                            continue;
                        }
                        if (copy is null)
                        {
                            HumanizedMouse.MoveInstant(home.x, home.y);
                            Finish($"⚠ Stopped after {Stats.Merged} merge(s): couldn't read the bag area three "
                                 + "looks running. Is the game on screen and the bag still open?");
                            return;
                        }
                        Log?.Invoke($"No more copies in the bag area — closest match was {copy.Dist:0}, "
                                  + $"and a real one scores under {QuestFind.SlidingAcceptDistance:0}.");
                        break;
                    }
                    blindLooks = 0;
                    sx = copy.X; sy = copy.Y;
                    where = $"copy at {copy.X * 100:0.0}%, {copy.Y * 100:0.0}% (match {copy.Dist:0})";
                }
                else
                {
                    if (grid.Count == 0) break;
                    (int c, int r, double gx, double gy) = grid.Dequeue();
                    sx = gx; sy = gy;
                    where = $"slot {r + 1},{c + 1}";
                }

                Stats.Attempts++;
                Stats.State = where;

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
                    Log?.Invoke($"⚠ {where}: couldn't read the tier counter ({blindMisses} of 3).");
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

                // The score is the honest witness. The displayed numerator FALLS on a level-up
                // (518 shows as 6/512 the moment it passes 512), so "did the numerator change"
                // needed two special cases and still couldn't tell a rise from a fall. A score can
                // only go up, and by exactly what was fed in — which is also worth printing, since
                // a jump of 32 says you just merged a +5 rather than a fresh drop.
                long nowScore = UpgradeScore.ScoreFrom(now.Value.Have, now.Value.Need) ?? -1;
                bool levelledUp = now.Value.Need != lastNeed;
                bool moved = nowScore >= 0 && lastScore >= 0
                    ? nowScore > lastScore
                    : levelledUp || now.Value.Have != lastHave;      // unreadable score: the old test
                long gained = nowScore >= 0 && lastScore >= 0 ? nowScore - lastScore : 0;
                // UNCONDITIONAL, including the -1. Keeping the last good score across an
                // undecodable read meant the NEXT reading — a true one, one point higher after the
                // merge we already counted — looked like a fresh merge on an empty square: a
                // phantom that also reset deadEnds, the only guard against the scan picking the
                // target item up and putting it back forever.
                lastScore = nowScore;
                lastHave = now.Value.Have;
                lastNeed = now.Value.Need;

                if (moved)
                {
                    deadEnds = 0;
                    Stats.Merged++;
                    string worth = gained > 1 ? $" (+{gained} points — that copy was a +{UpgradeScore.TierFor(gained)})" : "";
                    Log?.Invoke(levelledUp && nowScore >= 0
                        ? $"✔ merged — LEVELLED UP to +{UpgradeScore.TierFor(nowScore)}, now {Stats.Tier}{worth}"
                        : $"✔ merged — now {Stats.Tier}{worth}");

                    if (nowScore >= UpgradeScore.Max)
                    {
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        Finish($"✔ DONE — that item is a +10. {Stats.Merged} merged this run. "
                             + "Anything still in the bag is spare.");
                        return;
                    }
                }
                else
                {
                    Stats.Skipped++;
                    // With the grid this was routine — it covers the whole bag, most of which is
                    // empty. With the icon scan it is NOT routine: something that looked like a
                    // copy did not merge, so say so rather than sliding past it.
                    if (grid is null)
                    {
                        tried.Add((sx, sy));
                        deadEnds++;
                        Log?.Invoke($"⚠ {where} looked like a copy but the tier counter didn't move — "
                                  + "not trying that square again.");
                    }
                    await ReturnHeldAsync(sx, sy, ct);
                    if (grid is null && deadEnds >= 5)
                    {
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        Finish($"⚠ Stopped after {Stats.Merged} merge(s): five squares in a row looked like copies "
                             + "and merged nothing. Either the icon pick matches something else, or the Place/Merge "
                             + "picks have moved. Check them before running again.");
                        return;
                    }
                }

                await Task.Delay(260 + _rng.Next(200), ct);
            }

            HumanizedMouse.MoveInstant(home.x, home.y);
            string skipped = Stats.Skipped > 0 ? $", {Stats.Skipped} that didn't move the counter" : "";
            Finish(cancelled
                ? $"Stopped part-way — {Stats.Merged} merged{skipped}. Target is at {Stats.Tier}."
                : $"Done — {Stats.Merged} merged{skipped}. Target is at {Stats.Tier}.");
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
