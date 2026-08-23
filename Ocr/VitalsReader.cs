using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Reads the character's CURRENT health and mana off the HUD bars — the other thing the log
/// simply never tells us. (EQ's log has no HP/mana line at all, which is why "rest" used to be a
/// blind timer.)
///
/// How it works: the user drags a box over the HP bar once, and again over the mana bar, while
/// both are FULL. We remember the box normalized to the game window (so moving or resizing the
/// window doesn't break it) and we remember what "filled" looks like — the average colour of the
/// bar's interior at that moment. At runtime we grab the strip, walk it left→right, and the
/// fraction of columns that still match the learned fill colour IS the percentage. No OCR, no
/// per-UI assumptions, ~5 ms a read.
///
/// Deliberately colour-learned rather than colour-hard-coded: EQ UIs recolour these bars freely,
/// custom UIs even more so, and a red HP bar under a blue mana bar would defeat any fixed table.
/// The one rule the user has to follow is "be at full when you pick", which the picker says.
///
/// Bars that drain right→left, or vertically, are handled too. The box's own shape says which
/// axis the bar runs along (a health bar is far longer than it is thick), and along that axis we
/// take the longer of the two end-anchored runs — a partially drained bar always keeps its fill
/// against one end. Picking the axis by shape rather than by "longest run anywhere" matters: any
/// horizontal bar over half full has a full-height run somewhere, which would read as 100%.
/// </summary>
public sealed class VitalsReader
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>One bar: where it is on screen and what a full one looks like.</summary>
    public sealed class Bar
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        /// <summary>Learned "filled" colour (0-255 each), captured while the bar was full.</summary>
        public double R { get; set; } = -1;
        public double G { get; set; } = -1;
        public double B { get; set; } = -1;
        /// <summary>Thresholds are deliberately tiny: a vertical mana pip in a custom UI can be a
        /// handful of pixels wide on a 4K screen and still be perfectly readable.</summary>
        public bool Set => W > 0.0015 && H > 0.0008 && R >= 0;
    }

    /// <summary>The target window: a region plus a fingerprint of what it looks like when a target
    /// IS selected. Stored as a coarse grid of average colours rather than one colour, because the
    /// window is a mix of frame, background, name text and a health bar.</summary>
    public sealed class Region
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        /// <summary>Cols*Rows*3 averaged RGB values, captured with a target up.</summary>
        public double[]? Sig { get; set; }
        public bool Set => W > 0.004 && H > 0.004 && Sig is { Length: SigCols * SigRows * 3 };
    }

    /// <summary>Fingerprint grid. Coarse on purpose: averaging a cell washes out which mob's name
    /// is written in it, so one target looks like any other, while the window's frame and
    /// background — the parts that actually say "a window is drawn here" — stay put.</summary>
    private const int SigCols = 8, SigRows = 4;

    public Bar Hp { get; set; } = new();
    public Bar Mana { get; set; } = new();
    public Region Target { get; set; } = new();

    private readonly Func<IntPtr> _hwnd;

    public VitalsReader(Func<IntPtr> hwnd) { _hwnd = hwnd; Load(); }

    /// <summary>True once at least one bar is usable — rest gating turns itself on from here.</summary>
    public bool Ready => Hp.Set || Mana.Set;

    // ---------------- capture ----------------

    /// <summary>Full game-window frame, for the region picker.</summary>
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

    /// <summary>
    /// The AVERAGE colour of one small region of the game window, right now.
    ///
    /// A region blit rather than a whole frame, because the caller samples it repeatedly: eight
    /// full-window captures to answer one question would allocate sixty megabytes to look at a
    /// few hundred pixels. One number per sample is all a "did this change" test needs.
    /// </summary>
    public (double R, double G, double B)? MeanOf(double nx, double ny, double nw, double nh)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || nw <= 0 || nh <= 0 || !GetWindowRect(h, out RECT r)) return null;
        int winW = r.Right - r.Left, winH = r.Bottom - r.Top;
        if (winW <= 0 || winH <= 0) return null;
        int cw = Math.Max(2, (int)(nw * winW)), ch = Math.Max(2, (int)(nh * winH));
        int cx = r.Left + (int)(nx * winW), cy = r.Top + (int)(ny * winH);
        try
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly,
                                        PixelFormat.Format32bppArgb);
            try
            {
                double sr = 0, sg = 0, sb = 0;
                var row = new byte[cw * 4];
                for (int y = 0; y < ch; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), row, 0, row.Length);
                    for (int x = 0; x < cw; x++)
                    { sb += row[x * 4]; sg += row[x * 4 + 1]; sr += row[x * 4 + 2]; }
                }
                double n = cw * ch;
                return (sr / n, sg / n, sb / n);
            }
            finally { bmp.UnlockBits(d); }
        }
        catch { return null; }
    }

    /// <summary>Pixels of one bar as [x][y] RGB triples, grabbed live off the screen, or null when
    /// the window is gone.</summary>
    private double[,,]? Grab(Bar bar)
    {
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || bar.W <= 0 || !GetWindowRect(h, out RECT r)) return null;
        int winW = r.Right - r.Left, winH = r.Bottom - r.Top;
        int cx = r.Left + (int)(bar.X * winW), cy = r.Top + (int)(bar.Y * winH);
        int cw = Math.Max(3, (int)(bar.W * winW)), ch = Math.Max(2, (int)(bar.H * winH));
        try
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);
            return Sample(bmp, 0, 0, cw, ch);
        }
        catch { return null; }
    }

    /// <summary>Pixels of a sub-rectangle of an already-captured bitmap.</summary>
    private static double[,,]? Sample(Bitmap bmp, int x0, int y0, int w, int h)
    {
        try
        {
            x0 = Math.Clamp(x0, 0, Math.Max(0, bmp.Width - 1));
            y0 = Math.Clamp(y0, 0, Math.Max(0, bmp.Height - 1));
            w = Math.Clamp(w, 1, bmp.Width - x0);
            h = Math.Clamp(h, 1, bmp.Height - y0);
            BitmapData d = bmp.LockBits(new Rectangle(x0, y0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(d);
            var px = new double[w, h, 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * d.Stride + x * 4;
                    px[x, y, 0] = buf[p + 2]; px[x, y, 1] = buf[p + 1]; px[x, y, 2] = buf[p];
                }
            return px;
        }
        catch { return null; }
    }

    // ---------------- learning + reading ----------------

    /// <summary>Save a picked region and learn what a full bar looks like.
    ///
    /// The colour is sampled out of <paramref name="frame"/> — the frame the user drew the box on —
    /// and NOT re-grabbed off the live screen. The picker is a big modal sitting on top of the
    /// game, and the desktop repaints underneath it asynchronously, so a fresh capture taken the
    /// instant it closes can still contain the dialog's own pixels. Learning the dialog's
    /// background as "full health" would silently make every read 0% and park the bot in a
    /// permanent rest, so we use the picture we know was clean.
    ///
    /// Returns false when the sample failed — the caller tells the user to try again.</summary>
    public bool SetBar(bool mana, double nx, double ny, double nw, double nh, Bitmap frame)
    {
        Bar bar = mana ? Mana : Hp;
        bar.X = nx; bar.Y = ny; bar.W = nw; bar.H = nh;
        bar.R = bar.G = bar.B = -1;
        double[,,]? px = Sample(frame,
            (int)(nx * frame.Width), (int)(ny * frame.Height),
            Math.Max(3, (int)(nw * frame.Width)), Math.Max(2, (int)(nh * frame.Height)));
        if (px is null) return false;

        // The learned colour is the MEDIAN of the interior, not the mean: a mean is dragged off
        // by the border and by any tick marks or gloss overlay drawn on top of the bar.
        int w = px.GetLength(0), h = px.GetLength(1);
        int x0 = w >= 7 ? 1 : 0, x1 = w >= 7 ? w - 1 : w;
        int y0 = h >= 5 ? 1 : 0, y1 = h >= 5 ? h - 1 : h;
        var rs = new double[(x1 - x0) * (y1 - y0)];
        var gs = new double[rs.Length];
        var bs = new double[rs.Length];
        int n = 0;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
            { rs[n] = px[x, y, 0]; gs[n] = px[x, y, 1]; bs[n] = px[x, y, 2]; n++; }
        Array.Sort(rs); Array.Sort(gs); Array.Sort(bs);
        bar.R = rs[n / 2]; bar.G = gs[n / 2]; bar.B = bs[n / 2];
        Save();
        return true;
    }

    /// <summary>Health as 0..1, or null when the bar isn't set up or can't be grabbed.</summary>
    public double? HealthFraction() => Fraction(Hp);

    /// <summary>Mana as 0..1, or null when the bar isn't set up or can't be grabbed.</summary>
    public double? ManaFraction() => Fraction(Mana);

    private static bool Near(double r, double g, double b, Bar bar)
    {
        // Generous per-channel tolerance: EQ bars are commonly drawn with a vertical gradient
        // and a translucent gloss, so the same "full" pixel varies by row.
        double d = Math.Abs(r - bar.R) + Math.Abs(g - bar.G) + Math.Abs(b - bar.B);
        return d <= 110;
    }

    private double? Fraction(Bar bar)
    {
        if (!bar.Set) return null;
        double[,,]? px = Grab(bar);
        if (px is null) return null;
        int w = px.GetLength(0), h = px.GetLength(1);

        // Score each column (and each row) as filled when most of its pixels match the learned
        // colour — one stray matching pixel shouldn't count a column as full.
        var colFilled = new bool[w];
        for (int x = 0; x < w; x++)
        {
            int hits = 0;
            for (int y = 0; y < h; y++) if (Near(px[x, y, 0], px[x, y, 1], px[x, y, 2], bar)) hits++;
            colFilled[x] = hits * 2 >= h;
        }
        var rowFilled = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int hits = 0;
            for (int x = 0; x < w; x++) if (Near(px[x, y, 0], px[x, y, 1], px[x, y, 2], bar)) hits++;
            rowFilled[y] = hits * 2 >= w;
        }

        int fromLeft = 0; while (fromLeft < w && colFilled[fromLeft]) fromLeft++;
        int fromRight = 0; while (fromRight < w && colFilled[w - 1 - fromRight]) fromRight++;
        int fromBottom = 0; while (fromBottom < h && rowFilled[h - 1 - fromBottom]) fromBottom++;
        int fromTop = 0; while (fromTop < h && rowFilled[fromTop]) fromTop++;

        // Along the bar's long axis, the fill is whichever end-anchored run is longer.
        double frac = w >= h
            ? Math.Max(fromLeft, fromRight) / (double)w
            : Math.Max(fromBottom, fromTop) / (double)h;
        return Math.Clamp(frac, 0, 1);
    }

    // ---------------- target window ----------------

    /// <summary>True once the target window has been picked and can be tested.</summary>
    public bool HasTargetBox => Target.Set;

    /// <summary>Learn what the target window looks like WITH a target selected, from the frame the
    /// user drew on. Returns false if the sample failed.</summary>
    public bool SetTargetBox(double nx, double ny, double nw, double nh, Bitmap frame)
    {
        Target.X = nx; Target.Y = ny; Target.W = nw; Target.H = nh;
        Target.Sig = null;
        double[,,]? px = Sample(frame,
            (int)(nx * frame.Width), (int)(ny * frame.Height),
            Math.Max(SigCols, (int)(nw * frame.Width)), Math.Max(SigRows, (int)(nh * frame.Height)));
        if (px is null) return false;
        Target.Sig = Signature(px);
        Save();
        return Target.Set;                                   // a too-small drag is a failed pick, not a saved one
    }

    /// <summary>Average colour per grid cell.</summary>
    private static double[] Signature(double[,,] px)
    {
        int w = px.GetLength(0), h = px.GetLength(1);
        var sig = new double[SigCols * SigRows * 3];
        for (int cy = 0; cy < SigRows; cy++)
            for (int cx = 0; cx < SigCols; cx++)
            {
                int x0 = cx * w / SigCols, x1 = Math.Max(x0 + 1, (cx + 1) * w / SigCols);
                int y0 = cy * h / SigRows, y1 = Math.Max(y0 + 1, (cy + 1) * h / SigRows);
                double r = 0, g = 0, b = 0;
                int n = 0;
                for (int x = x0; x < x1 && x < w; x++)
                    for (int y = y0; y < y1 && y < h; y++)
                    { r += px[x, y, 0]; g += px[x, y, 1]; b += px[x, y, 2]; n++; }
                int i = (cy * SigCols + cx) * 3;
                sig[i] = r / Math.Max(1, n); sig[i + 1] = g / Math.Max(1, n); sig[i + 2] = b / Math.Max(1, n);
            }
        return sig;
    }

    /// <summary>Fraction of grid cells that currently look like they did with a target up, or -1
    /// when the window can't be read. With no target the EQ target window isn't drawn at all, so
    /// what's underneath is the moving world — which matches a specific UI panel in very few cells.</summary>
    public double TargetMatch()
    {
        if (!Target.Set) return -1;
        IntPtr h = _hwnd();
        if (h == IntPtr.Zero || !GetWindowRect(h, out RECT r)) return -1;
        int winW = r.Right - r.Left, winH = r.Bottom - r.Top;
        int cx = r.Left + (int)(Target.X * winW), cy = r.Top + (int)(Target.Y * winH);
        int cw = Math.Max(SigCols, (int)(Target.W * winW)), ch = Math.Max(SigRows, (int)(Target.H * winH));
        try
        {
            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);
            double[,,]? px = Sample(bmp, 0, 0, cw, ch);
            if (px is null) return -1;
            double[] now = Signature(px), want = Target.Sig!;
            int hits = 0;
            for (int i = 0; i < want.Length; i += 3)
            {
                double d = Math.Abs(now[i] - want[i]) + Math.Abs(now[i + 1] - want[i + 1]) + Math.Abs(now[i + 2] - want[i + 2]);
                if (d <= 90) hits++;                          // per-cell tolerance
            }
            return hits / (double)(SigCols * SigRows);
        }
        catch { return -1; }
    }

    /// <summary>Is something targeted? Null means "can't tell" — the caller must carry on as
    /// before rather than treating uncertainty as "no target" and freezing.</summary>
    public bool? HasTarget(double needFraction)
    {
        double m = TargetMatch();
        return m < 0 ? null : m >= Math.Clamp(needFraction, 0.1, 1.0);
    }

    /// <summary>A box that's nearly square gives us no way to tell which way the bar drains, and
    /// the read degrades to a full/empty flip at the halfway point. The UI asks for a re-pick.</summary>
    public static bool TooSquare(Bar bar) => bar.Set && bar.W < 2 * bar.H && bar.H < 2 * bar.W;

    /// <summary>One-line status for the UI, e.g. "hp 94% · mana 61%".</summary>
    public string Describe()
    {
        if (!Ready) return "not set — pick your HP and mana bars";
        string one(string label, double? f) => f is double v ? $"{label} {v * 100:0}%" : $"{label} —";
        return one("hp", HealthFraction()) + " · " + one("mana", ManaFraction());
    }

    // ---------------- persistence ----------------

    private static string FilePath => Path.Combine(AppSettings.Dir, "vitals.json");

    private sealed class Saved
    {
        public Bar? Hp { get; set; }
        public Bar? Mana { get; set; }
        public Region? Target { get; set; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Hp = Hp, Mana = Mana, Target = Target },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            Saved? s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (s?.Hp != null) Hp = s.Hp;
            if (s?.Mana != null) Mana = s.Mana;
            if (s?.Target != null) Target = s.Target;
        }
        catch { }
    }
}
