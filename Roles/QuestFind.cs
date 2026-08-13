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
    {
        int x0 = (int)(nx * frame.Width), y0 = (int)(ny * frame.Height);
        int w = Math.Max(SigGrid, (int)(nw * frame.Width)), h = Math.Max(SigGrid, (int)(nh * frame.Height));
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

        var sig = new double[SigGrid * SigGrid * 3];
        var counts = new int[SigGrid * SigGrid];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int gy = Math.Min(SigGrid - 1, y * SigGrid / h);
            for (int x = 0; x < w; x++)
            {
                int cell = gy * SigGrid + Math.Min(SigGrid - 1, x * SigGrid / w);
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

        double bw = step.IconW, bh = step.IconH;
        double stepX = Math.Max(bw / 2, 0.002), stepY = Math.Max(bh / 2, 0.002);
        IconHit? best = null;
        int guard = 0;
        for (double y = script.BagY; y + bh <= script.BagY + script.BagH + 1e-9; y += stepY)
            for (double x = script.BagX; x + bw <= script.BagX + script.BagW + 1e-9; x += stepX)
            {
                if (++guard > 20000) return best;              // a absurd bag/box ratio must not hang the gesture
                double[]? sig = SigFromRegion(frame, x, y, bw, bh);
                if (sig is null) continue;
                double dist = SigDistance(sig, step.IconSig);
                if (best is null || dist < best.Dist)
                    best = new IconHit(x + bw / 2, y + bh / 2, -1, -1, dist);
            }
        return best;
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
