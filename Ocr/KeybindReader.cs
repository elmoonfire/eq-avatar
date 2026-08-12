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
/// Reads the game's Controls → Key binds screen with Windows OCR and turns the visible rows
/// into KeyBind entries. Geometry does the parsing: words are clustered into rows, and each
/// row is split at its big horizontal gaps — left chunk = the action label, following chunks
/// = primary / alternate keys. A row with no big gap is treated as a category header for the
/// rows that follow. One call reads one visible page; the user scrolls and captures again,
/// and KeyMapStore.Merge stitches the passes together.
/// </summary>
public static class KeybindReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private static OcrEngine? _engine;
    private static OcrEngine? Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

    private readonly record struct Tok(string Text, double X0, double X1, double Cy, double H);

    /// <summary>Words we never accept as a bind's action label (column titles, window chrome).</summary>
    private static readonly Regex Noise = new(
        @"^(primary|alternate|alt|action|command|key\s*binds?|keyboard|mouse|controls|options|search|filter|page|reset|defaults?|accept|cancel|ok|apply)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<List<KeyBind>> ReadAsync(IntPtr hwnd)
    {
        var binds = new List<KeyBind>();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return binds;
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        if (w < 100 || h < 100) return binds;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        OcrEngine? engine = Engine;
        if (engine is null) return binds;

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
        if (toks.Count == 0) return binds;

        // ---- cluster words into visual rows (OCR often splits one row into several lines) ----
        double rowTol = Math.Max(6, toks.Average(t => t.H) * 0.62);
        var rows = new List<List<Tok>>();
        foreach (var t in toks.OrderBy(t => t.Cy))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row.Average(x => x.Cy) - t.Cy) <= rowTol) row.Add(t);
            else rows.Add(new List<Tok> { t });
        }

        // ---- split each row at its large horizontal gaps: label | primary | alternate ----
        double gapMin = Math.Max(34, w * 0.02);
        string category = "";
        foreach (var row in rows)
        {
            var ts = row.OrderBy(t => t.X0).ToList();
            var chunks = new List<List<Tok>> { new() { ts[0] } };
            for (int i = 1; i < ts.Count; i++)
            {
                if (ts[i].X0 - chunks[^1][^1].X1 >= gapMin) chunks.Add(new List<Tok>());
                chunks[^1].Add(ts[i]);
            }
            string Text(List<Tok> c) => string.Join(" ", c.Select(t => t.Text)).Trim();

            if (chunks.Count == 1)
            {
                // no key column → likely a category header for the rows below
                string t = Text(chunks[0]);
                if (t.Length is >= 3 and <= 32 && !Noise.IsMatch(t) && !t.Any(char.IsDigit) && t.Count(c => c == ' ') <= 3)
                    category = t;
                continue;
            }

            string action = Text(chunks[0]);
            if (action.Length < 2 || Noise.IsMatch(action)) continue;
            string primary = Text(chunks[1]);
            string alternate = chunks.Count > 2 ? string.Join(" / ", chunks.Skip(2).Select(Text)) : "";
            if (primary.Length == 0 && alternate.Length == 0) continue;
            if (Noise.IsMatch(primary)) continue;                       // "Primary | Alternate" title row

            binds.Add(new KeyBind
            {
                Category = category,
                Action = action,
                Primary = primary == "-" ? "" : primary,
                Alternate = alternate == "-" ? "" : alternate,
            });
        }
        return binds;
    }
}
