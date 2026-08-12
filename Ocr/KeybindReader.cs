using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using EQAvatar.Spike.Input;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Reads the game's Controls -> Key binds screen with Windows OCR. Geometry does the work:
/// words are clustered into rows, each row is split at its big horizontal gaps, and the key
/// chunks are then assigned to COLUMNS by x-position (measured once across the whole page).
/// Columns matter twice over — they tell an empty primary from an empty secondary, and they
/// give the applier a screen point to click for a cell with no text in it at all.
/// One call reads one visible page; the caller scrolls and calls again.
/// </summary>
public static class KeybindReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private static OcrEngine? _engine;
    private static OcrEngine? Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

    private readonly record struct Tok(string Text, double X0, double X1, double Cy, double H);

    /// <summary>One parsed row, with the SCREEN point of each key cell so it can be clicked.</summary>
    public sealed class KeyRow
    {
        public KeyBind Bind { get; init; } = new();
        public int RowY;                    // screen Y centre of the row
        public int PrimaryX, AlternateX;    // screen X centre of each key column (0 = unknown)
        public bool HasPrimaryCell => PrimaryX > 0;
        public bool HasAlternateCell => AlternateX > 0;
    }

    /// <summary>One capture: the rows found plus the SCREEN rectangle they occupied, so the
    /// auto-capture loop knows exactly where to put the cursor when it scrolls the list.</summary>
    public sealed class KeybindPage
    {
        public List<KeyRow> Rows { get; init; } = new();
        public List<KeyBind> Binds => Rows.Select(r => r.Bind).ToList();
        public int RegionX, RegionY, RegionW, RegionH;
        public bool HasRegion => RegionW > 20 && RegionH > 20;
        public (int X, int Y) Center => (RegionX + RegionW / 2, RegionY + RegionH / 2);

        public KeyRow? FindRow(string action) => Rows.FirstOrDefault(
            r => string.Equals(Normalize(r.Bind.Action), Normalize(action), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>OCR reads wobble on punctuation and spacing — compare action names loosely.</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Convenience wrapper kept for callers that only want the binds.</summary>
    public static async Task<List<KeyBind>> ReadAsync(IntPtr hwnd) => (await ReadPageAsync(hwnd)).Binds;

    /// <summary>Words we never accept as a bind's action label (column titles, window chrome).</summary>
    private static readonly Regex Noise = new(
        @"^(primary|secondary|alternate|alt|action|command|key\s*binds?|keyboard|mouse|controls|options|search|filter|page|category|all|reset|defaults?|accept|cancel|ok|apply|done|close)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<KeybindPage> ReadPageAsync(IntPtr hwnd)
    {
        var page = new KeybindPage();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return page;
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        if (w < 100 || h < 100) return page;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        OcrEngine? engine = Engine;
        if (engine is null) return page;

        SoftwareBitmap sw;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            sw = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        var toks = new List<Tok>();
        using (sw)
        {
            OcrResult ocr = await engine.RecognizeAsync(sw);
            foreach (OcrLine line in ocr.Lines)
                foreach (OcrWord word in line.Words)
                {
                    var b = word.BoundingRect;
                    if (b.Height < 6) continue;                         // speckle
                    toks.Add(new Tok(word.Text, b.X, b.X + b.Width, b.Y + b.Height / 2.0, b.Height));
                }
        }
        if (toks.Count == 0) return page;

        // ---- cluster words into visual rows (OCR often splits one row into several lines) ----
        double rowTol = Math.Max(6, toks.Average(t => t.H) * 0.62);
        var rows = new List<List<Tok>>();
        foreach (var t in toks.OrderBy(t => t.Cy))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row.Average(x => x.Cy) - t.Cy) <= rowTol) row.Add(t);
            else rows.Add(new List<Tok> { t });
        }

        // ---- split each row at its large horizontal gaps ----
        double gapMin = Math.Max(34, w * 0.02);
        var parsed = new List<(List<List<Tok>> chunks, double cy)>();
        foreach (var row in rows)
        {
            var ts = row.OrderBy(t => t.X0).ToList();
            var chunks = new List<List<Tok>> { new() { ts[0] } };
            for (int i = 1; i < ts.Count; i++)
            {
                if (ts[i].X0 - chunks[^1][^1].X1 >= gapMin) chunks.Add(new List<Tok>());
                chunks[^1].Add(ts[i]);
            }
            parsed.Add((chunks, ts.Average(t => t.Cy)));
        }

        static string Text(List<Tok> c) => string.Join(" ", c.Select(t => t.Text)).Trim();
        static double Mid(List<Tok> c) => (c[0].X0 + c[^1].X1) / 2.0;

        // ---- measure the key COLUMNS once, from the rows that clearly have two of them ----
        var firstKeyX = new List<double>();
        var secondKeyX = new List<double>();
        foreach (var (chunks, _) in parsed)
        {
            if (chunks.Count >= 2 && !Noise.IsMatch(Text(chunks[0]))) firstKeyX.Add(Mid(chunks[1]));
            if (chunks.Count >= 3) secondKeyX.Add(Mid(chunks[2]));
        }
        double colA = Median(firstKeyX), colB = Median(secondKeyX);
        bool twoCols = secondKeyX.Count >= 2 && colB > colA + gapMin * 0.5;

        string category = "";
        int accepted = 0;
        double minY = 0, maxY = 0, minX = 0, maxX = 0;

        foreach (var (chunks, cy) in parsed)
        {
            if (chunks.Count == 1)
            {
                // no key column -> likely a category header for the rows below
                string t = Text(chunks[0]);
                if (t.Length is >= 3 and <= 32 && !Noise.IsMatch(t) && !t.Any(char.IsDigit) && t.Count(c => c == ' ') <= 3)
                    category = t;
                continue;
            }

            string action = Text(chunks[0]);
            if (action.Length < 2 || Noise.IsMatch(action)) continue;

            string primary = "", alternate = "";
            foreach (var c in chunks.Skip(1))
            {
                string val = Text(c);
                if (val.Length == 0 || Noise.IsMatch(val)) continue;
                if (val == "-" || val == "—") continue;             // explicit "unbound" marker
                double mid = Mid(c);
                bool toAlt = twoCols && Math.Abs(mid - colB) < Math.Abs(mid - colA);
                if (toAlt) alternate = alternate.Length == 0 ? val : alternate + " " + val;
                else primary = primary.Length == 0 ? val : primary + " " + val;
            }
            if (primary.Length == 0 && alternate.Length == 0) continue;

            var rts = chunks.SelectMany(c => c).ToList();
            double rowTop = cy - rts[0].H, rowBot = cy + rts[0].H;
            if (accepted == 0) { minY = rowTop; maxY = rowBot; minX = rts.Min(t => t.X0); maxX = rts.Max(t => t.X1); }
            else
            {
                minY = Math.Min(minY, rowTop); maxY = Math.Max(maxY, rowBot);
                minX = Math.Min(minX, rts.Min(t => t.X0)); maxX = Math.Max(maxX, rts.Max(t => t.X1));
            }
            accepted++;

            page.Rows.Add(new KeyRow
            {
                Bind = new KeyBind { Category = category, Action = action, Primary = primary, Alternate = alternate },
                RowY = r.Top + (int)cy,
                PrimaryX = colA > 0 ? r.Left + (int)colA : 0,
                AlternateX = twoCols ? r.Left + (int)colB : 0,
            });
        }

        if (accepted > 0)
        {
            page.RegionX = r.Left + (int)minX;
            page.RegionY = r.Top + (int)minY;
            page.RegionW = (int)(maxX - minX);
            page.RegionH = (int)(maxY - minY);
        }
        return page;
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }
}
