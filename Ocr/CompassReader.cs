using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Reads the character's FACING off the in-game compass strip — the piece the log can't give us.
///
/// How it works: the user drags a box over the compass once (normalized to the game window, so
/// moving/resizing the window is fine). A one-time SPIN CALIBRATION turns the character a full
/// circle with mouselook while sampling the strip; the loop closure (the strip matching its
/// starting image again) tells us exactly how many drag-pixels make 360°, and the samples become
/// ~180 reference "fingerprints" — one per 2°. At runtime a single cheap screen grab of the strip
/// is correlated against the fingerprints → heading in compass degrees, ~30 ms, no OCR.
///
/// Compass degrees are then rotated (and possibly mirrored) into loc-space angles; that mapping
/// is LEARNED automatically by comparing compass reads against headings measured from /loc
/// movement segments, so no assumptions about which way EQ's axes point are baked in.
///
/// The compass works best fully opaque — a transparent one lets the world bleed through the
/// strip and muddies the fingerprints (calibration will say so via low closure confidence).
/// </summary>
public sealed class CompassReader
{
    private const int SigW = 96;          // fingerprint resolution (columns)
    private const double StepDeg = 2.0;   // reference spacing

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    private readonly Func<IntPtr> _hwnd;
    private readonly object _sync = new();
    private float[][]? _refs;             // [k] = fingerprint at k*StepDeg (spin-direction degrees)
    private float[]? _mean;               // average raw strip — the STATIC parts (frame, needle,
                                          // tick marks); subtracted so only the moving tape counts

    /// <summary>Normalized compass rect within the game window (0..1 of width/height).</summary>
    public double RX, RY, RW, RH;
    public double PxPerDeg { get; private set; }

    // compass→loc-space mapping, learned from movement: locDeg = OffsetDeg + Mirror * compassDeg
    public double OffsetDeg { get; private set; } = 90;
    public int Mirror { get; private set; }               // 0 = not learned yet (assume +1)
    private readonly List<(double loc, double comp)> _pairs = new();

    public bool HasRect => RW > 0.01 && RH > 0.004;
    public bool Ready => HasRect && _refs is { Length: >= 60 } && PxPerDeg > 0.5;
    public bool MappingLearned => Mirror != 0;

    public CompassReader(Func<IntPtr> hwnd) { _hwnd = hwnd; Load(); }

    private static string FilePath => Path.Combine(AppSettings.Dir, "compass.json");

    // ---------------- capture + fingerprints ----------------

    /// <summary>Full game-window frame (screen capture) — used by the region picker.</summary>
    public Bitmap? CaptureFrame()
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || !GetWindowRect(h, out RECT r)) return null;
        int w = Math.Max(1, r.Right - r.Left), ht = Math.Max(1, r.Bottom - r.Top);
        try
        {
            var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, ht), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }

    public void SetRect(double nx, double ny, double nw, double nh)
    {
        RX = nx; RY = ny; RW = nw; RH = nh;
        lock (_sync) { _refs = null; _mean = null; }       // new region → old fingerprints are void
        PxPerDeg = 0; Mirror = 0; _pairs.Clear();
        Save();
    }

    /// <summary>Grab the compass strip as a RAW column-brightness vector (no normalization).</summary>
    private float[]? RawStrip()
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || !HasRect || !GetWindowRect(h, out RECT r)) return null;
        int winW = r.Right - r.Left, winH = r.Bottom - r.Top;
        int cx = r.Left + (int)(RX * winW), cy = r.Top + (int)(RY * winH);
        int cw = Math.Max(8, (int)(RW * winW)), ch = Math.Max(4, (int)(RH * winH));
        try
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);

            BitmapData d = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var cols = new double[cw];
            var buf = new byte[d.Stride * ch];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(d);
            for (int y = 0; y < ch; y++)
            {
                int row = y * d.Stride;
                for (int x = 0; x < cw; x++)
                {
                    int px = row + x * 4;
                    cols[x] += 0.114 * buf[px] + 0.587 * buf[px + 1] + 0.299 * buf[px + 2];
                }
            }
            var sig = new float[SigW];
            for (int i = 0; i < SigW; i++)
            {
                double src = (double)i * cw / SigW;
                int a = (int)src, b = Math.Min(cw - 1, a + 1);
                double f = src - a;
                sig[i] = (float)((cols[a] * (1 - f) + cols[b] * f) / ch);
            }
            return sig;
        }
        catch { return null; }
    }

    /// <summary>Static-cancelled, z-scored fingerprint: subtract the calibration-time average
    /// strip (frame + needle + tick marks all cancel), leaving only the moving tape.</summary>
    private static float[] Normalize(float[] raw, float[]? mean)
    {
        var sig = new float[SigW];
        for (int i = 0; i < SigW; i++) sig[i] = raw[i] - (mean?[i] ?? 0);
        double m = sig.Average(v => (double)v);
        double var = sig.Sum(v => (v - m) * (v - m)) / SigW;
        float sd = (float)Math.Sqrt(Math.Max(var, 1e-6));
        for (int i = 0; i < SigW; i++) sig[i] = (float)((sig[i] - m) / sd);
        return sig;
    }

    private float[]? Signature()
    {
        float[]? raw = RawStrip();
        if (raw is null) return null;
        float[]? mean; lock (_sync) mean = _mean;
        return Normalize(raw, mean);
    }

    private static float Corr(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < SigW; i++) s += a[i] * b[i];
        return s / SigW;
    }

    // ---------------- runtime reads ----------------

    /// <summary>Heading in COMPASS (spin-direction) degrees, or null if unsure.</summary>
    public double? ReadRawDeg()
    {
        float[][]? refs;
        lock (_sync) refs = _refs;
        if (refs is null || !HasRect) return null;
        float[]? sig = Signature();
        if (sig is null) return null;

        int best = -1; float bestC = -2, second = -2;
        for (int k = 0; k < refs.Length; k++)
        {
            float c = Corr(sig, refs[k]);
            if (c > bestC) { second = bestC; bestC = c; best = k; }
            else if (c > second && Math.Min(Math.Abs(k - best), refs.Length - Math.Abs(k - best)) > 3) second = c;
        }
        if (best < 0 || bestC < 0.45 || bestC - second < 0.04) return null;   // world bleed / bad rect

        // parabolic refinement between the best slice and its neighbours
        int n = refs.Length;
        float cl = Corr(sig, refs[(best - 1 + n) % n]), cr = Corr(sig, refs[(best + 1) % n]);
        double denom = cl - 2 * bestC + cr;
        double frac = Math.Abs(denom) < 1e-6 ? 0 : Math.Clamp(0.5 * (cl - cr) / denom, -0.5, 0.5);
        double deg = (best + frac) * (360.0 / n);
        return (deg % 360 + 360) % 360;
    }

    /// <summary>Heading in LOC-space degrees (atan2 convention the hunt math uses), or null.</summary>
    public double? ReadLocDeg()
    {
        double? raw = ReadRawDeg();
        if (raw is not double c) return null;
        int m = Mirror == 0 ? 1 : Mirror;
        double d = OffsetDeg + m * c;
        return (d % 360 + 360) % 360;
    }

    /// <summary>Feed a heading measured from a /loc movement segment; learns the rotation/mirror
    /// between compass degrees and loc-space, then keeps refining it.</summary>
    public void LearnFromMovement(double locDeg)
    {
        double? raw = ReadRawDeg();
        if (raw is not double comp) return;
        _pairs.Add((locDeg, comp));
        if (_pairs.Count > 10) _pairs.RemoveAt(0);
        if (_pairs.Count < 3) return;

        (double spread, double mean) Fit(int mirror)
        {
            double sx = 0, sy = 0;
            foreach ((double loc, double cc) in _pairs)
            {
                double off = (loc - mirror * cc) * Math.PI / 180.0;
                sx += Math.Cos(off); sy += Math.Sin(off);
            }
            double r = Math.Sqrt(sx * sx + sy * sy) / _pairs.Count;      // 1 = perfectly consistent
            return (1 - r, (Math.Atan2(sy, sx) * 180.0 / Math.PI % 360 + 360) % 360);
        }
        (double sPlus, double mPlus) = Fit(+1);
        (double sMinus, double mMinus) = Fit(-1);
        int newMirror = sPlus <= sMinus ? +1 : -1;
        double spread = Math.Min(sPlus, sMinus);
        if (spread > 0.25) return;                                       // too noisy to trust yet
        Mirror = newMirror;
        OffsetDeg = newMirror == 1 ? mPlus : mMinus;
        Save();
    }

    // ---------------- spin calibration ----------------

    /// <summary>Spin with mouselook while sampling the strip, then find the 360° period by
    /// AUTOCORRELATION over everything captured — no fragile "looks like the start" threshold,
    /// and no assumption about mouse sensitivity: it keeps spinning (up to ~2 full circles even
    /// at very low sensitivity) until the repeat period is unmistakable.</summary>
    public async Task<string> SpinCalibrate(CancellationToken ct)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero) return "No game window — click Target EverQuest first.";
        if (!HasRect) return "Pick the compass region first.";
        if (!GetWindowRect(h, out RECT r)) return "Couldn't measure the game window.";

        SetForegroundWindow(h);
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        await Task.Delay(400, ct);

        const int StepPx = 24;                 // drag per tick; a raw strip is sampled EVERY tick
        const int MaxPx = 16000;               // ≥2 circles even at ~20 px/° sensitivity
        var raws = new List<float[]>();
        {
            float[]? first = RawStrip();
            if (first is null) return "Couldn't capture the compass strip — is the region on screen?";
            raws.Add(first);
        }

        double bestP = -1, bestScore = -1;
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        try
        {
            while (raws.Count * StepPx < MaxPx && !ct.IsCancellationRequested)
            {
                InputProbe.MouseMoveRelative(StepPx, 0);
                await Task.Delay(30, ct);
                float[]? raw = RawStrip();
                raws.Add(raw ?? raws[^1]);

                // Periodic early check: do we already contain 2.2+ copies of a clear period?
                if (raws.Count % 16 == 0 && raws.Count * StepPx > 1600)
                {
                    (double p, double s) = FindPeriod(raws, StepPx);
                    if (s > 0.62 && p > 0 && raws.Count * StepPx >= p * 2.2) { bestP = p; bestScore = s; break; }
                }
            }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); }

        if (bestP < 0) (bestP, bestScore) = FindPeriod(raws, StepPx);

        // Build the static-cancelling mean over (ideally) whole periods so it's unbiased.
        int meanCount = bestP > 0 ? Math.Min(raws.Count, (int)(Math.Floor(raws.Count * StepPx / bestP) * bestP / StepPx)) : raws.Count;
        var mean = new float[SigW];
        for (int i = 0; i < Math.Max(1, meanCount); i++)
            for (int j = 0; j < SigW; j++) mean[j] += raws[i][j];
        for (int j = 0; j < SigW; j++) mean[j] /= Math.Max(1, meanCount);

        // Did the strip actually move? (Wrong box → static UI → near-zero variance after mean-cancel.)
        double drift = 0;
        for (int i = 0; i < raws.Count; i += Math.Max(1, raws.Count / 40))
            drift += raws[i].Select((v, j) => Math.Abs(v - mean[j])).Average();
        drift /= 40;
        if (drift < 1.0)
            return "The region never changed while turning — the box doesn't seem to be on the moving "
                 + "compass tape. Re-pick it (the strip with the scrolling N/E/S/W letters).";

        if (bestP <= 0 || bestScore < 0.45)
            return $"Spun {raws.Count * StepPx} px but the repeat pattern was weak (similarity {bestScore:0.00}) — "
                 + "make the compass FULLY opaque, keep the box tight on the tape, and try again standing still.";

        PxPerDeg = bestP / 360.0;

        // References: phase-average every sample across all captured cycles (noise-cancelling).
        int n = (int)Math.Round(360.0 / StepDeg);
        var acc = new float[n][];
        var cnt = new int[n];
        for (int k = 0; k < n; k++) acc[k] = new float[SigW];
        for (int i = 0; i < raws.Count; i++)
        {
            double deg = i * StepPx / PxPerDeg % 360.0;
            int k = (int)Math.Round(deg / StepDeg) % n;
            float[] norm = Normalize(raws[i], mean);
            for (int j = 0; j < SigW; j++) acc[k][j] += norm[j];
            cnt[k]++;
        }
        var refs = new float[n][];
        for (int k = 0; k < n; k++)
        {
            if (cnt[k] == 0) continue;                                   // gaps filled below
            for (int j = 0; j < SigW; j++) acc[k][j] /= cnt[k];
            refs[k] = Normalize(acc[k], null);                           // re-normalize the average
        }
        for (int k = 0; k < n; k++)
        {
            if (refs[k] != null) continue;                               // rare gap → nearest filled slice
            for (int d = 1; d < n; d++)
            {
                float[]? near = refs[(k + d) % n] ?? refs[(k - d + n) % n];
                if (near != null) { refs[k] = near; break; }
            }
            refs[k] ??= new float[SigW];
        }
        lock (_sync) { _refs = refs; _mean = mean; }
        Mirror = 0; _pairs.Clear();                                      // mapping re-learns on the next run
        Save();
        double circles = raws.Count * StepPx / bestP;
        return $"Calibrated: 360° = {bestP:0} drag px ({PxPerDeg:0.00} px/°), repeat similarity {bestScore:0.00} "
             + $"over {circles:0.0} circles, {n} reference slices. Heading reads are live — the loc-space "
             + "mapping locks in during the first minute of hunting.";
    }

    /// <summary>Autocorrelation over the sampled strips: the drag-pixel period whose shifted
    /// copies of the (static-cancelled) sequence agree best = one full 360° turn.</summary>
    private static (double period, double score) FindPeriod(List<float[]> raws, int stepPx)
    {
        int count = raws.Count;
        if (count < 40) return (-1, -1);
        // local mean for static cancellation (recomputed here so early checks work mid-spin)
        var mean = new float[SigW];
        foreach (float[] raw in raws) for (int j = 0; j < SigW; j++) mean[j] += raw[j];
        for (int j = 0; j < SigW; j++) mean[j] /= count;
        var norm = new float[count][];
        for (int i = 0; i < count; i++) norm[i] = Normalize(raws[i], mean);

        int minLag = Math.Max(8, 700 / stepPx);                          // ≥700 px per circle
        int maxLag = Math.Min(count - 12, 12000 / stepPx);               // ≤12000 px per circle
        if (maxLag <= minLag) return (-1, -1);
        double bestScore = -1; int bestLag = -1;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0; int pairs = 0;
            int stride = Math.Max(1, (count - lag) / 60);                // ~60 pairs is plenty
            for (int i = 0; i + lag < count; i += stride) { s += Corr(norm[i], norm[i + lag]); pairs++; }
            if (pairs < 12) continue;
            s /= pairs;
            if (s > bestScore) { bestScore = s; bestLag = lag; }
        }
        if (bestLag < 0) return (-1, -1);
        // Reject harmonics: half the best lag scoring nearly as well means the true period is the half.
        int half = bestLag / 2;
        if (half >= minLag)
        {
            double sHalf = 0; int p2 = 0;
            int stride = Math.Max(1, (count - half) / 60);
            for (int i = 0; i + half < count; i += stride) { sHalf += Corr(norm[i], norm[i + half]); p2++; }
            if (p2 >= 12 && sHalf / p2 > bestScore - 0.05) { bestLag = half; bestScore = Math.Max(bestScore, sHalf / p2); }
        }
        return (bestLag * (double)stepPx, bestScore);
    }

    // ---------------- persistence ----------------

    private sealed class Dto
    {
        public double rx { get; set; } public double ry { get; set; }
        public double rw { get; set; } public double rh { get; set; }
        public double pxPerDeg { get; set; }
        public double offset { get; set; } public int mirror { get; set; }
        public float[]? mean { get; set; }
        public List<float[]>? refs { get; set; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            float[][]? refs; float[]? mean;
            lock (_sync) { refs = _refs; mean = _mean; }
            var dto = new Dto { rx = RX, ry = RY, rw = RW, rh = RH, pxPerDeg = PxPerDeg, offset = OffsetDeg, mirror = Mirror, mean = mean, refs = refs?.ToList() };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto));
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            Dto? dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto is null) return;
            RX = dto.rx; RY = dto.ry; RW = dto.rw; RH = dto.rh;
            PxPerDeg = dto.pxPerDeg; OffsetDeg = dto.offset; Mirror = dto.mirror;
            if (dto.refs is { Count: >= 60 } list && list.All(a => a.Length == SigW)
                && dto.mean is { Length: SigW } m)                       // pre-0.9.17 files lack the mean → recalibrate
                lock (_sync) { _refs = list.ToArray(); _mean = m; }
        }
        catch { /* corrupted file = start fresh */ }
    }
}
