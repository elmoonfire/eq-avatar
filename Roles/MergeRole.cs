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
using EQAvatar.Spike.Log;
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
    /// The copy's icon as its ACTUAL PIXELS, at the size the game draws them.
    ///
    /// Everything above this is a summary, and summaries were the whole problem. Measured on
    /// same-palette icons the 12×12 signature scores a DIFFERENT icon at 26 and the RIGHT icon,
    /// three pixels out of alignment, at 37 — the wrong item looks more like the reference than the
    /// right one does, so no threshold exists that keeps one and drops the other. That is not a
    /// number that needed tuning; it is a measure that cannot answer the question.
    ///
    /// Matched by normalized cross-correlation over a ±4 px alignment search, the same pair scores
    /// 0.98 for the real icon and 0.44 for the wrong one. Hayden asked for the squares to be
    /// compared with the pixels that are actually there. This is that.
    /// </summary>
    public QuestFind.IconPatch? IconPixels { get; set; }
    [JsonIgnore] public bool HasPixels => IconPixels is { Ok: true };

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
    /// projected tier is actually WORTH, not just which number it reaches — and, since the name
    /// gate, to decide whether a square holds the thing at all.</summary>
    public string ItemName { get; set; } = "";

    /// <summary>
    /// READ THE NAME before picking anything up.
    ///
    /// Hovering a bag slot makes the game draw the item's own tooltip, with its name at the top.
    /// That is the item telling us what it is, in words, and it is the only check on this page that
    /// is not a guess: a colour signature says "something roughly this shape and palette", and two
    /// field tests have now shown that a Desecrated Kejaar Totem answers to that description at
    /// both 6×6 and 12×12. At bag scale an icon is about 26 px across — a 12×12 grid over it is two
    /// pixels a cell — so there was never enough picture there to separate them, at any resolution.
    ///
    /// The icon scan still PROPOSES squares, because it is cheap and it narrows a five-rucksack
    /// bag to a handful of candidates. The name DECIDES.
    /// </summary>
    /// Default OFF now that the pixels decide. It hovers and OCRs one square at a time — about a
    /// second each — which is fine as a last check on the two squares a precise scan proposes and
    /// hopeless as a way to search a bag with hundreds of slots in it.
    public bool ConfirmByName { get; set; }

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

    /// <summary>
    /// The game DOES say when a merge worked.
    ///
    /// "The game writes nothing to the log about merging" has been in this file since 0.10.4, and a
    /// screenshot of the chat window on 2026-08-14 shows otherwise:
    ///
    ///   You have successfully merged two items together to create a new item: Talisman of Kejaar Kerrath +5
    ///
    /// That is the server talking, it names the result, and it cannot be misread the way a three-
    /// digit OCR of a progress counter can. The tier counter stays — it is what the forecast is
    /// drawn from, and it still catches the case where logging is off — but the log line is now the
    /// first witness asked.
    /// </summary>
    private static readonly Regex MergedRx = new(
        @"successfully merged two items together to create a new item:\s*(?<item>.+?)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly EqLogWatcher? _watcher;
    private readonly string _watcherPath = "";
    private volatile bool _watcherLive;
    private volatile string _watcherStartError = "";
    /// <summary>Bumped by the log watcher on its own thread every time the server confirms a merge,
    /// and the name it confirmed. Read by the sweep around each attempt.</summary>
    private int _mergedSeen;
    private string _mergedName = "";

    public MergeRole(MergePlan plan, IInputSink sink, Func<IntPtr> gameWindow, string? logPath = null)
    {
        _plan = plan;
        _sink = sink;
        _hwnd = gameWindow;
        _watcherPath = logPath ?? "";
        if (!string.IsNullOrEmpty(logPath)) _watcher = new EqLogWatcher(logPath!);
    }

    private void OnLogLine(string line)
    {
        Match m = MergedRx.Match(line ?? "");
        if (!m.Success) return;
        _mergedName = m.Groups["item"].Value.Trim();
        Interlocked.Increment(ref _mergedSeen);
    }

    public void Start()
    {
        if (Running || Volatile.Read(ref _finished) != 0) return;
        _cts = new CancellationTokenSource();
        Volatile.Write(ref _alive, 1);
        if (_watcher is not null)
        {
            // fromStart: false — only what the server says from here on. Replaying the file would
            // count last night's merges as this run's.
            _watcher.LineRead += OnLogLine;
            // File.Exists first: EqLogWatcher.Start swallows its own failures, so "it didn't throw"
            // is not evidence that anything is being read. Announcing a witness that isn't there is
            // worse than announcing none.
            try { _watcher.Start(fromStart: false); _watcherLive = System.IO.File.Exists(_watcherPath); }
            // Swallowing this silently meant the run announced it was watching the log while the
            // only witness left was the OCR it was supposed to back up.
            catch (Exception ex) { _watcherStartError = ex.Message; }
        }
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>The run has already announced why it ended. Nothing may speak or act after this.</summary>
    private bool Finished => Volatile.Read(ref _finished) != 0;

    private void Finish(string why)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        Volatile.Write(ref _alive, 0);
        if (_watcher is not null)
        {
            _watcher.LineRead -= OnLogLine;
            try { _watcher.Dispose(); } catch { }
        }
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
    /// <summary>
    /// How alike the pixels have to be. 1.0 is identical.
    ///
    /// Measured on same-palette icons: a real copy three pixels out of alignment and 20% brighter
    /// still scores 0.979, and the closest different icon scores 0.437. The bar sits in the middle
    /// of a gap half the scale wide, which is what a threshold is supposed to look like — every
    /// number this file had before was chosen inside the noise.
    /// </summary>
    private const double NccAccept = 0.85;

    /// <summary>What the colour pass hands to the pixel test. Deliberately loose: a false candidate
    /// costs a fraction of a millisecond to reject and a missed one costs a copy.</summary>
    private const double CoarsePropose = 60;

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
    private sealed record Candidate(QuestFind.IconHit Hit, double[]? Fine, double FineDist, double RejectDist,
                                   double Ncc = -1);

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
                               double ClosestDist,
                               int Accepted = 0);

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
    /// <summary>Positions already narrated as "not a copy", so re-judging them on every pass — which
    /// is what keeps one bad frame from being a life sentence — doesn't re-narrate them every pass.</summary>
    private readonly HashSet<(int, int)> _saidBelow = new();

    private Look LookForCopy(IReadOnlyCollection<(double X, double Y)> skip, double confirmedFine)
    {
        var knownWrong = new List<(double X, double Y)>();
        var belowBar = new List<(double X, double Y)>();
        if (!_plan.ScanReady) return new Look(false, null, 0, knownWrong, belowBar, 999);
        using System.Drawing.Bitmap? frame = QuestFind.Capture(_hwnd());
        if (frame is null) return new Look(false, null, 0, knownWrong, belowBar, 999);

        // A PROPOSER, not a judge. With the pixels deciding, the colour pass should hand over
        // everything that could plausibly be the item and let the real test throw them out — the
        // field log showed real copies scoring 25–29 against a bar of 35, which is close enough to
        // the edge that a slightly different slot background would have silently dropped them
        // before anything ever looked at them properly.
        // Pixel-armed plans propose with ALMOST NO deduplication (ProposeIcons), because the greedy
        // overlap dedupe eats real copies whenever two identical icons sit in adjacent slots: the
        // window straddling the pair orders ahead of both real centres, suppresses them, then fails
        // the pixel test itself. The sweep saw it as "nothing left" with copies still in the bag —
        // the same fault that made the counter read 3 where 19 sat.
        double proposeAt = _plan.HasPixels ? CoarsePropose : QuestFind.SlidingAcceptDistance;
        List<QuestFind.IconHit> all = _plan.HasPixels
            ? QuestFind.ProposeIcons(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
                _plan.IconSig!, _plan.IconW, _plan.IconH, proposeAt)
            : QuestFind.FindAllIcons(frame, _plan.BagX, _plan.BagY, _plan.BagW, _plan.BagH,
                _plan.IconSig!, _plan.IconW, _plan.IconH, proposeAt);
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
        int accepted = 0;
        foreach (QuestFind.IconHit h in all)
        {
            if (skip.Any(k => Math.Abs(k.X - h.X) < _plan.IconW * 0.5 && Math.Abs(k.Y - h.Y) < _plan.IconH * 0.5))
                continue;

            // THE PIXELS DECIDE. When the plan has them, the signature machinery below is skipped
            // entirely — it exists to answer a question this answers better.
            if (_plan.HasPixels)
            {
                (double best, int ddx, int ddy) = QuestFind.BestNcc(frame, h.X, h.Y, _plan.IconPixels!,
                    (int)Math.Round(_plan.IconW * frame.Width), (int)Math.Round(_plan.IconH * frame.Height));
                ActivityLog.Detail(MergeSource,
                    $"pixels at {h.X * 100:0.0}%,{h.Y * 100:0.0}% → {best:0.000} (coarse {h.Dist:0.0}, "
                    + $"aligned {ddx:+#;-#;0},{ddy:+#;-#;0})");
                if (best < NccAccept)
                {
                    // NOT remembered. A correlation is a judgement about ONE FRAME, and it is far
                    // more fragile than a colour average: a tooltip edge, the cursor, a highlight
                    // border or a merge animation across the slot all destroy it while leaving the
                    // coarse distance well inside the loose proposal bar. Writing the square off for
                    // the whole run on that basis would lose real copies to a moment of bad luck, so
                    // each pass judges afresh and the only cost of being wrong is a millisecond.
                    ActivityLog.Detail(MergeSource,
                        $"below the bar at {h.X * 100:0.0}%,{h.Y * 100:0.0}%: {best:0.000}");
                    // Quantised to icon-sized cells, not raw position. The raw proposals now come in
                    // four to nine per icon, and keying the "said it already" set on each of them
                    // would narrate the same look-alike totem half a dozen times per bag.
                    if (_saidBelow.Add(((int)(h.X / Math.Max(0.002, _plan.IconW)),
                                        (int)(h.Y / Math.Max(0.002, _plan.IconH)))))
                        Log?.Invoke(best < 0
                            ? $"· couldn't read the pixels at {h.X * 100:0.0}%, {h.Y * 100:0.0}% — too close to the "
                              + "edge of the window to compare."
                            : $"· not a copy at {h.X * 100:0.0}%, {h.Y * 100:0.0}% — the pixels match "
                              + $"{best * 100:0.0}%, and a real one matches over {NccAccept * 100:0}%.");
                    continue;
                }
                accepted++;
                // The candidate carries the ALIGNED centre, not the proposal grid's guess. The grab
                // clicks it and the skip list remembers it, so a proposal up to a sixth of an icon
                // out can neither make the click clip a neighbouring slot nor leave a skip entry
                // that a later pass's slightly different proposal fails to match.
                var snapped = new QuestFind.IconHit(h.X + (double)ddx / frame.Width,
                                                   h.Y + (double)ddy / frame.Height, -1, -1, h.Dist);
                Log?.Invoke($"· {snapped.X * 100:0.0}%, {snapped.Y * 100:0.0}% matches the copy's pixels "
                          + $"{best * 100:0.0}%.");
                return new Look(true, new Candidate(snapped, null, -1, double.MaxValue, best), all.Count,
                                knownWrong, belowBar, h.Dist, accepted);
            }

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

    // ---------------------------------------------------------------- reading the name

    /// <summary>How far (normalized) a name may sit from the square being hovered and still be
    /// THAT square's tooltip. The chat log echoes "Talisman of Kejaar Kerrath +5" every time a
    /// merge succeeds, so a name found anywhere on screen is not evidence — a name found beside the
    /// cursor is. Same argument as the NPC nameplate's drift limit.</summary>
    /// A RADIUS, not a rectangle, and tighter than the nameplate's 0.22 — the tooltip is drawn
    /// touching the square, so anything further away is something else on screen.
    private const double NameMaxDist = 0.22;

    /// <summary>
    /// How far around the target item's own window to ignore text.
    ///
    /// That window has to stay open for the whole run — the tier counter is read off it and the
    /// Place/Merge picks are on it — and it prints "Talisman of Kejaar Kerrath +5", which strips to
    /// exactly the same words as a copy's tooltip. Without this the gate would pass over ANY square
    /// whose neighbourhood happened to include it, which is the opposite of a check.
    /// </summary>
    private const double TargetWindowPad = 0.10;

    /// <summary>Squares in a row that named themselves something else before the run gives up.
    /// Generous, because a bag really can hold twenty-five look-alikes — but not unbounded, because
    /// the same number is what a broken gate produces.</summary>
    private const int NameMissLimit = 25;

    private const string StaleTier = " (last read)";

    /// <summary>Milliseconds to let the tooltip appear after the cursor lands.</summary>
    private const int HoverSettleMs = 360;

    /// <summary>What one hover saw: whether it matched, how far the nearest text sat, and
    /// everything readable nearby — the last of which is the whole point when it goes wrong.</summary>
    /// <param name="Read">FALSE means the look never happened — no window, the capture threw,
    /// the game wasn't in front. That is NOT the same as "this isn't the item", and the caller must
    /// not ban a square for it: a frame where the tooltip didn't render is not the item telling you
    /// anything.</param>
    public sealed record NameLook(bool Read, bool Matched, string Blob, double Dist, string Nearby,
                                  int Tokens, int Hits, string Why = "");

    /// <summary>
    /// Hover a square and read the tooltip the game draws for it.
    ///
    /// This is the check the icon signature could never be. A colour signature says "something
    /// roughly this shape and palette", and two field tests have shown a Desecrated Kejaar Totem
    /// answers to that at 6×6 AND at 12×12 — at bag scale an icon is about 26 px across, so a 12×12
    /// grid over it is two pixels a cell and there was never enough picture there to separate them.
    /// The tooltip is the item saying what it is, in words.
    ///
    /// The page's "Test the name read" button calls THIS, not a lookalike: a test that exercises
    /// different code than the run proves nothing.
    /// </summary>
    /// <param name="hover">False = read the same neighbourhood WITHOUT moving the cursor onto it.
    /// That is how the run tells a tooltip from furniture: whatever is still readable when the
    /// cursor is elsewhere was never the item talking.</param>
    public async Task<NameLook> ReadNameAtAsync(double nx, double ny, string want,
                                                CancellationToken ct = default, bool hover = true)
    {
        if (Screen(nx, ny) is not (int sx, int sy))
            return new NameLook(false, false, "", 999, "", 0, 0, "no game window");
        string[] tokens = NameTokens(want);
        if (tokens.Length == 0)
            return new NameLook(false, false, "", 999, "", 0, 0, "no item name to look for");
        // The same rule the copy count and the test button follow: a screen read taken while this
        // app is over the game photographs the app, finds nothing, and blames the user's picks.
        if (!_sink.Ready)
            return new NameLook(false, false, "", 999, "", tokens.Length, 0, "EverQuest wasn't the front window");

        // TWO hops, not one. A cursor that teleports onto a slot may never generate the movement the
        // game watches for, and a tooltip that never appears is indistinguishable from an item that
        // isn't there — which would make this gate reject the whole bag. Landing nearby and then
        // stepping on is what a hand does.
        try
        {
            if (hover)
            {
                HumanizedMouse.MoveInstant(sx + 14, sy + 10);
                await Task.Delay(70, ct);
                HumanizedMouse.MoveInstant(sx, sy);
            }
            await Task.Delay(HoverSettleMs, ct);
        }
        catch { return new NameLook(false, false, "", 999, "", tokens.Length, 0, "cancelled"); }

        List<FoundText> found;
        try { found = await ScreenText.ReadAsync(_hwnd()); }
        catch (Exception ex) { return new NameLook(false, false, "", 999, "", tokens.Length, 0, "the screen read threw: " + ex.Message); }
        if (QuestFind.WindowRect(_hwnd()) is not (double wx, double wy, double ww, double wh))
            return new NameLook(false, false, "", 999, "", tokens.Length, 0, "no game window");

        // Everything close enough to be this square's tooltip, joined into one string. Whole-window
        // OCR returns fragments and a name can be split across two of them, so the words are matched
        // against the joined neighbourhood rather than against each fragment on its own.
        var near = new List<(double D, string T)>();
        foreach (FoundText f in found)
        {
            double fx = (f.X - wx) / ww, fy = (f.Y - wy) / wh;
            double dx = fx - nx, dy = fy - ny;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > NameMaxDist) continue;
            if (InTargetWindow(fx, fy)) continue;      // the item we're merging INTO prints the same words
            near.Add((d, f.Text));
        }
        near.Sort((a, b) => a.D.CompareTo(b.D));
        string blob = string.Join(" ", near.Select(n => n.T));
        return new NameLook(true, MatchesName(blob, want), blob.Length > 200 ? blob[..200] : blob,
                            near.Count > 0 ? near[0].D : 999,
                            near.Count == 0 ? "(nothing readable beside that square)"
                                            : string.Join(" | ", near.Take(14).Select(n => n.T)),
                            tokens.Length, CountTokenHits(blob, tokens));
    }

    /// <summary>Is this point inside (or just outside) the target item's own window? Derived from
    /// the picks that are already on it, so it needs nothing new from the user.</summary>
    private bool InTargetWindow(double fx, double fy)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (_plan.PlaceBox.Set) { xs.Add(_plan.PlaceBox.X); ys.Add(_plan.PlaceBox.Y); }
        if (_plan.MergeButton.Set) { xs.Add(_plan.MergeButton.X); ys.Add(_plan.MergeButton.Y); }
        if (_plan.TierSet) { xs.Add(_plan.TierX); xs.Add(_plan.TierX + _plan.TierW); ys.Add(_plan.TierY); ys.Add(_plan.TierY + _plan.TierH); }
        if (xs.Count == 0) return false;
        return fx >= xs.Min() - TargetWindowPad && fx <= xs.Max() + TargetWindowPad
            && fy >= ys.Min() - TargetWindowPad && fy <= ys.Max() + TargetWindowPad;
    }

    /// <summary>
    /// The words worth matching in an item name: four letters or more, and never the upgrade
    /// suffix. A bag copy is "Talisman of Kejaar Kerrath"; the one you are merging INTO is
    /// "Talisman of Kejaar Kerrath +5". Matching on the "+5" would reject every copy in the bag.
    /// </summary>
    public static string[] NameTokens(string name)
    {
        var outp = new List<string>();
        foreach (string raw in (name ?? "").Split(' ', '`', '\'', '-', '.', ',', '(', ')'))
        {
            string t = new(raw.Where(char.IsLetter).ToArray());
            if (t.Length >= 4) outp.Add(t.ToLowerInvariant());
        }
        return outp.ToArray();
    }

    /// <summary>
    /// Does this blob of OCR name the item?
    ///
    /// Every long word has to be there, near enough. NOT "contains the name" — at this size OCR
    /// turns "Kerrath" into "Kerralh" often enough that an exact test would refuse real copies all
    /// night. And NOT "contains any word", because "Desecrated Kejaar Totem" and "Talisman of
    /// Kejaar Kerrath" share "Kejaar", and matching on one shared word is exactly the mistake the
    /// colour signature was already making, spelled differently.
    /// </summary>
    public static bool MatchesName(string ocr, string want)
    {
        string[] tokens = NameTokens(want);
        if (tokens.Length == 0) return false;
        // A clear majority of the words, and nothing required outright.
        //
        // Requiring the LONGEST word was the obvious rule and the wrong one: the longest word has
        // the most characters and therefore the most exposure to OCR damage, so it made the single
        // most fragile token into a veto over the whole run. A majority is what actually separates
        // the two items here — "Desecrated Kejaar Totem" shares exactly one word with "Talisman of
        // Kejaar Kerrath", which is 1 of 3 and nowhere near two thirds.
        // Two thirds is safe when there is a LONG word carrying the identity, and dangerous when
        // there isn't. "Fire Opal Ring" is three four-letter words, each with an edit budget of its
        // own, and two of them are common enough that an unrelated "Rune of Fire Ring" clears the
        // bar. So: with a distinctive word present, a majority (and that word has to be one of the
        // hits); without one, every word has to be there.
        string[] longOnes = tokens.Where(t => t.Length >= 6).ToArray();
        string hay = Flatten(ocr);
        int hits = tokens.Count(t => TokenPresent(hay, t));
        if (longOnes.Length == 0) return hits == tokens.Length;
        if (!longOnes.Any(t => TokenPresent(hay, t))) return false;
        return hits >= (tokens.Length * 2 + 2) / 3;             // ceil(2/3)
    }

    private static int CountTokenHits(string ocr, string[] tokens)
    {
        string hay = Flatten(ocr);
        return tokens.Count(t => TokenPresent(hay, t));
    }

    private static string Trim(string? s, int n) => (s ?? "").Length <= n ? (s ?? "") : s![..n] + "…";

    /// <summary>
    /// Letters only, lower case, spaces removed.
    ///
    /// The spaces have to go. Whole-window OCR returns fragments and a name can be split across
    /// two of them, so "Talisman" comes back as "Talis" + "man" — joined with a space that is
    /// "talis man", which is two edits from "talisman" before OCR has made a single mistake of its
    /// own. Flattened it is exact.
    /// </summary>
    private static string Flatten(string s)
        => new((s ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());

    /// <summary>
    /// Is this word somewhere in that text, allowing for OCR damage?
    ///
    /// Approximate substring match by edit distance, so a DROPPED or ADDED character costs one, the
    /// same as a wrong one. The previous version compared fixed-width windows character by
    /// character, which meant "Kerrath" read as "Kerath" — one missing letter — scored a mismatch
    /// at every position after it and failed outright. Insertions and deletions are the OCR errors
    /// that actually happen at this size.
    /// </summary>
    private static bool TokenPresent(string hay, string token)
    {
        if (token.Length == 0 || hay.Length == 0) return false;
        if (hay.Contains(token)) return true;
        int budget = token.Length >= 7 ? 2 : 1;

        // Standard approximate-substring DP: row 0 is all zeros, so the pattern may begin anywhere
        // in the text for free, and the answer is the smallest value in the final row.
        var prev = new int[hay.Length + 1];
        var cur = new int[hay.Length + 1];
        for (int i = 1; i <= token.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= hay.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                                  prev[j - 1] + (token[i - 1] == hay[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        for (int j = 0; j <= hay.Length; j++) if (prev[j] <= budget) return true;
        return false;
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
    public static int CountCopies(IntPtr hwnd, MergePlan plan) => ScanCopies(hwnd, plan)?.Count ?? -1;

    /// <summary>
    /// Every copy in the bag area with the score that says so — the same two passes the sweep uses,
    /// so the number on the page and the number the sweep acts on can never disagree.
    /// </summary>
    /// <returns>NULL when the screen could not be read at all — which the caller must NOT render as
    /// "0 copies" in green. An empty bag and a bag nobody could see are different answers, and the
    /// count button's whole job is to tell you which one you have.</returns>
    public static List<(double X, double Y, double Score)>? ScanCopies(IntPtr hwnd, MergePlan plan)
    {
        var outp = new List<(double, double, double)>();
        if (!plan.ScanReady) return null;

        if (!plan.HasPixels)
        {
            using System.Drawing.Bitmap? f = QuestFind.Capture(hwnd);
            if (f is null) return null;
            // Colour-only: the coarse score IS the judge, so the overlap dedupe inside FindAllIcons
            // is the right one and the count is whatever it says.
            foreach (QuestFind.IconHit h in QuestFind.FindAllIcons(f, plan.BagX, plan.BagY, plan.BagW,
                         plan.BagH, plan.IconSig!, plan.IconW, plan.IconH, QuestFind.SlidingAcceptDistance))
                outp.Add((h.X, h.Y, h.Dist));
            return outp;
        }

        // TWO frames, a beat apart, and a copy counts if EITHER saw it. One frame is a single
        // moment of a screen that is never still: spell sparkles, the levitate shimmer and item
        // flashes drift across the bags, and whichever icons they happened to cover that instant
        // fail the pixel test — which is how the same bag counted differently every press. The
        // union is safe in a way it wouldn't be for a looser metric, because a false POSITIVE
        // needs a slot to correlate ≥85% with the exact reference, and sparkle glare only ever
        // destroys a match, never manufactures one. Read failure stays distinct: null means
        // NEITHER frame could be read, not "0 copies".
        var found = new List<QuestFind.CopyHit>();
        bool readAny = false;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass > 0) Thread.Sleep(280);
            using System.Drawing.Bitmap? frame = QuestFind.Capture(hwnd);
            if (frame is null) continue;
            readAny = true;
            // The SAME arguments the sweep uses, resize included. A count that skipped the rescale
            // would disagree with the run the moment the window changed size — and the count is
            // what the forecast, and the user's decision to press Run, are both built on.
            foreach (QuestFind.CopyHit c in QuestFind.FindAllCopies(frame, plan.BagX, plan.BagY,
                         plan.BagW, plan.BagH, plan.IconSig!, plan.IconW, plan.IconH, CoarsePropose,
                         plan.IconPixels!,
                         (int)Math.Round(plan.IconW * frame.Width),
                         (int)Math.Round(plan.IconH * frame.Height), NccAccept))
                if (!found.Any(k => Math.Abs(k.X - c.X) < plan.IconW * 0.5
                                 && Math.Abs(k.Y - c.Y) < plan.IconH * 0.5))
                    found.Add(c);
        }
        if (!readAny) return null;
        foreach (QuestFind.CopyHit c in found) outp.Add((c.X, c.Y, c.Ncc));
        outp.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return outp;
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
            // The name gate, said out loud. It is the difference between "she checks what she picks
            // up" and "she doesn't", and a run that quietly wasn't checking is how both field tests
            // ended.
            string wantName = (_plan.ItemName ?? "").Trim();
            if (_plan.ConfirmByName && wantName.Length > 0 && NameTokens(wantName).Length == 0)
            {
                Finish($"Can't start — \"{wantName}\" has no word of four letters or more to match on, so the name "
                     + "gate has nothing to look for. Type the item's full name as the game spells it, or turn the "
                     + "name check off (and know that the icon alone has twice picked up the wrong item).");
                return;
            }
            if (_plan.ConfirmByName && wantName.Length > 0)
                Log?.Invoke($"· name gate ARMED — every square that passes the pixel test gets hovered and has to "
                          + $"read back as \"{wantName}\" before anything is picked up. "
                          + "Words: " + string.Join(", ", NameTokens(wantName)) + ".");
            else
                Log?.Invoke(_plan.ConfirmByName
                    ? "⚠ name gate OFF — no item name is set, so she is going on the icon alone. Type the item's "
                      + "name on this page: it is the only check here that isn't a guess."
                    : "⚠ name gate turned off — she is going on the icon alone, which has twice now been fooled "
                      + "by a Desecrated Kejaar Totem.");

            if (_watcherLive)
                Log?.Invoke("· watching the log for the server's own \"successfully merged\" line — that, not the "
                          + "counter, is the first thing asked after each attempt, and the name in it has to match.");
            else if (!string.IsNullOrEmpty(_watcherPath))
                Log?.Invoke($"⚠ the log file it was told to watch isn't there ({_watcherPath}) — a merge can only "
                          + "be confirmed by OCR of the tier counter this run.");
            else if (_watcherStartError.Length > 0)
                Log?.Invoke($"⚠ couldn't start watching the log ({_watcherStartError}) — a merge can only be "
                          + "confirmed by OCR of the tier counter this run.");
            else
                Log?.Invoke("⚠ no log file to watch, so a merge can only be confirmed by OCR of the tier counter. "
                          + "Set the log folder on Tab 1 and press Ensure Log=1.");

            // If the item window sits on top of the bags, the exclusion that keeps its label out of
            // the gate also throws away the tooltips the gate depends on. That produces a run that
            // refuses every square and blames the spelling, so it gets said out loud.
            if (_plan.ConfirmByName && wantName.Length > 0 && _plan.BagSet
                && (InTargetWindow(_plan.BagX + _plan.BagW / 2, _plan.BagY + _plan.BagH / 2)
                    || InTargetWindow(_plan.BagX, _plan.BagY)))
                Log?.Invoke("⚠ the target item's window overlaps the bag area. Text there is ignored so the item's "
                          + "own \"+5\" label can't pass the name check for it — which means tooltips over that "
                          + "part of the bag are ignored too. Drag the item window clear of the bags.");

            // Measured against the CURRENT window, not the one the pick was made in. Reporting the
            // learned size's radius while the sweep uses the live size's would be the run describing
            // a search it isn't performing.
            int liveW = _plan.HasPixels ? (int)Math.Round(_plan.IconW * (QuestFind.WindowRect(_hwnd())?.W ?? 0)) : 0;
            int liveH = _plan.HasPixels ? (int)Math.Round(_plan.IconH * (QuestFind.WindowRect(_hwnd())?.H ?? 0)) : 0;
            if (liveW < 4 || liveH < 4) { liveW = _plan.IconPixels?.W ?? 0; liveH = _plan.IconPixels?.H ?? 0; }

            if (_plan.HasPixels && QuestFind.SearchPadWanted(Math.Max(liveW, liveH)) > QuestFind.SearchPadCap)
                Log?.Invoke($"⚠ that icon is {liveW}×{liveH} px on screen, which needs a wider alignment search than "
                          + $"the {QuestFind.SearchPadCap} px cap allows — some copies may never get lined up and "
                          + "will read as \"not a copy\". Re-pick the icon with a TIGHTER box.");

            if (_plan.HasPixels)
                Log?.Invoke($"· matching the copy's ACTUAL PIXELS ({_plan.IconPixels!.W}×{_plan.IconPixels.H} as "
                          + $"learned, {liveW}×{liveH} on screen now), nudged ±{QuestFind.SearchPadFor(liveW)},"
                          + $"±{QuestFind.SearchPadFor(liveH)} px to find the best fit — wide enough to cover the "
                          + "gap between the squares the colour pass proposes, which is what decides whether a copy "
                          + "ever gets lined up at all. A square has to match over "
                          + $"{NccAccept * 100:0}% to be touched; a different icon in the same colours scores "
                          + "around 45%.");
            else if (_plan.ScanReady)
                Log?.Invoke("⚠ this plan has no pixel copy of the icon, so she is falling back to the colour "
                          + "signatures — which cannot separate two icons drawn from one palette, and twice "
                          + "haven't. Re-pick the copy's icon once to fix that.");

            if (_plan.ScanReady && !_plan.HasPixels)
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
            int learned = 0, vetoedTotal = 0, nameMisses = 0, readFails = 0;
            bool blankRetried = false, gateRefused = false;
            bool guardTripped = false;
            // Squares the item's own tooltip said were something else. Permanent for the run: a name
            // is not a threshold that can widen later, it is the item telling you what it is.
            var namedOut = new List<(double X, double Y)>();
            while (true)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (!await WaitFocus(ct)) { cancelled = true; break; }
                // A tripped guard is not a finished sweep, and must not end with the word "Done".
                if (++passes > 4000) { guardTripped = true; break; }

                double sx, sy;
                string where;
                bool nameConfirmed = false;
                if (grid is null)
                {
                    skip.Clear();
                    skip.AddRange(tried);
                    skip.AddRange(knownWrong);
                    skip.AddRange(belowBar);
                    skip.AddRange(namedOut);
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
                        // Coarse hits are PROPOSALS, and the proposal bar is deliberately loose, so
                        // an almost-empty bag still produces plenty of them. What actually means
                        // "nothing here" is that nothing PASSED — and that is the count that deserves
                        // a second and third look before the run believes the bag is empty.
                        int nothingHere = _plan.HasPixels ? look.Accepted : look.Seen;
                        if (nothingHere == 0)
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
                        if (namedOut.Count > 0)
                        {
                            Log?.Invoke($"No more copies in the bag area. {namedOut.Count} square(s) looked right "
                                      + $"but named themselves something other than {wantName} — left alone, "
                                      + "never picked up.");
                            vetoedTotal = vetoed;
                            gateRefused = Stats.Merged == 0;
                            break;
                        }
                        Log?.Invoke(look.Seen == 0
                            ? "No copies in the bag area — nothing in it matched the copy's colours at all."
                            : vetoed > 0
                                ? $"No more copies in the bag area. {vetoed} square(s) were close on colour but a "
                                  + "different picture up close — left alone, not merged."
                                : tried.Count > 0
                                    ? $"No more copies in the bag area — the {tried.Count} square(s) still matching "
                                      + "are ones she already tried without the counter moving."
                                    : _plan.HasPixels
                                        ? "No more copies in the bag area — nothing in it matched the copy's "
                                          + $"pixels closely enough (the bar is {NccAccept * 100:0}%)."
                                        : $"No more copies in the bag area — closest match was {look.ClosestDist:0}"
                                          + $", and a real one scores under {QuestFind.SlidingAcceptDistance:0}.");
                        vetoedTotal = vetoed;
                        break;
                    }
                    blindLooks = 0;
                    emptyLooks = 0;
                    QuestFind.IconHit copy = look.Pick.Hit;
                    pendingFine = look.Pick.Fine;
                    sx = copy.X; sy = copy.Y;
                    where = look.Pick.Ncc >= 0
                        ? $"copy at {copy.X * 100:0.0}%, {copy.Y * 100:0.0}% (pixels {look.Pick.Ncc * 100:0.0}%)"
                        : $"copy at {copy.X * 100:0.0}%, {copy.Y * 100:0.0}% (match {copy.Dist:0}"
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

                // ---- THE NAME GATE. Nothing is picked up until the item has said what it is.
                //
                // Before this, a square that looked right was clicked, placed, merge-pressed, and
                // only THEN judged — by a counter that hadn't moved. That is a wrong item picked up,
                // carried across the screen, and put back, five times over, before the run gave up.
                // Reading the tooltip costs one hover and one screen read, and it happens while the
                // item is still sitting in its own square where it belongs.
                if (grid is null && _plan.ConfirmByName && wantName.Length > 0)
                {
                    // Reached only for squares that already passed the pixel test, so this is a last
                    // word on one or two squares rather than a walk through the bag. As a SEARCH it
                    // was hopeless: a second a square, twenty-five squares, and hundreds to go.

                    Stats.State = "reading the name at " + $"{sx * 100:0.0}%, {sy * 100:0.0}%";
                    NameLook look2 = await ReadNameAtAsync(sx, sy, wantName, ct);
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    ActivityLog.Detail(MergeSource,
                        $"hover {sx * 100:0.0}%,{sy * 100:0.0}% → {look2.Hits}/{look2.Tokens} words, "
                        + $"nearest text {look2.Dist:0.00} away · {look2.Nearby}");

                    // A LOOK THAT DIDN'T HAPPEN IS NOT A VERDICT. The capture threw, the game slipped
                    // behind us, the window went away — none of that is the item saying what it is,
                    // and banning a square for it would quietly lose a real copy for the whole run.
                    if (!look2.Read)
                    {
                        readFails++;
                        Log?.Invoke($"⚠ couldn't read the name at {sx * 100:0.0}%, {sy * 100:0.0}% "
                                  + $"({readFails} of 3) — {look2.Why}.");
                        if (readFails >= 3)
                        {
                            HumanizedMouse.MoveInstant(home.x, home.y);
                            Finish($"⚠ Stopped after {Stats.Merged} merge(s): couldn't read any item name three "
                                 + $"times running ({look2.Why}). Nothing was picked up. Keep EverQuest in front "
                                 + "with the bags open, then press \"test the name read\" to see what she can see.");
                            return;
                        }
                        await Task.Delay(500, ct);
                        continue;
                    }
                    readFails = 0;

                    // NOTHING readable beside the square is not the same as "this is something
                    // else". The tooltip may simply not have drawn inside the settle time, and
                    // banning the square for that would quietly lose a real copy for the whole run.
                    // One more look, slower, before it is believed — the same three-strikes courtesy
                    // every other read on this page gets.
                    if (look2.Blob.Length == 0 && !blankRetried)
                    {
                        blankRetried = true;
                        ActivityLog.Detail(MergeSource,
                            $"no tooltip at {sx * 100:0.0}%,{sy * 100:0.0}% — hovering again, slower");
                        await Task.Delay(500, ct);
                        continue;
                    }
                    blankRetried = false;

                    if (!look2.Matched)
                    {
                        nameMisses++;
                        namedOut.Add((sx, sy));
                        Log?.Invoke(look2.Blob.Length == 0
                            ? $"· nothing readable beside {sx * 100:0.0}%, {sy * 100:0.0}% — no tooltip appeared "
                              + "there. Leaving it alone."
                            : $"· {sx * 100:0.0}%, {sy * 100:0.0}% isn't a {wantName} — the tooltip there reads "
                              + $"\"{Trim(look2.Blob, 80)}\". Not touching it.");
                        // Enough squares in a row naming themselves something else means the gate
                        // isn't working — the name is spelled differently in game, the bags are
                        // covered, the tooltip isn't appearing. That is a reason to stop and say so,
                        // not to hover an entire rucksack. NOT gated on having merged nothing: the
                        // gate can start failing after the first merge just as easily as before it,
                        // and that version of the guard would have been switched off for the rest of
                        // the night by a single success.
                        if (nameMisses >= NameMissLimit)
                        {
                            HumanizedMouse.MoveInstant(home.x, home.y);
                            Finish($"⚠ Stopped after {Stats.Merged} merge(s): {NameMissLimit} squares in a row "
                                 + $"didn't name themselves \"{wantName}\". Nothing wrong was picked up. Either the "
                                 + "name is spelled differently in game, the bags are covered, or the tooltip isn't "
                                 + "appearing — press \"test the name read\" to see exactly what she can read.");
                            return;
                        }
                        // Park the cursor away from the bags. Leaving it on the rejected square
                        // leaves that square's tooltip drawn over its neighbours, and the next scan
                        // then photographs a bag with a large opaque panel across it — which reads
                        // as "no more copies" while the copies are still sitting under it.
                        HumanizedMouse.MoveInstant(home.x, home.y);
                        await Task.Delay(180, ct);
                        continue;
                    }
                    // ---- AND IS IT ACTUALLY THE TOOLTIP?
                    //
                    // A name found beside a square is only evidence if it APPEARED because we
                    // hovered there. The chat window prints "…create a new item: Talisman of Kejaar
                    // Kerrath +5" after every success and never moves; so does the target item's own
                    // window. Either can sit inside the radius of a bag square, and from the first
                    // merge onward that would turn this gate into a rubber stamp — the worst
                    // possible outcome, because the run would report every square as confirmed.
                    //
                    // So: move away and look again. Text still there with the cursor elsewhere is
                    // furniture, not the item. Only paid on the squares we are about to act on.
                    HumanizedMouse.MoveInstant(home.x, home.y);
                    await Task.Delay(260, ct);
                    NameLook away = await ReadNameAtAsync(sx, sy, wantName, ct, hover: false);
                    if (away.Read && away.Matched)
                    {
                        namedOut.Add((sx, sy));
                        nameMisses++;
                        Log?.Invoke($"· ignoring {sx * 100:0.0}%, {sy * 100:0.0}% — that name is on screen whether "
                                  + "or not she hovers it, so it's the chat log or the item window, not a tooltip.");
                        ActivityLog.Detail(MergeSource, "static text near that square: " + away.Nearby);
                        continue;
                    }

                    nameMisses = 0;
                    nameConfirmed = true;
                    Log?.Invoke($"· {sx * 100:0.0}%, {sy * 100:0.0}% names itself {wantName} "
                              + $"({look2.Hits} of {look2.Tokens} words) — taking it.");
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
                // Snapshotted immediately before the commit, not before the pick-up. The gap between
                // them is a hover, a screen read and two clicks — seconds — and a confirmation that
                // arrived late from the PREVIOUS attempt would otherwise be credited to this one.
                int mergedBefore = Volatile.Read(ref _mergedSeen);

                if (!ClickAt(_plan.MergeButton, 420, ct))
                {
                    await ReturnHeldAsync(sx, sy, ct);
                    if (Finished) { HumanizedMouse.MoveInstant(home.x, home.y); return; }
                    await Task.Delay(400, ct); continue;
                }

                await Task.Delay(320, ct);
                // THE SERVER FIRST. A chat line saying the merge succeeded is testimony; a counter
                // read off the screen is an inference from three OCR'd digits. Where they disagree,
                // the words win — and where the counter can't be read at all, the words are still
                // there, which is the difference between a run that stops after three bad reads and
                // a run that keeps working.
                bool loggedMerge = Volatile.Read(ref _mergedSeen) != mergedBefore;
                if (loggedMerge)
                {
                    // The line NAMES the result, so check it. A merge the player did by hand in
                    // another window, or a merge of some other item, would otherwise confirm
                    // whatever attempt happened to be in flight — with all the consequences of a
                    // false confirmation, which are the worst ones on this page.
                    string got = _mergedName;
                    if (wantName.Length > 0 && !MatchesName(got, wantName))
                    {
                        loggedMerge = false;
                        Log?.Invoke($"· the server confirmed a merge of \"{got}\", which isn't a {wantName} — "
                                  + "not counting it as ours. Falling back to the tier counter.");
                    }
                    else ActivityLog.Detail(MergeSource, $"server confirmed the merge: \"{got}\"");
                }

                (int Have, int Need)? now = await ReadTierAsync();

                if (now is null && !loggedMerge)
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
                if (now is { } t) Stats.Tier = $"{t.Have}/{t.Need}";
                else if (Stats.Tier.Length > 0 && !Stats.Tier.EndsWith(StaleTier)) Stats.Tier += StaleTier;

                // The score is the honest witness. The displayed numerator FALLS on a level-up
                // (518 shows as 6/512 the moment it passes 512), so "did the numerator change"
                // needed two special cases and still couldn't tell a rise from a fall. A score can
                // only go up, and by exactly what was fed in — which is also worth printing, since
                // a jump of 32 says you just merged a +5 rather than a fresh drop.
                //
                // `now` can be NULL here now, and only in one situation: the server said the merge
                // happened and the counter didn't read. That is a merge with no number attached —
                // counted, narrated, and left out of the arithmetic rather than guessed at.
                long nowScore = now is { } n1 ? UpgradeScore.ScoreFrom(n1.Have, n1.Need) ?? -1 : -1;
                // `lastNeed >= 0` matters as much as the guard on lastHave below it. A real
                // denominator is always a ladder step — 1, 2, 4 … 1024 — so `!= -1` is true for
                // EVERY read, and without this test the invalidated baseline makes "levelled up"
                // fire on the next attempt whatever it did. That put the whole phantom-merge chain
                // back: a look-alike counted as a merge, deadEnds reset, a correct reject un-learned,
                // and its picture saved to disk as the reference copy.
                bool levelledUp = lastNeed >= 0 && now is { } n2 && n2.Need != lastNeed;
                bool moved = loggedMerge                            // the server said so — nothing to weigh
                    || (nowScore >= 0 && lastScore >= 0
                        ? nowScore > lastScore
                        // The numerator fallback needs a BASELINE, and `lastHave < 0` means there
                        // isn't one — the previous attempt merged on the server's word while the
                        // counter was unreadable, so the number on screen has moved for a reason
                        // already accounted for. Without this guard the next attempt reads the
                        // PREVIOUS merge's result, calls itself a merge, resets deadEnds, un-learns
                        // a correct reject, and can write a totem's picture to disk as the
                        // reference copy. Not knowing is the honest answer here.
                        : levelledUp || (lastHave >= 0 && now is { } n3 && n3.Have != lastHave));
                long gained = nowScore >= 0 && lastScore >= 0 ? nowScore - lastScore : 0;
                // UNCONDITIONAL, including the -1. Keeping the last good score across an
                // undecodable read meant the NEXT reading — a true one, one point higher after the
                // merge we already counted — looked like a fresh merge on an empty square: a
                // phantom that also reset deadEnds, the only guard against the scan picking the
                // target item up and putting it back forever.
                lastScore = nowScore;
                // Invalidated together, always. Keeping a stale numerator while the score went to
                // -1 is what makes the phantom above possible.
                if (now is { } n4) { lastHave = n4.Have; lastNeed = n4.Need; }
                else { lastHave = -1; lastNeed = -1; }

                if (moved)
                {
                    deadEnds = 0;
                    Stats.Merged++;
                    string worth = gained > 1 ? $" (+{gained} points — that copy was a +{UpgradeScore.TierFor(gained)})" : "";
                    Log?.Invoke(now is null
                        // Confirmed by the server, not by the counter. Printing the last number we
                        // happened to read as though it were current would be a lie about the one
                        // figure the user reads to decide whether to run again.
                        ? "✔ merged — the server confirmed it, but the tier counter didn't read this time, so the "
                          + "number above is from before this merge."
                        : levelledUp && nowScore >= 0
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
                    // Only in signature mode. A learned reject exists because the signatures could not
                    // tell two icons apart; the pixels can, so there is nothing for it to add and a
                    // persisted blacklist is the one mistake here with no symptom.
                    if (grid is null && pendingFine is not null && _plan.HasFineIcon && !_plan.HasPixels)
                    {
                        double d = QuestFind.SigDistance(pendingFine, _plan.IconSigFine!);
                        double bar = Math.Max(RejectMinDistance, confirmedFine + RejectSafety);
                        // The item said its own name. Whatever went wrong here, it was not identity —
                        // the merge window was covered, the click missed, the counter lagged — and
                        // blacklisting the picture of a confirmed copy is the one mistake with no
                        // symptom, so the name overrules every signature argument below it.
                        if (nameConfirmed)
                            Log?.Invoke("· not learning that picture — the tooltip named it correctly, so this is a "
                                      + "click or a window problem, not the wrong item.");
                        else if (confirmedFine < 0)
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
            // The gate gets its own sentence. "Done — 1 merged" and "Done — 1 merged, and I hovered
            // forty squares that turned out to be something else" describe the same bag, and only
            // the second one tells you the icon pick wants another look.
            if (namedOut.Count > 0)
                avoided += $" {namedOut.Count} square(s) named themselves something else and were never touched.";
            Finish((gateRefused
                ? $"⚠ Nothing merged. {namedOut.Count} square(s) looked like the item but named themselves "
                  + $"something other than {wantName}, so none of them were touched. If those really are copies, "
                  + "check the spelling against the tooltip in game — press \"test the name read\" to see what "
                  + "she can read."
                : guardTripped
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
