using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Login;

namespace EQAvatar.Spike.Roles;

/// <summary>
/// Finding the things a turn-in needs when they are no longer where they were picked.
///
/// The first field test proved fixed points are only half an answer: the GIVE button never moves,
/// but the TOTEMS migrate through the bags as each one is consumed, and the NPC is never standing
/// on the same pixels twice. So the fixed point becomes the fallback, and two finders sit in
/// front of it:
///
/// ITEMS BY ICON. An item's icon is a fixed sprite the UI stamps into whatever slot holds it —
/// the one screen element that genuinely IS the same pixels every time. At pick time the dragged
/// box's pixels are distilled into a 6×6 colour signature; at run time every cell of the bag area
/// is scored against it and the best match is clicked. When NO cell matches, that is the honest
/// "out of items" signal — better evidence than an unanswered offer.
///
/// THE NPC BY NAMEPLATE. The game paints the target's name in large text above its head. OCR
/// finds that text; the click lands at a body offset learned at pick time (the vector from the
/// nameplate to the spot the user actually clicked). The nameplate follows the NPC wherever he
/// stands, so the click does too.
///
/// Everything here is static and side-effect-free so the runner and the card's hover test use
/// EXACTLY the same lookups — a test that exercises different code than the run proves nothing.
/// </summary>
public static class QuestFind
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    public const int SigGrid = 6;                 // 6×6 cells × 3 channels = 108 doubles
    /// <summary>
    /// The CONFIRM grid. 6×6 is a fast, forgiving screen: it slides cheaply and it tolerates the
    /// half-pixel misalignment that comes of stepping a window across a bag. What it cannot do is
    /// tell two icons apart that share a palette — thirty-six average colours of a brown, bone and
    /// gold Talisman of Kejaar Kerrath and a brown, bone and gold Desecrated Kejaar Totem land
    /// within a few points of each other, which is how a sweep that had just merged two real copies
    /// went looking for a third and found a totem.
    ///
    /// So the coarse grid still FINDS, and this one CONFIRMS: four times the cells over the same
    /// box, applied only to the single best candidate, where the cost is one region read instead of
    /// several hundred. Two icons can share an average colour; they cannot share 144 of them.
    /// </summary>
    public const int SigGridFine = 12;            // 12×12 cells × 3 channels = 432 doubles
    public const int SigLenFine = SigGridFine * SigGridFine * 3;
    /// <summary>Mean per-channel diff to accept an icon match. NOT "identical images ≈ 10": the
    /// learned sig comes from the user's tight drag and the runtime sig from a cell's inner 70%,
    /// so margins and centering differ and a TRUE match lands ~20–40. Empty dark slots against a
    /// coloured icon sit well above 80, so 55 keeps daylight on both sides. Every match and every
    /// rejection logs its number — tune from the log, not from theory.</summary>
    public const double IconAcceptDistance = 55;
    /// <summary>How far (normalized) the nameplate may sit from where it was learned. Beyond this
    /// the "match" is almost certainly the TARGET WINDOW — which also prints the name, never
    /// moves, and would send an item-laden click into UI chrome — so fall back to the fixed pick
    /// instead of trusting it.</summary>
    public const double NpcMaxDrift = 0.22;
    /// <summary>Accept threshold for the SLIDING search, which compares windows of the exact
    /// learned size: aligned true matches score under ~15, a different item's icon 50+. Far
    /// tighter than the old grid compare — which once called Indicolite Gauntlets a totem at 24,
    /// because inner-70%-of-a-guessed-cell versus a tight drag blurs everything toward everything.</summary>
    public const double SlidingAcceptDistance = 35;

    // ---------------------------------------------------------------- capture

    public static Bitmap? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return null;
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        try
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }

    public static (double X, double Y, double W, double H)? WindowRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return null;
        return (r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    // ---------------------------------------------------------------- icon signatures

    /// <summary>Distil a normalized region of a frame into a 6×6 grid of average colours.</summary>
    public static double[]? SigFromRegion(Bitmap frame, double nx, double ny, double nw, double nh)
        => SigFromRegion(frame, nx, ny, nw, nh, SigGrid);

    /// <summary>
    /// The same, at whatever resolution the caller asks for. <paramref name="grid"/> = 6 is the
    /// sliding search's fast screen; 12 is the confirm.
    ///
    /// Note the region is IDENTICAL either way — same box, same pixels — so a fine signature and a
    /// coarse one describe the same thing at two zoom levels, and one can never be found where the
    /// other wasn't.
    /// </summary>
    public static double[]? SigFromRegion(Bitmap frame, double nx, double ny, double nw, double nh, int grid)
    {
        if (grid < 1) return null;
        int x0 = (int)(nx * frame.Width), y0 = (int)(ny * frame.Height);
        int w = Math.Max(grid, (int)(nw * frame.Width)), h = Math.Max(grid, (int)(nh * frame.Height));
        if (x0 < 0 || y0 < 0 || x0 + w > frame.Width || y0 + h > frame.Height) return null;

        var rect = new Rectangle(x0, y0, w, h);
        BitmapData data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf;
        int stride = data.Stride;
        try
        {
            buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        }
        finally { frame.UnlockBits(data); }

        var sig = new double[grid * grid * 3];
        var counts = new int[grid * grid];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int gy = Math.Min(grid - 1, y * grid / h);
            for (int x = 0; x < w; x++)
            {
                int cell = gy * grid + Math.Min(grid - 1, x * grid / w);
                int i = row + x * 4;                                 // BGRA
                sig[cell * 3 + 0] += buf[i + 2];
                sig[cell * 3 + 1] += buf[i + 1];
                sig[cell * 3 + 2] += buf[i + 0];
                counts[cell]++;
            }
        }
        for (int c = 0; c < counts.Length; c++)
            if (counts[c] > 0)
            {
                sig[c * 3 + 0] /= counts[c];
                sig[c * 3 + 1] /= counts[c];
                sig[c * 3 + 2] /= counts[c];
            }
        return sig;
    }

    /// <summary>Mean per-channel difference between two signatures. ~0 identical, 255 opposite.</summary>
    public static double SigDistance(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return double.MaxValue;
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    /// <summary>Col/Row are -1 for sliding-search hits, which aren't grid-aligned.</summary>
    public sealed record IconHit(double X, double Y, int Col, int Row, double Dist);

    /// <summary>
    /// The sliding search, decoupled from any one role's script: a frame, a normalized bag rect,
    /// a learned signature and the size it was learned at. Auto Merge counts copies with the same
    /// code the Quest Runner finds them with — two implementations of "is this icon here?" would
    /// be two chances to disagree about what the user is looking at.
    /// </summary>
    public static IconHit? FindIconInRect(Bitmap frame, double bx, double by, double bw, double bh,
                                          double[] sig, double iw, double ih)
    {
        if (sig.Length != SigGrid * SigGrid * 3 || iw <= 0.002 || ih <= 0.002) return null;
        double stepX = Math.Max(iw / 2, 0.002), stepY = Math.Max(ih / 2, 0.002);
        IconHit? best = null;
        int guard = 0;
        for (double y = by; y + ih <= by + bh + 1e-9; y += stepY)
            for (double x = bx; x + iw <= bx + bw + 1e-9; x += stepX)
            {
                if (++guard > 20000) return best;
                double[]? probe = SigFromRegion(frame, x, y, iw, ih);
                if (probe is null) continue;
                double dist = SigDistance(probe, sig);
                if (best is null || dist < best.Dist)
                    best = new IconHit(x + iw / 2, y + ih / 2, -1, -1, dist);
            }
        return best;
    }

    /// <summary>
    /// EVERY copy in the rect, as non-overlapping hits under the accept threshold — "how many of
    /// these do I have?" answered by looking rather than by asking the user to count.
    ///
    /// Non-overlapping matters: the search steps in half-icon strides, so a single icon is hit
    /// four times over. Claiming four copies where one sits would turn a forecast into a fiction.
    /// </summary>
    public static List<IconHit> FindAllIcons(Bitmap frame, double bx, double by, double bw, double bh,
                                             double[] sig, double iw, double ih, double accept)
    {
        var hits = new List<IconHit>();
        if (sig.Length != SigGrid * SigGrid * 3 || iw <= 0.002 || ih <= 0.002) return hits;
        double stepX = Math.Max(iw / 3, 0.002), stepY = Math.Max(ih / 3, 0.002);
        var raw = new List<IconHit>();
        int guard = 0;
        bool bailed = false;
        for (double y = by; y + ih <= by + bh + 1e-9 && !bailed; y += stepY)
            for (double x = bx; x + iw <= bx + bw + 1e-9; x += stepX)
            {
                // break exits the INNER loop only, which used to leave the outer one grinding on
                // and return a partial count that the forecast then printed as a fact.
                if (++guard > 40000) { bailed = true; break; }
                double[]? probe = SigFromRegion(frame, x, y, iw, ih);
                if (probe is null) continue;
                double dist = SigDistance(probe, sig);
                if (dist <= accept) raw.Add(new IconHit(x + iw / 2, y + ih / 2, -1, -1, dist));
            }
        // Best first, then greedily drop anything sitting on top of one already taken.
        //
        // ⚠ This dedupe is only safe when the COARSE score is also the judge (the colour-only
        // paths). When native pixels judge afterwards, deduping here loses real copies: a window
        // STRADDLING two adjacent identical icons — half of each — averages out to very nearly the
        // icon itself, and when that straddle orders ahead of its neighbours it suppresses BOTH
        // real centres… and then fails the pixel test, because the nearest true centre is half a
        // slot away, outside the alignment search. Two copies traded for one ghost, per pair, which
        // is exactly "19 in a bag, 3 counted — but 12 when I spread them out". Pixel-armed callers
        // use FindAllCopies below, which aligns FIRST and dedupes on where icons actually are.
        foreach (IconHit h in raw.OrderBy(h => h.Dist))
        {
            bool overlaps = hits.Any(k => Math.Abs(k.X - h.X) < iw * 0.7 && Math.Abs(k.Y - h.Y) < ih * 0.7);
            if (!overlaps) hits.Add(h);
        }
        return hits;
    }

    /// <summary>A pixel-confirmed copy: where it ACTUALLY is (aligned, not the proposal grid's
    /// guess), how well its pixels match, and the coarse distance that proposed it.</summary>
    public sealed record CopyHit(double X, double Y, double Ncc, double Coarse);

    /// <summary>
    /// Every square that could PLAUSIBLY be the icon, with almost no deduplication — the raw
    /// material for a pixel judge. The only trimming is of proposals so close together (under a
    /// quarter icon) that they must snap to the same place; anything looser throws away real
    /// copies, because the greedy suppressor cannot tell a straddle between two identical icons
    /// from the icons themselves. That distinction is precisely what the pixels are for.
    /// </summary>
    public static List<IconHit> ProposeIcons(Bitmap frame, double bx, double by, double bw, double bh,
                                             double[] sig, double iw, double ih, double accept)
    {
        var kept = new List<IconHit>();
        if (sig.Length != SigGrid * SigGrid * 3 || iw <= 0.002 || ih <= 0.002) return kept;
        double stepX = Math.Max(iw / 3, 0.002), stepY = Math.Max(ih / 3, 0.002);
        var raw = new List<IconHit>();
        int guard = 0;
        bool bailed = false;
        for (double y = by; y + ih <= by + bh + 1e-9 && !bailed; y += stepY)
            for (double x = bx; x + iw <= bx + bw + 1e-9; x += stepX)
            {
                if (++guard > 40000) { bailed = true; break; }
                double[]? probe = SigFromRegion(frame, x, y, iw, ih);
                if (probe is null) continue;
                double dist = SigDistance(probe, sig);
                if (dist <= accept) raw.Add(new IconHit(x + iw / 2, y + ih / 2, -1, -1, dist));
            }
        // 0.25 is chosen against the two distances that matter: the proposal stride is a THIRD of
        // an icon, so two grid proposals are at least 0.33 apart and never suppress each other —
        // and a straddle sits 0.5 from each neighbour, so it never suppresses a real centre either.
        // On today's fixed grid this pass therefore removes nothing at all; it exists as the safety
        // rail for any future proposer with a finer or irregular step, so that "almost no dedupe"
        // can never quietly become "no dedupe" and hand the judge the same square four times.
        foreach (IconHit h in raw.OrderBy(h => h.Dist))
            if (!kept.Any(k => Math.Abs(k.X - h.X) < iw * 0.25 && Math.Abs(k.Y - h.Y) < ih * 0.25))
                kept.Add(h);
        return kept;
    }

    /// <summary>
    /// Every copy of the icon in the rect, judged by its PIXELS — align first, decide second,
    /// dedupe last, on the aligned positions.
    ///
    /// The order is the entire fix for a counter that read 3 where 19 sat. Deduping the colour
    /// proposals before the pixels judged let a straddle between two adjacent identical icons eat
    /// both of its neighbours and then fail the pixel test itself; here every plausible proposal is
    /// aligned to wherever its best correlation actually is, the ones that pass are by construction
    /// centred on real icons, and only THEN are duplicates folded — at half an icon, which two
    /// distinct slots can never violate.
    /// </summary>
    public static List<CopyHit> FindAllCopies(Bitmap frame, double bx, double by, double bw, double bh,
                                              double[] sig, double iw, double ih, double proposeAt,
                                              IconPatch reference, int wantW, int wantH, double nccAccept)
    {
        var confirmed = new List<CopyHit>();
        foreach (IconHit h in ProposeIcons(frame, bx, by, bw, bh, sig, iw, ih, proposeAt))
        {
            (double best, int dx, int dy) = BestNcc(frame, h.X, h.Y, reference, wantW, wantH);
            if (best < nccAccept) continue;
            confirmed.Add(new CopyHit(h.X + (double)dx / frame.Width, h.Y + (double)dy / frame.Height,
                                      best, h.Dist));
        }
        var final = new List<CopyHit>();
        foreach (CopyHit c in confirmed.OrderByDescending(c => c.Ncc))
            if (!final.Any(k => Math.Abs(k.X - c.X) < iw * 0.5 && Math.Abs(k.Y - c.Y) < ih * 0.5))
                final.Add(c);
        return final;
    }

    /// <summary>
    /// Slide a window of the learned icon's OWN size across the bag area in half-icon steps and
    /// return the best-matching position. No columns, no rows, no questions for the user: the
    /// tight box they dragged around one copy already says how big a slot's icon is, and comparing
    /// same-sized regions is what makes the score mean something.
    /// </summary>
    public static IconHit? FindIconSliding(IntPtr hwnd, QuestScript script, TurnInStep step)
    {
        if (!script.BagSet || step.IconSig is not { Length: SigGrid * SigGrid * 3 } || !step.HasIconSize) return null;
        using Bitmap? frame = Capture(hwnd);
        if (frame is null) return null;

        return FindIconInRect(frame, script.BagX, script.BagY, script.BagW, script.BagH,
                              step.IconSig, step.IconW, step.IconH);
    }

    /// <summary>
    /// Scan the script's bag area for the cell that best matches a step's learned icon.
    /// Returns the best hit REGARDLESS of threshold — the caller decides what "no match" means,
    /// because "closest cell scored 87" is exactly what a useful out-of-items log line says.
    /// </summary>
    public static IconHit? FindIconCell(IntPtr hwnd, QuestScript script, TurnInStep step)
    {
        if (!script.BagSet || step.IconSig is not { Length: SigGrid * SigGrid * 3 }) return null;
        using Bitmap? frame = Capture(hwnd);
        if (frame is null) return null;

        double cw = script.BagW / script.BagCols, ch = script.BagH / script.BagRows;
        IconHit? best = null;
        for (int row = 0; row < script.BagRows; row++)
            for (int col = 0; col < script.BagCols; col++)
            {
                // sample the inner ~70% of the cell so slot borders don't vote
                double nx = script.BagX + cw * col + cw * 0.15;
                double ny = script.BagY + ch * row + ch * 0.15;
                double[]? sig = SigFromRegion(frame, nx, ny, cw * 0.70, ch * 0.70);
                if (sig is null) continue;
                double dist = SigDistance(sig, step.IconSig);
                if (best is null || dist < best.Dist)
                    best = new IconHit(script.BagX + cw * (col + 0.5), script.BagY + ch * (row + 0.5), col, row, dist);
            }
        return best;
    }

    // ---------------------------------------------------------------- matching the actual pixels

    /// <summary>
    /// An icon's REAL pixels, at the size the game draws them.
    ///
    /// Every signature above this line throws information away: a 6×6 or even a 12×12 grid of
    /// average colours over a 26-pixel icon is a summary, and summaries of two brown-and-gold
    /// necklace icons look alike no matter how many cells you use. There are 26×26×3 ≈ 2,000
    /// numbers actually on the screen. This keeps all of them.
    /// </summary>
    public sealed class IconPatch
    {
        public int W { get; set; }
        public int H { get; set; }
        /// <summary>Row-major RGB, 3 bytes a pixel. Base64 in the plan file — about 2.7 KB.</summary>
        public string Data { get; set; } = "";

        /// <summary>The window width this was learned at. Without it the patch is a pixel count with
        /// no idea what it was a pixel count OF, and a resized game window turns every real copy
        /// into a miss with nothing on screen to say why.</summary>
        public int FrameW { get; set; }
        public int FrameH { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] private byte[]? _px;
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[] Pixels
        {
            get
            {
                // Decoding happens behind a property that `HasPixels` reads during a render pass.
                // A hand-edited or truncated file would otherwise throw a FormatException out of a
                // getter, unwind through a click handler, and close the app — so a broken patch
                // degrades to "no pixels" and the page says colour-only instead.
                if (_px is not null) return _px;
                try { _px = Data.Length > 0 ? Convert.FromBase64String(Data) : Array.Empty<byte>(); }
                catch { _px = Array.Empty<byte>(); }
                return _px;
            }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Ok => W > 3 && H > 3 && Pixels.Length == W * H * 3;

        /// <summary>
        /// A patch that lives only in memory, with no base64 round trip.
        ///
        /// Stored patches come out of a JSON file, so <see cref="Data"/> is their real form and
        /// <see cref="Pixels"/> decodes it once. A CROP has no file behind it: encoding 2,880 bytes
        /// to base64 only so the very next line can decode them again is about 11 KB of garbage per
        /// call, and the call sites make it inside a loop bounded at 220 candidates — several
        /// megabytes a scan to rebuild something byte-identical every time.
        ///
        /// NOT for anything that gets serialised. <see cref="Data"/> is deliberately left empty, so
        /// a patch made this way and written to questscripts.json would come back blank.
        /// </summary>
        public static IconPatch InMemory(byte[] px, int w, int h, int frameW, int frameH)
            => new() { W = w, H = h, FrameW = frameW, FrameH = frameH, _px = px };
    }

    /// <summary>
    /// How alike two patches are, as normalized cross-correlation: 1.0 identical, 0 unrelated.
    ///
    /// NOT the mean absolute difference the signatures used. That measure moves when the picture
    /// gets brighter, when the slot behind it is highlighted, and when the window lands a pixel
    /// off — so a real copy scored 45–53 against a bar of 45 and the bar had to sit exactly on top
    /// of the noise. Cross-correlation subtracts each patch's own mean and divides by its own
    /// spread, so a uniform brightness or contrast shift cancels out entirely and what is left is
    /// whether the two pictures have the same SHAPE. Real copies land near 0.95; a different icon
    /// with the same palette lands near 0.5. That gap is what makes a threshold meaningful.
    /// </summary>
    public static double Ncc(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return -1;
        double sa = 0, sb = 0;
        for (int i = 0; i < n; i++) { sa += a[i]; sb += b[i]; }
        double ma = sa / n, mb = sb / n;
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y; da += x * x; db += y * y;
        }
        // A patch with no variation at all (a flat empty slot) correlates with nothing. Saying "no
        // match" is right; saying "divide by zero" is not.
        if (da <= 1e-9 || db <= 1e-9) return 0;
        return num / Math.Sqrt(da * db);
    }

    /// <summary>Lift a rectangle of a frame out as raw RGB at its real size.</summary>
    public static byte[]? RawRgb(Bitmap frame, int x0, int y0, int w, int h)
    {
        if (w <= 0 || h <= 0 || x0 < 0 || y0 < 0 || x0 + w > frame.Width || y0 + h > frame.Height) return null;
        BitmapData data = frame.LockBits(new Rectangle(x0, y0, w, h), ImageLockMode.ReadOnly,
                                         PixelFormat.Format32bppArgb);
        try
        {
            // ROW BY ROW. A sub-rect lock hands back the PARENT bitmap's stride, so `new byte[stride*h]`
            // is a full-window-width strip — 200 KB for a 26 px icon, which is over the large-object
            // threshold, allocated once per correlation offset. It also reads past the end of the
            // pixel buffer on the last row whenever x0 > 0, which the bag's bottom row can reach.
            int stride = data.Stride;
            var row = new byte[w * 4];
            var outp = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;                          // BGRA
                    int o = (y * w + x) * 3;
                    outp[o] = row[i + 2]; outp[o + 1] = row[i + 1]; outp[o + 2] = row[i];
                }
            }
            return outp;
        }
        finally { frame.UnlockBits(data); }
    }

    /// <summary>Learn an icon's pixels from the frame the user drew on.</summary>
    public static IconPatch? PatchFromRegion(Bitmap frame, double nx, double ny, double nw, double nh)
    {
        int x0 = (int)Math.Round(nx * frame.Width), y0 = (int)Math.Round(ny * frame.Height);
        int w = (int)Math.Round(nw * frame.Width), h = (int)Math.Round(nh * frame.Height);
        if (w < 4 || h < 4) return null;
        byte[]? px = RawRgb(frame, x0, y0, w, h);
        if (px is null) return null;
        return new IconPatch
        { W = w, H = h, FrameW = frame.Width, FrameH = frame.Height, Data = Convert.ToBase64String(px) };
    }

    /// <summary>How much of a patch's width and height the centre crop keeps. 0.76 throws away a
    /// twelfth of the picture off each side — enough to clear a slot's frame, its background and the
    /// corner a stack draws its quantity in, while still leaving 58% of the area to correlate.
    /// A CONSTANT rather than a parameter, because <see cref="CentreOf"/> memoises its result per
    /// patch and a per-call crop size would hand back somebody else's crop.</summary>
    public const double CentreKeep = 0.76;

    /// <summary>
    /// One crop per patch, for as long as the patch itself lives.
    ///
    /// The crop is a pure function of the patch, and it is asked for hundreds of times per scan for
    /// an input that cannot change in between. A weak table keyed on the patch keeps exactly one
    /// and lets it die with the patch — so re-picking an icon does not leave the old crop behind,
    /// and nothing has to remember to invalidate anything.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IconPatch, IconPatch> _centres = new();

    /// <summary>
    /// Where the ITEM is inside a reference, and how much of the reference it actually is.
    ///
    /// This exists because of a measurement, not a theory. A field reference for a Desecrated
    /// Kejaar Totem was 40×40 px; the totem's own pixels were a 13×38 box inside it — 31% of the
    /// area. The other 69% was flat empty slot, plus the slot's frame, plus slivers of whatever
    /// happened to be in the neighbouring slots, because the picking square (40 px) was half as
    /// wide again as the game's actual slot pitch (27 px).
    ///
    /// Correlation over that reference is therefore mostly a correlation over furniture that is
    /// IDENTICAL IN EVERY SLOT. An empty slot scored 55% against it. A real copy only scored well
    /// while its NEIGHBOURS also matched — so the run worked for twenty-five cycles and then
    /// stopped, not because the item changed but because fifty reward items piled up around it.
    /// That is a failure no threshold can fix and no amount of the item being right can survive.
    ///
    /// Returns null when the patch is unusable or when the answer would be "all of it" — there is
    /// nothing to say about a reference that is already tight.
    /// </summary>
    public readonly record struct Content(int X, int Y, int W, int H, double Ink,
                                          bool Top, bool Bottom, bool Left, bool Right)
    {
        /// <summary>The inked box as a fraction of the whole reference's area.</summary>
        public double AreaFraction(IconPatch p) => (double)W * H / Math.Max(1, p.W * p.H);

        public bool RunsOffEdge => Top || Bottom || Left || Right;

        /// <summary>
        /// Which edges, in words — the sentence that names the geometry.
        ///
        /// Worth its own method because the FOUR facts say more than their disjunction does. "Off
        /// the top and bottom but not the sides" is a column; "off the sides but not the top and
        /// bottom" is a row; and a person who reads either of those knows immediately which of
        /// their own slots is being eaten. Collapsing them to one bool threw away the single most
        /// diagnostic thing this measurement produces.
        /// </summary>
        public string Edges()
        {
            if (Top && Bottom && !Left && !Right) return "the top and bottom edges but not the sides";
            if (Left && Right && !Top && !Bottom) return "both side edges but not the top or bottom";
            if (Top && Bottom && Left && Right) return "every edge";
            var parts = new List<string>();
            if (Top) parts.Add("top");
            if (Bottom) parts.Add("bottom");
            if (Left) parts.Add("left");
            if (Right) parts.Add("right");
            if (parts.Count == 0) return "no edge";
            string joined = parts.Count == 1 ? parts[0]
                          : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[^1];
            return "the " + joined + (parts.Count > 1 ? " edges" : " edge");
        }

        /// <summary>
        /// Is the picture reaching the edge because the square caught the NEIGHBOURING slots,
        /// rather than because the icon simply fills its square?
        ///
        /// No test over these pixels alone can separate those two — three totems stacked in a
        /// column with no gap between them and one tall totem are the same picture. What CAN
        /// separate them is how much of the square is inked at all. A tightly picked icon that
        /// touches its edges fills most of its square; a square that has swallowed its neighbours
        /// is mostly empty slot with ink at the far ends. So: ink at an edge AND a sparse square.
        /// One without the other is not evidence, and warning on it would fire at a good pick and
        /// then give advice that only makes it worse.
        /// </summary>
        public bool LikelyNeighbours => RunsOffEdge && Ink < 0.45;
    }

    /// <summary>How different from the background a pixel must be to count as the item. Generous:
    /// the cost of counting a background pixel as ink is a slightly loose box, and the cost of
    /// missing a dark part of an item is cutting the item in half.</summary>
    private const int ContentTol = 26;

    /// <summary>The smallest side, and (squared) the smallest sample count, a content crop may have.
    /// Twelve is the point below which the numbers this file is calibrated on stop meaning much.</summary>
    private const int MinCropSide = 12;

    private static bool Bg(byte[] px, int i, int[] bg)
        => Math.Abs(px[i] - bg[0]) <= ContentTol && Math.Abs(px[i + 1] - bg[1]) <= ContentTol
           && Math.Abs(px[i + 2] - bg[2]) <= ContentTol;

    public static Content? ContentBox(IconPatch p)
    {
        if (p is not { Ok: true } || p.W < 8 || p.H < 8) return null;
        byte[] px = p.Pixels;

        // THE BACKGROUND IS THE MEDIAN OF THE INTERIOR, not of a ring around the edge.
        //
        // The ring was the obvious choice and it is wrong, provably. Measured on the field
        // reference: a two-pixel ring is half slot-frame and half slot-fill, so its median lands
        // between the two at a colour that is NEITHER, matches almost nothing, and the whole method
        // refuses. The interior is ~75% background on any reference worth cropping, so its median
        // IS the background — and when it isn't, the item fills the patch, every pixel reads as
        // ink, the box comes out full-size, and ContentOf correctly answers "nothing to crop to".
        // The failure mode is a no-op rather than a wrong box.
        const int Inset = 2;
        int iw = p.W - Inset * 2, ih = p.H - Inset * 2;
        if (iw < 4 || ih < 4) return null;
        var ch = new int[3][];
        for (int c = 0; c < 3; c++) ch[c] = new int[iw * ih];
        int n = 0;
        for (int y = Inset; y < p.H - Inset; y++)
            for (int x = Inset; x < p.W - Inset; x++)
            {
                int i = (y * p.W + x) * 3;
                ch[0][n] = px[i]; ch[1][n] = px[i + 1]; ch[2][n] = px[i + 2];
                n++;
            }
        var bg = new int[3];
        for (int c = 0; c < 3; c++) { Array.Sort(ch[c], 0, n); bg[c] = ch[c][n / 2]; }

        // How much of the interior actually agrees with that median. Under a quarter and there is
        // no coherent background to measure an item against — a busy picture, or a box laid across
        // several different things — and a bounding box taken from it would be noise wearing a
        // number.
        int agree = 0;
        for (int y = Inset; y < p.H - Inset; y++)
            for (int x = Inset; x < p.W - Inset; x++)
            {
                int i = (y * p.W + x) * 3;
                if (Math.Abs(px[i] - bg[0]) <= ContentTol && Math.Abs(px[i + 1] - bg[1]) <= ContentTol
                    && Math.Abs(px[i + 2] - bg[2]) <= ContentTol) agree++;
            }
        if (agree < n * 0.25) return null;

        int x0 = p.W, y0 = p.H, x1 = -1, y1 = -1, ink = 0;
        // The 1 px frame on each side is skipped: it is the SLOT's, not the item's, and counting it
        // makes every box the full width of the patch — which is exactly the answer that would hide
        // the problem this method exists to find.
        for (int y = 1; y < p.H - 1; y++)
            for (int x = 1; x < p.W - 1; x++)
            {
                int i = (y * p.W + x) * 3;
                if (Math.Abs(px[i] - bg[0]) <= ContentTol && Math.Abs(px[i + 1] - bg[1]) <= ContentTol
                    && Math.Abs(px[i + 2] - bg[2]) <= ContentTol) continue;
                ink++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        if (x1 < 0 || ink < 12) return null;                     // an empty slot has no item in it
        // DOES THE INK RUN OFF THE EDGE? This is the honest test for "the square is not confined to
        // one slot", and it is the one that cannot be talked out of firing.
        //
        // A bounding box can be quietly wrong: it measures where ink IS, not what the ink belongs
        // to, and on a square that overlaps its neighbours the box grows to cover them and then
        // reports a healthy-looking fraction. The field reference is the proof — a 13×38 inked box
        // in a 40×40 pick, which is impossible for a single icon in a 27 px slot. Those 38 rows are
        // an unbroken column: the totems sat stacked vertically, so the square held the BOTTOM of
        // the one above and the TOP of the one below as well. Three items in a reference for one.
        //
        // Ink reaching the border means something was cut through. A square that really does sit
        // inside one slot has that slot's frame and the gap to the next one around it, which is
        // background, so its ink cannot reach the edge. And unlike the area fraction, a fuller bag
        // makes this MORE likely to fire, not less — which is the right direction, because a fuller
        // bag is exactly when an oversized square starts failing.
        // A RUN, NOT A PIXEL. One inked pixel in the border ring is a frame corner or an
        // anti-aliased edge; a quarter of a side is something the square cut through. The first
        // draft tested a single pixel and would have fired on the very reference the new advice
        // tells people to produce — an icon filling its square touches its own border by
        // construction — and then told them to shrink it further, which is unsatisfiable.
        bool Run(int n, int len) => n * 4 >= len;
        int topN = 0, botN = 0, leftN = 0, rightN = 0;
        for (int x = 1; x < p.W - 1; x++)
        {
            if (!Bg(px, (1 * p.W + x) * 3, bg)) topN++;
            if (!Bg(px, ((p.H - 2) * p.W + x) * 3, bg)) botN++;
        }
        for (int y = 1; y < p.H - 1; y++)
        {
            if (!Bg(px, (y * p.W + 1) * 3, bg)) leftN++;
            if (!Bg(px, (y * p.W + p.W - 2) * 3, bg)) rightN++;
        }
        return new Content(x0, y0, x1 - x0 + 1, y1 - y0 + 1,
                           (double)ink / Math.Max(1, (p.W - 2) * (p.H - 2)),
                           Run(topN, p.W - 2), Run(botN, p.W - 2),
                           Run(leftN, p.H - 2), Run(rightN, p.H - 2));
    }

    /// <summary>
    /// The patch cropped down to the item it contains — CONCENTRIC with the patch, deliberately.
    ///
    /// A crop centred on the ITEM rather than on the patch would be tighter, and would be a bug.
    /// <see cref="BestNcc"/> lays a reference over the frame centred on the point it is given and
    /// reports the offset it liked; a crop whose own centre had moved would need that offset
    /// carried back out and undone by every caller, and the day someone forgot, the runner would
    /// click a few pixels off the item with nothing on screen to say why. Growing the box
    /// symmetrically instead costs a little margin and keeps every coordinate in this file meaning
    /// exactly one thing.
    ///
    /// On the field reference that prompted this — a 13×38 totem inside a 40×40 pick — the
    /// symmetric box is 19 wide, which takes the item from 32% of the width to 68%. The tight box
    /// would have reached 100%; the difference is not worth a coordinate system with a trap in it.
    ///
    /// Returns the patch ITSELF when there is nothing worth cropping to, which callers test by
    /// reference. Null only for an unusable patch.
    /// </summary>
    public static IconPatch? ContentOf(IconPatch p)
    {
        if (p is not { Ok: true }) return null;
        return _contents.GetValue(p, static k =>
        {
            if (ContentBox(k) is not { } box) return k;
            const int Pad = 2;
            // Half-extents measured from the PATCH's centre to the furthest edge of the item, so
            // the result contains all of the item wherever inside the patch it sits.
            double cx = (k.W - 1) / 2.0, cy = (k.H - 1) / 2.0;
            int hx = (int)Math.Ceiling(Math.Max(cx - box.X, box.X + box.W - 1 - cx)) + Pad;
            int hy = (int)Math.Ceiling(Math.Max(cy - box.Y, box.Y + box.H - 1 - cy)) + Pad;
            int w = Math.Min(k.W, hx * 2 + 1), h = Math.Min(k.H, hy * 2 + 1);
            // Floors, because a background read that went wrong must not be able to shrink a
            // reference to a speck — and a speck correlates with almost anything.
            w = Math.Max(w, Math.Max(MinCropSide, k.W / 3));
            h = Math.Max(h, Math.Max(MinCropSide, k.H / 3));
            // And a floor on SAMPLES, not just on sides. Every calibration number in this file was
            // measured on a full-sized patch; correlation noise grows as 1/sqrt(n) and BestNcc's
            // max-over-offsets biases a non-copy upward as n shrinks, so a tiny crop quietly raises
            // what a DIFFERENT item can reach against an unchanged 0.70 floor. Below this, hand the
            // patch back and let the fixed geometric crop answer instead.
            if (w * h < MinCropSide * MinCropSide) return k;
            // Not worth a second alignment search to shave a pixel off each edge.
            if (w >= k.W - 1 && h >= k.H - 1) return k;
            int x0 = (k.W - w) / 2, y0 = (k.H - h) / 2;
            byte[] src = k.Pixels, dst = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(src, ((y0 + y) * k.W + x0) * 3, dst, y * w * 3, w * 3);
            return IconPatch.InMemory(dst, w, h, k.FrameW, k.FrameH);
        });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IconPatch, IconPatch> _contents = new();

    /// <summary>
    /// The middle of a patch, with the outer border thrown away.
    ///
    /// An inventory slot is not just an icon: it has a frame, a background that differs between
    /// bag windows and between highlighted and plain rows, and — for anything stacked — a quantity
    /// number drawn in a corner. All of that sits at the EDGES, and all of it changes while the
    /// item does not. Correlating the middle answers "is this the same picture" without asking the
    /// chrome to agree as well.
    ///
    /// Used as a SECOND opinion, never a replacement: see <see cref="Score"/>, which computes the
    /// full-patch score first and only consults this when that one has already failed.
    /// </summary>
    public static IconPatch? CentreOf(IconPatch p)
    {
        if (p is not { Ok: true }) return null;
        return _centres.GetValue(p, static k =>
        {
            int w = Math.Max(4, (int)Math.Round(k.W * CentreKeep));
            int h = Math.Max(4, (int)Math.Round(k.H * CentreKeep));
            // A patch small enough that the crop would be the whole thing gets itself back, and
            // Score recognises that by reference and skips the pointless second search.
            if (w >= k.W || h >= k.H) return k;
            int x0 = (k.W - w) / 2, y0 = (k.H - h) / 2;
            byte[] src = k.Pixels, dst = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(src, ((y0 + y) * k.W + x0) * 3, dst, y * w * 3, w * 3);
            return IconPatch.InMemory(dst, w, h, k.FrameW, k.FrameH);
        });
    }

    /// <summary>
    /// How high the CENTRE-ONLY comparison is allowed to lift a score: just under
    /// <see cref="PixelAccept"/>, so a crop can win a square a chance but never a certainty.
    ///
    /// Every calibration number in this file — a real copy over 0.97, a different icon in the same
    /// palette about 0.44, every non-copy measured under 0.51 — was measured on the WHOLE patch.
    /// None of them has been measured on 58% of one, and two things push a crop's non-copy ceiling
    /// up: fewer samples (correlation noise scales with 1/sqrt(n)), and BestNcc returning the
    /// maximum over hundreds of alignment offsets, whose upward bias grows as the sample shrinks.
    ///
    /// So a crop-won score lands in the PROVISIONAL band by construction. That band already has
    /// the right answer to "I think this is it but I can't prove it": offer the item and let the
    /// server say. What it must never do is reach the strict bar, where a square is clicked and
    /// given away on nobody's word but the arithmetic's.
    ///
    /// SQUASHED INTO THE BAND, NOT CLIPPED AT IT. `Math.Min(centre, CropCeiling)` was the obvious
    /// way to write this and it is a serious bug: every crop win above the ceiling returns the
    /// identical 0.84, the selectors are all strict `>`, and so the FIRST square in proposal order
    /// wins and nothing can displace it. A girdle whose middle scores 0.855 in the top row would
    /// beat the real item whose middle scores 0.990 further down, because both report 0.84 and
    /// 0.84 is not greater than 0.84. The map below is strictly increasing in `centre`, so ranking
    /// survives the ceiling — which is the entire reason the scan compares scores at all.
    /// </summary>
    public const double CropCeiling = 0.84;

    /// <summary>
    /// How well the WHOLE square has to do before the centre is worth a second search.
    ///
    /// Not a safety bar — a cost one, and a large one. The crop is a full alignment search of its
    /// own, run for every candidate the colour pass proposes, and that pass proposes hundreds
    /// (MaxProposals caps it at 220). Doing it unconditionally added about 40% to the hottest loop
    /// in the app, in exactly the state where the loop already runs to its cap because nothing
    /// reaches the strict bar and nothing breaks early.
    ///
    /// 0.30 is far below anything this is meant to rescue. The field misses that started all of
    /// this scored 0.552, 0.730 and 0.764 on the whole patch; a square scoring under 0.30 is not
    /// the right item behind a changed border, it is a different picture.
    /// </summary>
    public const double CropTryFrom = 0.30;

    /// <summary>
    /// How well a square matches a reference: the whole patch, and its middle, best of the two.
    ///
    /// The second opinion exists because of a field report — "the icons look the same on my
    /// screen" — against arithmetic that said 55%. When a person looking at two slots sees the
    /// same icon, the disagreement is almost never about the icon; it is about everything ELSE
    /// inside the square, and all of that lives at the edges.
    ///
    /// SHARED, and shared deliberately: the runner scans with this and the quest card's hover test
    /// reports with it. A hover test computing a different number than the run is worse than no
    /// hover test, because it is consulted precisely when the two disagree.
    /// </summary>
    /// <returns>
    /// Ncc — the score to judge and rank by. Cropped — whether the crop was LOAD-BEARING, i.e. the
    /// whole patch alone would not have cleared <see cref="ProbableAccept"/>; that is what decides
    /// whether a score is calibrated evidence, and it is deliberately NOT the same question as
    /// "did the crop nudge the number up". Full — the whole-patch score, carried out so the log can
    /// say what actually happened rather than quoting a guarantee that was measured on a different
    /// picture. Five rounds of review have turned on that distinction; it should be printable.
    /// CropTried — whether the middle was correlated at all, and Centre — what it scored. THREE
    /// outcomes, not two, and they are separate fields because -1 cannot carry both: the crop can be
    /// skipped (too far off to bother), or run and beaten, or run and come back UNMEASURABLE — which
    /// happens when the middle of the square is a single flat colour, i.e. it is an empty slot. That
    /// last one is not a gap in the evidence, it is the most positive identification of an empty
    /// slot this code can make, and it answers the question a stopped run is actually asking.
    /// </returns>
    public static (double Ncc, int Dx, int Dy, bool Cropped, double Full, double Centre, bool CropTried) Score(
        Bitmap frame, double cx, double cy, IconPatch reference, int wantW, int wantH)
    {
        (double full, int dx, int dy) = BestNcc(frame, cx, cy, reference, wantW, wantH);
        // Already a copy, too far off to be worth a second search, or unreadable. The unreadable
        // case rides in on the same test: BestNcc's "I could not measure that" is -1, which is
        // below CropTryFrom, so it returns untouched and the caller narrates it as its own outcome
        // rather than having it quietly overwritten by a crop score of nothing.
        if (full >= PixelAccept || full < CropTryFrom) return (full, dx, dy, false, full, -1, false);
        // THE ITEM'S OWN BOX FIRST, the fixed geometric middle only as a fallback.
        //
        // A fixed 76% crop assumes the item roughly fills the square it was picked from. When it
        // doesn't — a 13 px totem inside a 40 px pick — the middle is still 55% empty slot, which
        // is why the geometric crop barely moved a score it should have rescued outright. Cropping
        // to where the item ACTUALLY is makes the comparison about the item. Falling back matters
        // just as much: a busy or unreadable background gives ContentOf no honest answer, and it
        // says so by handing the patch back rather than inventing a box.
        IconPatch? mid = ContentOf(reference);
        if (mid is null || ReferenceEquals(mid, reference)) mid = CentreOf(reference);
        if (mid is null || ReferenceEquals(mid, reference)) return (full, dx, dy, false, full, -1, false);
        int mw = Math.Max(4, (int)Math.Round(wantW * (double)mid.W / Math.Max(1, reference.W)));
        int mh = Math.Max(4, (int)Math.Round(wantH * (double)mid.H / Math.Max(1, reference.H)));
        // THE PAD COMES FROM THE FULL WINDOW, NOT FROM THE CROP.
        //
        // SearchPadFor derives a radius from the size it is handed, and it is right to — for the
        // whole patch. But the radius has to cover half a PROPOSAL STEP, and ProposeIcons steps in
        // thirds of the FULL icon: for a 40 px icon the nearest proposal can be 6.7 px out, while a
        // 19 px crop would ask for only ±6 and a 13 px one for ±5. Letting the crop pick its own
        // radius reintroduces this file's most expensive bug — the field run that found 5 of 14
        // copies with a fixed ±4 search, where every copy it did align with scored over 99.6%. It
        // would fail silently here, too: a misaligned crop just scores low, loses to the full patch,
        // and the rescue never fires with nothing in the log to say why.
        (double centre, int cdx, int cdy) = BestNcc(frame, cx, cy, mid, mw, mh,
                                                    SearchPadFor(Math.Max(4, wantW)),
                                                    SearchPadFor(Math.Max(4, wantH)));
        if (centre <= full) return (full, dx, dy, false, full, centre, true);
        // Maps centre ∈ (full, 1.0] onto (full, CropCeiling], monotonically. A perfect middle
        // reports exactly the ceiling; everything else reports proportionally less, so two crop
        // wins can still be told apart and the better one still wins.
        double capped = full + (CropCeiling - full) * (centre - full) / (1.0 - full);
        // `Cropped` means the crop is LOAD-BEARING — the whole patch would not have cleared the
        // provisional floor on its own — not merely that it nudged the number up. Callers use this
        // flag to decide what a score is EVIDENCE for, and "the crop won by a hair over a full
        // patch that already scored 0.76" is calibrated evidence: the 0.51 non-copy ceiling was
        // measured on exactly that full patch. Reporting `true` there closed the learning gate on
        // almost every real copy it exists to learn from — the same dead switch as `!_offered`,
        // reintroduced through a different door.
        return capped > full ? (capped, cdx, cdy, full < ProbableAccept, full, centre, true)
                            : (full, dx, dy, false, full, centre, true);
    }

    /// <summary>
    /// How far, in pixels, to hunt for the best alignment around a proposed centre — DERIVED from
    /// how far apart the proposals are, never a constant.
    ///
    /// This is the whole game. `FindAllIcons` steps in thirds of an icon, so the nearest proposal to
    /// a real icon's centre can be half a step away in each axis. If the search is narrower than
    /// that, icons are missed not because they look wrong but because nothing ever lined the
    /// template up with them — and they are missed at a rate you can predict:
    ///
    ///     found ≈ (2·pad / stepX) × (2·pad / stepY)
    ///
    /// A field run with a 39×42 px reference and a fixed ±4 px search found 5 of 14 copies. The
    /// formula says (8/13)×(8/14) = 35%, and 14 × 0.35 = 4.9. It was not a matching problem; every
    /// copy it did align with scored 99.6% or better and every non-copy scored under 51%.
    ///
    /// So the radius covers half a step plus a margin, and the miss rate goes to zero.
    /// </summary>
    public static int SearchPadFor(int iconPx) => Math.Clamp(SearchPadWanted(iconPx), 4, SearchPadCap);

    /// <summary>What the geometry asks for, before the cost cap. Exposed so the caller can SAY when
    /// the cap has bitten — a cap that silently narrows the search is the original bug wearing a
    /// different hat, and it would present identically: real copies reported as "not a copy".</summary>
    public static int SearchPadWanted(int iconPx) => (int)Math.Ceiling(iconPx / 6.0) + 2;

    /// <summary>Cost ceiling. The search is (2p+1)² correlations, so this is quadratic — 40 covers
    /// every icon up to 228 px, which is past any sane inventory slot on any display.</summary>
    public const int SearchPadCap = 40;

    /// <summary>How well the real pixels have to correlate before a square IS the item. 0.85 sits
    /// in the middle of a gap half the scale wide — a real copy scores over 0.97 even brighter or
    /// highlighted, a different icon in the same palette about 0.44.</summary>
    public const double PixelAccept = 0.85;
    /// <summary>The colour signature's bar when it is only PROPOSING candidates for the pixels to
    /// judge. Deliberately loose: a false candidate costs a fraction of a millisecond, a missed one
    /// costs a cycle.</summary>
    public const double CoarseProposeAt = 60;
    /// <summary>Candidates correlated before giving up. Each is an alignment search, and a hand-in
    /// needs ONE copy, not a census.</summary>
    public const int MaxProposals = 220;

    /// <summary>
    /// The floor for a PROVISIONAL match — the item wearing a face we haven't photographed.
    ///
    /// Set against the MEASURED tail, not the mean. "About 0.44" is the average for a different
    /// icon in the same palette; the field measurement is the one that binds — every non-copy
    /// scored under 0.51 — and BestNcc returns the maximum over hundreds of alignment offsets,
    /// which biases every non-copy upward. 0.70 clears that ceiling while still catching the 0.764
    /// a real totem scored after its appearance drifted.
    /// </summary>
    public const double ProbableAccept = 0.70;

    /// <summary>How many of the likeliest squares the alternate appearances are shown to. Small
    /// because each one costs a full alignment search per appearance — and shared, because the
    /// card's hover test exists to mirror the run and a different number here would silently stop
    /// it doing that. Stated rather than hidden: a real copy outside the top of this list is the
    /// one thing this cannot find.</summary>
    public const int LookShortlist = 16;


    /// <summary>
    /// The best correlation obtainable near a point, and where it was found.
    ///
    /// The search window is the whole point. The coarse scan steps in thirds of an icon, so its idea
    /// of "here" is several pixels out — and at native resolution being three pixels out turns an
    /// identical picture into a poor score. Nudging within ±4 px and keeping the best is what makes
    /// the number mean "is this the same icon" instead of "did the slider happen to land square".
    /// </summary>
    /// <summary>Nearest-neighbour resample of a raw RGB patch. Only ever used to follow a window
    /// resize, where the alternative is every real copy quietly failing to match.</summary>
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 3];
        for (int y = 0; y < dh; y++)
        {
            int sy = Math.Min(sh - 1, y * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, x * sw / dw);
                int so = (sy * sw + sx) * 3, dof = (y * dw + x) * 3;
                dst[dof] = src[so]; dst[dof + 1] = src[so + 1]; dst[dof + 2] = src[so + 2];
            }
        }
        return dst;
    }

    /// <summary>
    /// The best correlation obtainable near a point, and where it was found.
    ///
    /// The search window is the whole point. The coarse scan steps in thirds of an icon, so its idea
    /// of "here" is several pixels out — and at native resolution being three pixels out turns an
    /// identical picture into a poor score. Nudging within ±4 px and keeping the best is what makes
    /// the number mean "is this the same icon" instead of "did the slider happen to land square".
    /// </summary>
    /// <param name="wantW">The icon's size in the CURRENT window, if it differs from the size the
    /// patch was learned at. The patch is stored in pixels but located by fractions, so a resized
    /// window would otherwise compare a 26 px reference against a 21 px icon plus five pixels of a
    /// neighbouring slot — every real copy failing, and nothing on screen to say why.</param>
    /// <param name="padOverride">A caller-supplied search radius in pixels, replacing the derived
    /// one. The derived pad covers half a PROPOSAL STEP, which is right when the centre came from
    /// the sliding scan — but a caller asking "is the icon anywhere NEAR here?" (the held-item
    /// check, whose icon rides the cursor at an offset the game chooses) needs to name its own
    /// radius, because its uncertainty has nothing to do with any stride.</param>
    /// <param name="padYOverride">A separate vertical radius. Zero means "use padOverride", which
    /// keeps every existing caller correct. It exists because a NON-SQUARE reference has two
    /// different proposal strides, and forcing one radius on both axes over-searches the short one
    /// — which costs time, and worse, hands one comparison more chances to find a high correlation
    /// than the comparison it is being judged against.</param>
    public static (double Best, int Dx, int Dy) BestNcc(Bitmap frame, double cx, double cy, IconPatch reference,
                                                        int wantW = 0, int wantH = 0, int padOverride = 0,
                                                        int padYOverride = 0)
    {

        if (!reference.Ok) return (-1, 0, 0);
        byte[] want = reference.Pixels;
        int w = reference.W, h = reference.H;
        if (wantW > 3 && wantH > 3 && (wantW != w || wantH != h))
        {
            want = Resample(want, w, h, wantW, wantH);
            w = wantW; h = wantH;
        }

        int px = (int)Math.Round(cx * frame.Width) - w / 2;
        int py = (int)Math.Round(cy * frame.Height) - h / 2;
        // Sized to the step the proposals actually came in on, per axis.
        int padX = padOverride > 0 ? padOverride : SearchPadFor(w);
        int padY = padYOverride > 0 ? padYOverride : padOverride > 0 ? padOverride : SearchPadFor(h);

        // CLAMPED to the frame rather than abandoned at it. Returning "no match" for anything near
        // the window edge would blind the sweep to whole rows of the bag — the bottom row of an
        // inventory sits within a few pixels of the bottom of the screen.
        int x0 = Math.Max(0, px - padX), y0 = Math.Max(0, py - padY);
        int x1 = Math.Min(frame.Width, px + w + padX), y1 = Math.Min(frame.Height, py + h + padY);
        int rw = x1 - x0, rh = y1 - y0;
        // rw < w only when the icon itself doesn't fit on the frame, so there is nothing to compare
        // against and no fallback that would mean anything. The caller narrates this as "too close
        // to the edge of the window to compare", which is exactly what it is.
        if (rw < w || rh < h) return (-1, 0, 0);

        // ONE lock over the whole search area instead of one per offset. Hundreds of LockBits calls
        // per candidate, each copying its own buffer, would be most of the cost of this method.
        byte[]? region = RawRgb(frame, x0, y0, rw, rh);
        if (region is null) return (-1, 0, 0);

        // The reference's mean and spread are the same for every offset, so they are computed once
        // instead of up to two thousand times — and the probe is correlated straight out of the
        // region, so nothing is copied per offset either. Between them that is most of the work.
        int n = w * h * 3;
        double sw = 0;
        for (int i = 0; i < want.Length; i++) sw += want[i];
        double mw = sw / n;
        double dw = 0;
        for (int i = 0; i < want.Length; i++) { double d = want[i] - mw; dw += d * d; }
        if (dw <= 1e-9) return (0, 0, 0);            // a flat reference correlates with nothing

        double best = -1; int bx = 0, by = 0;
        for (int dy = 0; dy + h <= rh; dy++)
            for (int dx = 0; dx + w <= rw; dx++)
            {
                double sb = 0, sbb = 0, sab = 0;
                for (int y = 0; y < h; y++)
                {
                    int ri = ((y + dy) * rw + dx) * 3;
                    int wi = y * w * 3;
                    for (int k = 0; k < w * 3; k++)
                    {
                        double b = region[ri + k];
                        sb += b; sbb += b * b; sab += want[wi + k] * b;
                    }
                }
                // Same quantity as Ncc, rearranged so one pass over the probe does it:
                //   Σ(a-ā)(b-b̄) = Σab − n·ā·b̄      Σ(b-b̄)² = Σb² − n·b̄²
                double mb = sb / n;
                double db = sbb - n * mb * mb;
                if (db <= 1e-9) continue;
                double v = (sab - n * mw * mb) / Math.Sqrt(dw * db);
                if (v > best) { best = v; bx = x0 + dx - px; by = y0 + dy - py; }
            }
        return (best, bx, by);
    }

    // ---------------------------------------------------------------- the NPC by nameplate

    /// <summary>The most OCR-able token of an NPC's name — longest run of letters, 4+ chars
    /// ("Kerran" out of "The Kerran Sha`rr"; backticks never survive OCR anyway).</summary>
    public static string NameKey(string npc)
    {
        string best = "";
        foreach (string tok in (npc ?? "").Split(' ', '`', '\'', '-', '.'))
        {
            string t = new(tok.Where(char.IsLetter).ToArray());
            if (t.Length > best.Length) best = t;
        }
        return best.Length >= 4 ? best.ToLowerInvariant() : "";
    }

    public sealed record NpcHit(double X, double Y, double NameX, double NameY, string Matched);

    /// <summary>
    /// Find the NPC by its overhead name and return the learned body point (nameplate + offset),
    /// all normalized to the window. Null when the name can't be read this instant — the caller
    /// falls back to the fixed pick and says so.
    /// </summary>
    public static async Task<NpcHit?> FindNpcAsync(IntPtr hwnd, QuestScript script)
    {
        string key = NameKey(script.Npc);
        if (key.Length == 0 || !script.NpcAnchorLearned) return null;
        if (WindowRect(hwnd) is not (double wx, double wy, double ww, double wh)) return null;

        List<FoundText> found;
        try { found = await ScreenText.ReadAsync(hwnd); }
        catch { return null; }

        NpcHit? best = null;
        double bestScore = double.MaxValue;
        foreach (FoundText f in found)
        {
            string t = new(f.Text.Where(char.IsLetter).ToArray());
            if (!t.ToLowerInvariant().Contains(key)) continue;
            double nx = (f.X - wx) / ww, ny = (f.Y - wy) / wh;
            if (ny > 0.72) continue;                      // chat log echoes the name constantly — ignore the bottom band
            // Prefer the hit nearest where the nameplate was when the anchor was learned: the
            // target window also prints the name, but it never moves, so it only wins when the
            // real nameplate isn't on screen — and then the learned offset is wrong anyway.
            double dx = nx - script.NpcNameX, dy = ny - script.NpcNameY;
            double score = dx * dx + dy * dy;
            if (score < bestScore)
            {
                bestScore = score;
                best = new NpcHit(nx + script.NpcDx, ny + script.NpcDy, nx, ny, f.Text.Trim());
            }
        }
        if (best is null) return null;
        // Too far from where the nameplate was learned = probably the target window, not the NPC.
        if (Math.Sqrt(bestScore) > NpcMaxDrift) return null;
        // A body point off the window means the match was junk (or the NPC is half off screen).
        if (best.X is <= 0.01 or >= 0.99 || best.Y is <= 0.01 or >= 0.99) return null;
        return best;
    }
}
