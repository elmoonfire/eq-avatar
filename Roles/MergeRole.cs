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
using EQAvatar.Spike.Data;
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

    /// <summary>
    /// The SAME box at 12×12 — the confirm signature. 6×6 finds; this decides.
    ///
    /// Thirty-six average colours cannot separate two icons drawn from one palette: a Talisman of
    /// Kejaar Kerrath and a Desecrated Kejaar Totem are both brown, bone and gold in roughly the
    /// same places, which is how a sweep merged two real copies, ran out, and went looking for a
    /// third among the totems. 144 cells over the same pixels can.
    ///
    /// Null on any plan made before this existed. Rather than refuse to run — the coarse pick is
    /// still a good pick — the sweep LEARNS it from the first merge the tier counter confirms:
    /// whatever moved the counter was, by definition, a real copy.
    /// </summary>
    public double[]? IconSigFine { get; set; }

    /// <summary>
    /// Icons that looked like a copy and merged nothing. Fine-grid signatures, learned in the field
    /// and kept: the totem she picked up last night is the totem still in the bag tonight.
    ///
    /// A candidate closer to one of these than to the copy itself is not clicked at all — which is
    /// the difference between "she stopped after five wrong items" and "she never touched one".
    /// </summary>
    public List<double[]> RejectSigs { get; set; } = new();
    /// <summary>Bounded, because these live in a file that is rewritten in full on every trivial
    /// edit and each one is 432 numbers. Twenty-four distinct look-alikes in one bag would already
    /// mean the icon pick is wrong.</summary>
    public const int MaxRejects = 24;
    /// <summary>Pictures of what each pick learned, keyed "place"/"merge"/"bag"/"tier"/"item".</summary>
    public Dictionary<string, PickShot> Shots { get; set; } = new();
    /// <summary>The item's name, used to look its real stats up so the forecast can show what the
    /// projected tier is actually WORTH, not just which number it reaches.</summary>
    public string ItemName { get; set; } = "";

    [JsonIgnore] public bool HasIcon => IconSig is { Length: 108 };
    [JsonIgnore] public bool HasIconSize => IconW > 0.002 && IconH > 0.002;
    [JsonIgnore] public bool HasFineIcon => IconSigFine is { Length: QuestFind.SigLenFine };
    /// <summary>Only well-formed rejects. A list that picked up a wrong-length entry (a hand-edited
    /// file, a half-written save) must not make every comparison throw inside the sweep's loop.</summary>
    [JsonIgnore] public IEnumerable<double[]> Rejects => RejectSnapshot();

    /// <summary>A COPY, taken under the save gate. The sweep appends to this list from its own
    /// thread while the page draws the count and the page's "forget them" replaces it wholesale;
    /// handing out the live list means a `foreach` on the UI thread throwing "Collection was
    /// modified" out of the middle of a render pass.</summary>
    public List<double[]> RejectSnapshot()
    {
        lock (SaveGate)
            return (RejectSigs ?? new List<double[]>()).Where(r => r is { Length: QuestFind.SigLenFine }).ToList();
    }

    [JsonIgnore] public int RejectCount { get { lock (SaveGate) return RejectSnapshot().Count; } }

    /// <summary>Unlearn a picture she was wrong about. Returns true if one went.</summary>
    public bool ForgetReject(double[] fine)
    {
        lock (SaveGate)
        {
            if (RejectSigs is null) return false;
            for (int i = 0; i < RejectSigs.Count; i++)
                if (RejectSigs[i] is { Length: QuestFind.SigLenFine } r
                    && QuestFind.SigDistance(fine, r) <= RejectHitDistance)
                { RejectSigs.RemoveAt(i); return true; }
            return false;
        }
    }

    public void ForgetAllRejects() { lock (SaveGate) RejectSigs = new List<double[]>(); }

    /// <summary>How close two pictures must be to count as the same one. Shared by the veto and by
    /// the un-learning above, so a reject she stops honouring is the same reject she removes.</summary>
    public const double RejectHitDistance = 22;

    /// <summary>Remember an icon that looked like a copy and merged nothing, oldest dropped first.
    /// Rounded to one decimal: these ride in a JSON file rewritten in full on every edit, and
    /// seventeen significant figures of a colour average is noise stored at full price.</summary>
    public void LearnReject(double[] fine)
    {
        if (fine is not { Length: QuestFind.SigLenFine }) return;
        // Under the same gate as Save. The sweep learns from its own thread while the UI can be
        // serializing this very object — an item name losing focus is enough — and a List that grows
        // mid-serialization throws "Collection was modified" into a catch that says nothing.
        lock (SaveGate)
        {
            RejectSigs ??= new List<double[]>();
            RejectSigs.Add(fine.Select(v => Math.Round(v, 1)).ToArray());
            while (RejectSigs.Count > MaxRejects) RejectSigs.RemoveAt(0);
        }
    }
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
                    p.RejectSigs ??= new List<double[]>();
                    if (p.Columns <= 0) p.Columns = 5;
                    if (p.Rows <= 0) p.Rows = 2;
                    return p;
                }
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Write the plan out, temp-then-replace.
    ///
    /// It used to be a plain overwrite, which was survivable while this file was only written when
    /// someone pressed a pick button. The sweep now writes it mid-run — every time it learns a
    /// look-alike — so a torn write is no longer a lost setting; it is every pick the user ever
    /// made, gone, in the middle of the night.
    /// </summary>
    /// <summary>Serialization and the file swap both happen under this. Two threads writing the
    /// same temp path is an IOException; one thread serializing while the other appends to
    /// RejectSigs is an InvalidOperationException. Both used to be swallowed.</summary>
    internal static readonly object SaveGate = new();

    /// <summary>Returns the reason it failed, or null on success — so a caller who NEEDS the write
    /// to have happened can say so. The old blanket catch meant a save that silently didn't happen
    /// was indistinguishable from one that did.</summary>
    public string? TrySave()
    {
        try
        {
            lock (SaveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Save() => TrySave();

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
    /// <summary>The name this role records under. Declared here rather than on the page, so the
    /// role that writes a detail line and the console that shows it cannot disagree about who
    /// spoke.</summary>
    public const string MergeSource = "Merge";

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

    /// <summary>The run has already announced why it ended. Nothing may speak or act after this.</summary>
    private bool Finished => Volatile.Read(ref _finished) != 0;

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

    // ---------------------------------------------------------------- telling copies from look-alikes
    //
    // Every number below is a threshold, and the house rule for thresholds in this app is that they
    // are tuned from the log rather than from theory — so every one of these decisions prints the
    // number it was made on when the console's detail switch is up.

    /// <summary>How far a candidate's 12×12 signature may sit from the copy's before it is treated
    /// as a different item. Generous on purpose: the confirm exists to catch a DIFFERENT icon (a
    /// totem lands far past this), not to police the few points a real copy drifts by when the
    /// sliding window lands half a pixel off.</summary>
    private const double FineLimit = 45;
    /// <summary>
    /// Once a merge has been confirmed by the counter, the run knows what a real copy of THIS item
    /// scores on THIS screen — but it knows it from the BEST-aligned copy in the bag, because the
    /// coarse search hands them over best-first. So this is a margin over that best case, and it is
    /// wide: the sliding window steps in thirds of an icon, and a third of an icon is half a cell
    /// at 6×6 but a whole cell at 12×12, so two genuine copies at different alignment phases score
    /// meaningfully differently.
    /// </summary>
    private const double FineDrift = 25;
    /// <summary>The measured bar can never ratchet BELOW this. One unusually clean first copy would
    /// otherwise pull the bar down past what a normally-aligned copy can reach, and — because the
    /// bar only ever widens on a confirmed merge, and nothing can be confirmed once everything is
    /// vetoed — nothing would ever open it back up. A one-way ratchet that starts from the best
    /// case is a trap, not a threshold.</summary>
    private const double FineFloor = 32;
    private const double RejectHitDistance = MergePlan.RejectHitDistance;
    /// <summary>How much CLOSER a candidate must sit to a known non-copy than to the real one before
    /// that comparison decides anything. Not a hair: a learned reject was captured through the same
    /// sliding window as the live candidate, while the copy's reference came from the user's own
    /// hand-dragged box, so the reject has a systematic head start and a bare "nearer" would let it
    /// win over a real copy.</summary>
    private const double RejectMargin = 10;
    /// <summary>Below this a "wrong" item is the same picture as the copy — which is what the item
    /// you are merging INTO looks like, sitting in the same bag wearing the same icon. Learning it
    /// as a reject would poison the plan against the real thing, so that case stays a
    /// don't-touch-that-square-again and never becomes a don't-touch-that-picture-again.</summary>
    private const double RejectMinDistance = 8;
    /// <summary>And a reject must also sit this far past the worst score a CONFIRMED merge produced
    /// this run. Anything inside that band is, by the run's own evidence, indistinguishable from a
    /// copy — and the counter failing to move has a second explanation that has nothing to do with
    /// the item: the merge worked and the item window simply had not repainted when it was read.</summary>
    private const double RejectSafety = 12;

    /// <summary>A candidate, with everything that was known about it before anything was clicked.</summary>
    private sealed record Candidate(QuestFind.IconHit Hit, double[]? Fine, double FineDist, double RejectDist);

    /// <summary>What one look at the bag produced.</summary>
    /// <param name="Read">False = the screen could not be read at all. The caller must NOT confuse
    /// that with "there are none left": one is a reason to look again, the other to stop.</param>
    /// <param name="KnownWrong">Squares holding a picture she has already learned isn't a copy.
    /// Those are settled and never need looking at again.</param>
    /// <param name="BelowBar">Squares the close-up said were a different picture. Kept SEPARATE
    /// because that verdict was made against a bar that can still widen: a merge confirmed later in
    /// the run raises it, and a square written off under the old bar deserves a second look rather
    /// than a life sentence handed down by the first copy that happened to be well aligned.</param>
    private sealed record Look(bool Read, Candidate? Pick, int Seen,
                               List<(double X, double Y)> KnownWrong,
                               List<(double X, double Y)> BelowBar,
                               double ClosestDist);

    /// <summary>
    /// Look at the bag and choose the next copy — or decide there isn't one.
    ///
    /// Two screens, in order of cost. The 6×6 sliding search proposes every square that could be a
    /// copy; the 12×12 confirm, run only on the proposals, decides. That second screen is the whole
    /// point of this method: the coarse signature is a colour average, and a Desecrated Kejaar
    /// Totem averages out to very nearly a Talisman of Kejaar Kerrath, so a sweep that merged its
    /// last real copy would find a totem, pick it up, fail to merge it, put it back, and go find
    /// the next totem. Five of those and it stopped — but it should never have touched the first.
    /// </summary>
    private Look LookForCopy(IReadOnlyCollection<(double X, double Y)> skip, double confirmedFine)
    {
        var knownWrong = new List<(double X, double Y)>();
        var belowBar = new List<(double X, double Y)>();
        if (!_plan.ScanReady) return new Look(false, null, 0, knownWrong, belowBar, 999);
        using System.Drawing.Bitmap? frame = QuestFind.Capture(_hwnd());
        if (frame is null) return new Look(false, null, 0, knownWrong, belowBar, 999);

        List<QuestFind.IconHit> all = QuestFind.FindAllIcons(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
            _plan.IconSig!, _plan.IconW, _plan.IconH, QuestFind.SlidingAcceptDistance);
        double limit = _plan.HasFineIcon && confirmedFine >= 0
            ? Math.Clamp(confirmedFine + FineDrift, FineFloor, FineLimit)
            : FineLimit;

        if (all.Count > 0)
            ActivityLog.Detail(MergeSource,
                $"look: {all.Count} square(s) under the coarse bar — "
                + string.Join(", ", all.Take(12).Select(h => $"{h.X * 100:0.0}%,{h.Y * 100:0.0}% @{h.Dist:0.0}"))
                + (all.Count > 12 ? " …" : ""));

        // Materialised ONCE. `Rejects` is a LINQ filter over the stored list, and re-running it
        // inside the candidate loop re-filters for every square in the bag.
        List<double[]> rejects = _plan.RejectSnapshot();
        foreach (QuestFind.IconHit h in all)
        {
            if (skip.Any(k => Math.Abs(k.X - h.X) < _plan.IconW * 0.5 && Math.Abs(k.Y - h.Y) < _plan.IconH * 0.5))
                continue;

            double[]? fine = QuestFind.SigFromRegion(frame, h.X - _plan.IconW / 2, h.Y - _plan.IconH / 2,
                                                     _plan.IconW, _plan.IconH, QuestFind.SigGridFine);
            double fineDist = fine is not null && _plan.HasFineIcon
                ? QuestFind.SigDistance(fine, _plan.IconSigFine!) : -1;
            double rejDist = double.MaxValue;
            if (fine is not null)
                foreach (double[] r in rejects)
                    rejDist = Math.Min(rejDist, QuestFind.SigDistance(fine, r));

            // Something she has already been burned by — nothing is clicked and nothing is risked.
            //
            // The rule is RELATIVE, and only relative. An absolute "close to a reject, therefore
            // wrong" was the dangerous half: a reject is captured through the same sliding window as
            // the live candidate, while the copy's reference came from the user's hand-dragged box,
            // so the reject starts with an unearned advantage — and one wrongly learned picture
            // would then veto real copies that match the reference almost perfectly, forever, with
            // no symptom but an empty-looking bag.
            //
            // The second clause is the floor under that: a candidate the COPY itself matches as well
            // as any confirmed merge has is a copy, whatever some stored picture thinks. That is
            // what makes a wrong reject recoverable rather than permanent — it gets clicked, it
            // merges, and the merge branch throws the bad reject away.
            double copyBand = confirmedFine >= 0 ? confirmedFine + RejectSafety : FineFloor;
            bool isKnownWrong = rejDist < double.MaxValue && fineDist >= 0
                && rejDist + RejectMargin < fineDist
                && fineDist > copyBand;
            if (isKnownWrong)
            {
                knownWrong.Add((h.X, h.Y));
                Log?.Invoke($"· skipped a look-alike at {h.X * 100:0.0}%, {h.Y * 100:0.0}% — it matches something "
                          + $"she learned isn't a copy (score {rejDist:0.0}"
                          + (fineDist >= 0 ? $" against {fineDist:0.0} for the real one" : "") + ").");
                continue;
            }

            if (fineDist >= 0 && fineDist > limit)
            {
                belowBar.Add((h.X, h.Y));
                Log?.Invoke($"· skipped {h.X * 100:0.0}%, {h.Y * 100:0.0}% — close on colour (coarse {h.Dist:0.0}) "
                          + $"but a different picture up close ({fineDist:0.0}, the bar is {limit:0.0}). "
                          + "Not a copy.");
                continue;
            }

            ActivityLog.Detail(MergeSource,
                $"chose {h.X * 100:0.0}%,{h.Y * 100:0.0}% — coarse {h.Dist:0.0}, "
                + (fineDist >= 0 ? $"fine {fineDist:0.0} (bar {limit:0.0})" : "fine not armed yet")
                + (rejDist < double.MaxValue ? $", nearest known non-copy {rejDist:0.0}" : ""));
            return new Look(true, new Candidate(h, fine, fineDist, rejDist), all.Count,
                            knownWrong, belowBar, h.Dist);
        }

        // Nothing acceptable left. Report the closest thing on screen anyway, so the caller can tell
        // "nothing here" (a number) from "couldn't look" (a null).
        QuestFind.IconHit? closest = QuestFind.FindIconInRect(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
                                                              _plan.IconSig!, _plan.IconW, _plan.IconH);
        return new Look(true, null, all.Count, knownWrong, belowBar, closest?.Dist ?? 999);
    }

    /// <summary>Persist what the sweep just learned, immediately rather than at the end. Every
    /// interesting way this run can end — the five-dead-ends stop, a lost cursor, F12 — is a way it
    /// can end without reaching a tidy save at the bottom, and the whole value of a learned
    /// look-alike is that it survives to the next run.</summary>
    private void SavePlan()
    {
        string? why = _plan.TrySave();
        // Speaking matters more here than the write did. A learned look-alike that didn't reach disk
        // will be learned again next run — annoying. A learned look-alike that SILENTLY didn't reach
        // disk looks exactly like one that did, and the same wrong item gets picked up every night
        // with the console insisting she has it handled.
        if (why is not null) Log?.Invoke("⚠ couldn't save what she learned (" + why + ") — she'll have to "
                                       + "learn it again next run.");
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
        for (int i = 0; i < 60; i++)                                    // ~24 s, then give up
        {
            // Cancellation is checked HERE rather than in the loop condition so that pressing Stop
            // never leaves through the give-up path below. Those are different events with different
            // advice — "you stopped me while I was holding something" is a note; "I tried for
            // twenty-four seconds and the game never came back" is a fault — and the loop condition
            // reported the second for both. Worse, ClickAt returns false the instant the token is
            // cancelled, so a Stop pressed a moment earlier produced that fault report having never
            // attempted the put-back at all.
            if (ct.IsCancellationRequested)
            {
                Log?.Invoke("⚠ Stopped while an item was on the cursor — put it back yourself before starting "
                          + "another sweep.");
                return;
            }
            if (_sink.Ready && ClickAt(sx, sy, 260, ct)) return;
            Stats.State = "holding an item — waiting for the game window";
            try { await Task.Delay(400, ct); }
            catch (OperationCanceledException)
            {
                Log?.Invoke("⚠ Stopped while an item was on the cursor — put it back yourself before starting "
                          + "another sweep.");
                return;
            }
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
        (int Have, int Need)? parsed = ParseTier(text);
        // The RAW text, before any repair or sanity test. "Tier 5 4/32" and "T1er S 4132" fail for
        // completely different reasons — one is a box drawn too wide, the other is OCR at the edge
        // of what it can do — and the parsed answer is null either way.
        ActivityLog.Detail(MergeSource,
            $"tier OCR read \"{(text ?? "").Replace("\r", " ").Replace("\n", " ").Trim()}\" → "
            + (parsed is { } t ? $"{t.Have}/{t.Need}" : "nothing usable"));
        return parsed;
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

            // What is ARMED, said out loud before anything is clicked — the same rule the Quest
            // Runner follows. A confirm step that is quietly not running is worse than none: you
            // would believe the look-alike problem was fixed while she went on picking up totems.
            if (_plan.ScanReady)
            {
                int rejects = _plan.Rejects.Count();
                Log?.Invoke(_plan.HasFineIcon
                    ? "· close-up confirm is armed — every square the colour search proposes is checked against the "
                      + "copy's picture at four times the detail before anything is picked up."
                      + (rejects > 0 ? $" She is also avoiding {rejects} icon(s) she has learned aren't copies." : "")
                    : "⚠ close-up confirm NOT armed — this plan predates it, so the colour search is on its own and a "
                      + "different item in the same palette can pass. She'll learn the close-up picture from the first "
                      + "merge the counter confirms; re-pick the copy's icon to arm it before that instead."
                      + (rejects > 0 ? $" ({rejects} learned non-copy icon(s) are still being avoided.)" : ""));
            }

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
            // Two counters, not one. "I couldn't see the bag" and "I looked and there was
            // nothing in it" are different failures with different advice, and sharing a
            // counter between them let three looks with two causes finish under one cause's
            // name — telling the user the game wasn't on screen when it had been all along.
            int passes = 0, blindLooks = 0, emptyLooks = 0, deadEnds = 0;
            // Squares that looked like a copy and did NOT move the counter. Without this the scan
            // re-finds the same square forever: the item you are merging INTO usually sits in the
            // same bag and carries the same icon, so once the real copies are gone the target
            // itself becomes the best match and gets picked up and put back until the guard trips.
            var tried = new List<(double X, double Y)>();
            // Squares the close-up wrote off under a bar that can still widen, and squares holding a
            // picture she has already learned about. The first list is CLEARED whenever a confirmed
            // merge raises the bar; the second never is, because a learned non-copy is settled.
            var belowBar = new List<(double X, double Y)>();
            var knownWrong = new List<(double X, double Y)>();
            var skip = new List<(double X, double Y)>();
            // The close-up signature of the square about to be clicked, taken BEFORE the click while
            // the icon is still in it. If the counter moves, it is a real copy's picture and worth
            // keeping; if it doesn't, it is a look-alike's and worth avoiding forever.
            double[]? pendingFine = null;
            // The worst close-up score a CONFIRMED merge has produced this run — the honest bar for
            // everything after it, measured on this screen at this resolution rather than guessed.
            double confirmedFine = -1;
            int learned = 0, vetoedTotal = 0;
            bool guardTripped = false;
            while (true)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (!await WaitFocus(ct)) { cancelled = true; break; }
                // A tripped guard is not a finished sweep, and must not end with the word "Done".
                if (++passes > 4000) { guardTripped = true; break; }

                double sx, sy;
                string where;
                if (grid is null)
                {
                    skip.Clear();
                    skip.AddRange(tried);
                    skip.AddRange(knownWrong);
                    skip.AddRange(belowBar);
                    Look look = LookForCopy(skip, confirmedFine);
                    knownWrong.AddRange(look.KnownWrong);
                    belowBar.AddRange(look.BelowBar);
                    if (look.Pick is null)
                    {
                        // ONE bad frame is not an empty bag. A tell window, a tooltip or a capture
                        // that throws all look exactly like "no copies left" — and the old grid walk
                        // was immune to this, because a bad frame cost it one slot instead of the
                        // night. So look again before believing it, and if the LOOK is what failed,
                        // never finish with the word "Done".
                        //
                        // A look that READ the screen and vetoed everything in it is NOT a bad frame
                        // — it is the answer. Making her look twice more at squares she has just
                        // proved aren't copies would only produce the same verdict slower.
                        if (!look.Read)
                        {
                            blindLooks++;
                            if (blindLooks < 3)
                            {
                                Log?.Invoke($"· couldn't read the bag this look ({blindLooks} of 3) — looking again.");
                                await Task.Delay(700, ct);
                                continue;
                            }
                            HumanizedMouse.MoveInstant(home.x, home.y);
                            Finish($"⚠ Stopped after {Stats.Merged} merge(s): couldn't read the bag area three "
                                 + "looks running. Is the game on screen and the bag still open?");
                            return;
                        }
                        if (look.Seen == 0)
                        {
                            // Nothing at all under the coarse bar. That is as easily a bag that got
                            // covered as a bag that got empty, so look again before believing it.
                            emptyLooks++;
                            if (emptyLooks < 3)
                            {
                                Log?.Invoke($"· nothing matched this look ({emptyLooks} of 3) — looking again.");
                                await Task.Delay(700, ct);
                                continue;
                            }
                        }
                        // Everything the colour search proposed was either something she has already
                        // tried or something the close-up says isn't a copy. That is an ANSWER, not a
                        // bad frame: looking twice more at squares she has just judged would produce
                        // the same verdict slower.
                        // Counted from the lists themselves. A running total added them up again
                        // every time a widened bar sent her back for a second look, so twenty
                        // look-alikes could be reported as eighty — and that number is precisely the
                        // one that tells you your icon pick wants another go.
                        int vetoed = knownWrong.Count + belowBar.Count;
                        Log?.Invoke(look.Seen == 0
                            ? "No copies in the bag area — nothing in it matched the copy's colours at all."
                            : vetoed > 0
                                ? $"No more copies in the bag area. {vetoed} square(s) were close on colour but a "
                                  + "different picture up close — left alone, not merged."
                                : tried.Count > 0
                                    ? $"No more copies in the bag area — the {tried.Count} square(s) still matching "
                                      + "are ones she already tried without the counter moving."
                                    : $"No more copies in the bag area — closest match was {look.ClosestDist:0}, "
                                      + $"and a real one scores under {QuestFind.SlidingAcceptDistance:0}.");
                        vetoedTotal = vetoed;
                        break;
                    }
                    blindLooks = 0;
                    emptyLooks = 0;
                    QuestFind.IconHit copy = look.Pick.Hit;
                    pendingFine = look.Pick.Fine;
                    sx = copy.X; sy = copy.Y;
                    where = $"copy at {copy.X * 100:0.0}%, {copy.Y * 100:0.0}% (match {copy.Dist:0}"
                          + (look.Pick.FineDist >= 0 ? $", close-up {look.Pick.FineDist:0}" : "") + ")";
                }
                else
                {
                    if (grid.Count == 0) break;
                    (int c, int r, double gx, double gy) = grid.Dequeue();
                    sx = gx; sy = gy;
                    pendingFine = null;
                    where = $"slot {r + 1},{c + 1}";
                }

                Stats.Attempts++;
                Stats.State = where;

                ActivityLog.Detail(MergeSource,
                    $"clicking {sx * 100:0.00}%,{sy * 100:0.00}% → Place Item {_plan.PlaceBox.X * 100:0.00}%,"
                    + $"{_plan.PlaceBox.Y * 100:0.00}% → Merge Item {_plan.MergeButton.X * 100:0.00}%,"
                    + $"{_plan.MergeButton.Y * 100:0.00}%");

                if (!ClickAt(sx, sy, 240, ct)) { await Task.Delay(400, ct); continue; }   // pick the copy up

                // The copy is now ON THE CURSOR. Every exit from here has to put it back in its own
                // slot first, and has to WAIT for focus to do it: the reason a click failed is that
                // EQ isn't foreground, and an immediate retry tests the same condition and does
                // exactly as little — while the copy rides the cursor into the next slot's click.
                if (!ClickAt(_plan.PlaceBox, 320, ct))
                {
                    await ReturnHeldAsync(sx, sy, ct);
                    if (Finished) { HumanizedMouse.MoveInstant(home.x, home.y); return; }
                    await Task.Delay(400, ct); continue;
                }
                if (!ClickAt(_plan.MergeButton, 420, ct))
                {
                    await ReturnHeldAsync(sx, sy, ct);
                    if (Finished) { HumanizedMouse.MoveInstant(home.x, home.y); return; }
                    await Task.Delay(400, ct); continue;
                }

                await Task.Delay(320, ct);
                (int Have, int Need)? now = await ReadTierAsync();

                if (now is null)
                {
                    blindMisses++;
                    Log?.Invoke($"⚠ {where}: couldn't read the tier counter ({blindMisses} of 3).");
                    await ReturnHeldAsync(sx, sy, ct);
                    if (Finished) { HumanizedMouse.MoveInstant(home.x, home.y); return; }
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

                    // The counter moved, so THAT was a real copy — which makes its close-up picture
                    // ground truth rather than a guess. Two uses:
                    //
                    //  · a plan made before the confirm existed gets its reference from here, so an
                    //    old plan is unprotected for exactly one merge instead of forever;
                    //  · the run learns what a real copy actually scores on this screen, and holds
                    //    later candidates to that rather than to a constant chosen in advance.
                    if (pendingFine is not null)
                    {
                        // It merged, so whatever she may have decided about this picture before was
                        // wrong. Un-learning here is what keeps a mistaken reject from being a life
                        // sentence: the only way to find out a blacklist entry was wrong is to click
                        // something and watch the counter move, and this is that moment.
                        if (_plan.ForgetReject(pendingFine))
                        {
                            SavePlan();
                            Log?.Invoke("· that picture had been learned as \"not a copy\" — it just merged, so "
                                      + "she's forgotten that. Sorry.");
                        }
                        if (!_plan.HasFineIcon)
                        {
                            _plan.IconSigFine = pendingFine;
                            SavePlan();
                            Log?.Invoke("· learned the copy's close-up picture from that merge — from here she can "
                                      + "tell it from a different item in the same colours.");
                        }
                        else
                        {
                            double d = QuestFind.SigDistance(pendingFine, _plan.IconSigFine!);
                            if (d > confirmedFine)
                            {
                                confirmedFine = d;
                                // The bar just moved outward, so every square it turned away was
                                // judged by a rule that no longer applies. Give them back — the
                                // alternative is a run that writes off the whole bag on the strength
                                // of how well the first copy happened to be aligned.
                                // BOTH lists, not just the close-up one. The look-alike veto has a
                                // safety clause of its own — it never fires over a square the copy
                                // matches as well as a confirmed merge does — and that clause is
                                // computed from this very number. A square turned away when there
                                // was no confirmed merge to measure against was judged by the
                                // widest, least informed version of the rule; leaving it on the
                                // list meant a stale reject learned in some earlier run could veto
                                // a real copy at the first look of every run thereafter, before
                                // anything had merged — and since only a merge can un-learn a bad
                                // reject, it would never get the chance to correct itself.
                                int back = belowBar.Count + knownWrong.Count;
                                if (back > 0)
                                {
                                    ActivityLog.Detail(MergeSource,
                                        $"bar widened — reconsidering {back} square(s) it had turned away");
                                    belowBar.Clear();
                                    knownWrong.Clear();
                                }
                            }
                            ActivityLog.Detail(MergeSource,
                                $"confirmed copy scored {d:0.0} close-up; the bar for later candidates is now "
                                + $"{Math.Clamp(confirmedFine + FineDrift, FineFloor, FineLimit):0.0}");
                        }
                    }
                    pendingFine = null;

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
                    // Remember the PICTURE, not just the square — a square is only good until the
                    // bags shuffle, while the picture of a Desecrated Kejaar Totem is good for the
                    // rest of the grind. Done BEFORE putting the item back, so that if the put-back
                    // is the thing that fails, its "an item may still be on the cursor" warning is
                    // the last word rather than being buried under a bookkeeping line.
                    //
                    // The guards matter more than the learning, because a wrongly learned reject is
                    // the one failure with no symptom: she would quietly refuse real copies and
                    // report an empty bag, in this run and every run after it.
                    //
                    //  · CONFIRMED FIRST. Nothing is learned until a merge in this run has proved
                    //    what a real copy actually scores. Without that number there is nothing to
                    //    measure "different enough" against.
                    //  · CLEAR OF THE COPY ITSELF. The item you are merging INTO sits in the same
                    //    bag wearing the same icon and can never merge into itself, so it fails here
                    //    every single time — and learning it would teach her to avoid the one thing
                    //    she is looking for.
                    //  · CLEAR OF THE CONFIRMED BAND. A counter that didn't move has an innocent
                    //    explanation that has nothing to do with the item: the merge worked and the
                    //    window had not repainted when it was read. Anything scoring inside the band
                    //    a confirmed copy scored is treated as that, not as a look-alike.
                    if (grid is null && pendingFine is not null && _plan.HasFineIcon)
                    {
                        double d = QuestFind.SigDistance(pendingFine, _plan.IconSigFine!);
                        double bar = Math.Max(RejectMinDistance, confirmedFine + RejectSafety);
                        if (confirmedFine < 0)
                            Log?.Invoke($"· not learning that picture yet (close-up {d:0.0}) — nothing has merged this "
                                      + "run, so there's no proof of what a real copy scores to measure it against.");
                        else if (d < bar)
                            Log?.Invoke($"· that square's icon is too close to the copy's own (close-up {d:0.0}, and a "
                                      + $"confirmed copy scored up to {confirmedFine:0.0}) — most likely the item "
                                      + "you're merging INTO, which can't merge into itself, or a read taken before "
                                      + "the window repainted. Skipping the square, NOT the picture.");
                        else
                        {
                            _plan.LearnReject(pendingFine);
                            learned++;
                            Log?.Invoke($"· learned that picture as \"not a copy\" (close-up {d:0.0} against "
                                      + $"{confirmedFine:0.0} for a confirmed copy) — she won't pick it up again, in "
                                      + "this run or any later one.");
                            SavePlan();
                        }
                    }
                    pendingFine = null;

                    await ReturnHeldAsync(sx, sy, ct);
                    // ReturnHeldAsync gives up by calling Finish. Anything said after that would be
                    // spoken by a run that has already announced it stopped — and the line it would
                    // bury is the one warning that an item is still on the cursor.
                    if (Finished) { HumanizedMouse.MoveInstant(home.x, home.y); return; }

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
            // The look-alikes get their own sentence rather than a footnote. "Done — 2 merged" and
            // "Done — 2 merged, and I left 14 things alone that were nearly the same colour" are
            // different reports about the same bag, and the second is the one that tells you the
            // icon pick wants a second look.
            vetoedTotal = Math.Max(vetoedTotal, knownWrong.Count + belowBar.Count);
            string avoided = vetoedTotal > 0
                ? $" She left {vetoedTotal} look-alike(s) alone"
                  + (learned > 0 ? $" and learned {learned} new one(s) to avoid from here on." : ".")
                : learned > 0 ? $" She learned {learned} icon(s) that aren't copies." : "";
            Finish((guardTripped
                ? $"⚠ Stopped after {Stats.Merged} merge(s){skipped}: 4,000 passes is not a bag, it is a loop. "
                  + $"Target is at {Stats.Tier}. Check the picks before running again."
                : cancelled
                    ? $"Stopped part-way — {Stats.Merged} merged{skipped}. Target is at {Stats.Tier}."
                    : $"Done — {Stats.Merged} merged{skipped}. Target is at {Stats.Tier}.") + avoided);
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
