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
        lock (_sync) { _refs = null; }                     // new region → old fingerprints are void
        PxPerDeg = 0; Mirror = 0; _pairs.Clear();
        Save();
    }

    /// <summary>Grab just the compass strip and reduce it to a z-scored column-mean vector.</summary>
    private float[]? Signature()
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
            // z-score so brightness/contrast shifts don't matter
            double mean = sig.Average(v => (double)v);
            double var = sig.Sum(v => (v - mean) * (v - mean)) / SigW;
            float sd = (float)Math.Sqrt(Math.Max(var, 1e-6));
            for (int i = 0; i < SigW; i++) sig[i] = (float)((sig[i] - mean) / sd);
            return sig;
        }
        catch { return null; }
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

    /// <summary>Spin the character a full circle with mouselook, sampling the strip; the loop
    /// closure fixes px-per-degree exactly and produces the reference fingerprints.</summary>
    public async Task<string> SpinCalibrate(CancellationToken ct)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero) return "No game window — click Target EverQuest first.";
        if (!HasRect) return "Pick the compass region first.";
        if (!GetWindowRect(h, out RECT r)) return "Couldn't measure the game window.";

        SetForegroundWindow(h);
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        await Task.Delay(400, ct);

        float[]? start = Signature();
        if (start is null) return "Couldn't capture the compass strip — is the region on screen?";

        var samples = new List<(double px, float[] sig)> { (0, start) };
        double cum = 0, total = -1;
        int closeHits = 0;
        InputProbe.MouseButtonEvent(MouseBtn.Right, true);
        try
        {
            while (cum < 5600 && !ct.IsCancellationRequested)
            {
                InputProbe.MouseMoveRelative(24, 0);
                cum += 24;
                await Task.Delay(30, ct);
                if (((int)cum / 24) % 2 == 0) continue;                  // sample every ~48 px
                float[]? sig = Signature();
                if (sig is null) { closeHits = 0; continue; }
                samples.Add((cum, sig));
                if (cum < 1000) continue;                                // can't be home yet
                if (Corr(sig, start) > 0.90)
                {
                    if (++closeHits >= 2) { total = cum; break; }
                }
                else closeHits = 0;
            }
        }
        finally { InputProbe.MouseButtonEvent(MouseBtn.Right, false); }

        if (total < 0)
            return "Spun a long way but never saw the start again — make the compass fully opaque, "
                 + "check the region box, and try again.";

        PxPerDeg = total / 360.0;
        int n = (int)Math.Round(360.0 / StepDeg);
        var refs = new float[n][];
        foreach (int k in Enumerable.Range(0, n))
        {
            double targetPx = k * StepDeg * PxPerDeg;
            refs[k] = samples.OrderBy(s => Math.Abs(s.px - targetPx)).First().sig;
        }
        lock (_sync) _refs = refs;
        Mirror = 0; _pairs.Clear();                                      // mapping re-learns on the next run
        Save();
        return $"Calibrated: 360° = {total:0} drag px ({PxPerDeg:0.00} px/°), {n} reference slices. "
             + "Heading reads are live — the loc-space mapping locks in during the first minute of hunting.";
    }

    // ---------------- persistence ----------------

    private sealed class Dto
    {
        public double rx { get; set; } public double ry { get; set; }
        public double rw { get; set; } public double rh { get; set; }
        public double pxPerDeg { get; set; }
        public double offset { get; set; } public int mirror { get; set; }
        public List<float[]>? refs { get; set; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            float[][]? refs; lock (_sync) refs = _refs;
            var dto = new Dto { rx = RX, ry = RY, rw = RW, rh = RH, pxPerDeg = PxPerDeg, offset = OffsetDeg, mirror = Mirror, refs = refs?.ToList() };
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
            if (dto.refs is { Count: >= 60 } list && list.All(a => a.Length == SigW))
                lock (_sync) _refs = list.ToArray();
        }
        catch { /* corrupted file = start fresh */ }
    }
}
