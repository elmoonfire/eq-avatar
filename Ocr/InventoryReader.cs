using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Reads the in-game Inventory window (default_modern skin) off the screen — the most-trusted
/// source of character data, because it is literally what the game displays.
///
/// HOW IT WORKS NOW (rewritten 0.9.28). The old reader OCR'd the whole window and then walked
/// each OCR line looking for "label, then numbers". That could never work: the skin draws the
/// label at x=0 and the value at x=58 of a 175-wide row, and Windows OCR reliably splits that
/// gap into two separate lines — so "HP" and "1486/1486" were never on the same line and the
/// parse scored zero rows every time. Worse, adjacent value boxes ran together ("226" and
/// "1312" arriving as "2261312") and thin glyphs were lost to HDR washout ("861/861" → "8611861").
///
/// The rewrite reads the grid by GEOMETRY instead, using the client's own layout (see
/// <see cref="InventoryLayout"/>, transcribed from EQUI_InventoryWindow.xml). It OCRs the frame
/// once to find the two column headers — "Character Vitals" and "Stats and Resists" — which sit
/// exactly one column stride apart by construction. That measurement gives the origin and the
/// column offset directly in pixels; the vertical scale is seeded from it and then confirmed
/// against three probe boxes at the corners of the grid. Every one of the ~50 value boxes can
/// then be located to the pixel and read on its own. One box holds one number, which makes merged values and lost slashes impossible: current
/// and max are read separately and never needed a "/" between them in the first place.
///
/// Every crop goes through <see cref="ImagePrep"/> first, so Auto HDR washout is corrected
/// before the OCR engine sees it and nobody has to turn HDR off to use the app.
///
/// If the geometry pass comes up short (an unexpected skin, a scaled or clipped window), the
/// reader falls back to a row-band parse: cluster OCR words into rows by their Y centres and
/// pair each label with the numbers to its right. That is the same idea the old parser was
/// reaching for, done on word boxes instead of OCR's arbitrary line grouping.
/// </summary>
public sealed class InventorySnapshot
{
    public string? Name;
    public int? Level;
    public string? Classes;                                  // "PAL/MNK/ENC"
    /// <summary>Race, when the window happens to print it near the header. The inventory does not
    /// always show it; a null here means "not seen this read", never "no race".</summary>
    public string? Race;
    public readonly Dictionary<string, List<double>> Fields = new();   // label -> numbers, raw
    public long? Plat, Gold, Silver, Copper;
    public DateTime CapturedAt = DateTime.Now;
    public readonly List<string> Warnings = new();
    public string RawSeen = "";                              // every OCR line, for remote debugging

    /// <summary>UI units → screen pixels, solved from the two column headers. 1.0 at 100% scale.</summary>
    public double UiScale = 1.0;
    /// <summary>How the grid was read, for the log: "geometry", "geometry+bands" or "bands".</summary>
    public string Method = "";
    /// <summary>Folder the diagnostic dump landed in, when one was requested.</summary>
    public string? DiagPath;
    /// <summary>Screen position of the "Character Vitals" anchor — the origin every other part
    /// of the window is measured from, including the equipment grid.</summary>
    public double AnchorX, AnchorY;
    /// <summary>The 23 equipment slots, captured in the same pass. Null if the grid wasn't read.</summary>
    public EquipmentSnapshot? Equipment;

    public double? First(string label) =>
        Fields.TryGetValue(label, out List<double>? n) && n.Count > 0 ? n[0] : null;
    public double? Nth(string label, int i) =>
        Fields.TryGetValue(label, out List<double>? n) && n.Count > i ? n[i] : null;
}

public static class InventoryReader
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private static OcrEngine? _engine;
    private static OcrEngine? Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

    /// <summary>Padding around each value box, in UI units, so a slightly-off origin still lands.</summary>
    private const double BoxPad = 2.0;
    /// <summary>Value boxes are 14 units tall; blow them up to at least this many pixels to read.</summary>
    private const int MinBoxPixels = 56;

    /// <param name="diagnostics">Force the diagnostic dump. Left null it writes itself whenever a
    /// read comes back incomplete — which is exactly when the artefacts are worth having, and
    /// costs nothing on the reads that worked.</param>
    public static async Task<InventorySnapshot?> ReadAsync(IntPtr gameHwnd, Action<string>? log = null,
                                                           bool? diagnostics = null)
    {
        if (gameHwnd == IntPtr.Zero || !GetWindowRect(gameHwnd, out RECT r)) { log?.Invoke("No game window."); return null; }
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        if (Engine is null) { log?.Invoke("Windows OCR engine unavailable."); return null; }

        using var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(frame))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        // ---- Pass 1: find the two column headers on a contrast-corrected copy of the frame.
        // Scale 1.0 keeps OCR coordinates in frame space, so no mapping is needed afterwards.
        using Bitmap flat = ImagePrep.Prepare(frame, new Rectangle(0, 0, w, h), 1.0);
        OcrResult pass1 = await Recognize(flat);

        var snap = new InventorySnapshot();
        var rawLines = new List<string>();
        foreach (OcrLine l in pass1.Lines) rawLines.Add(l.Text);

        (RectangleF? vitals, RectangleF? stats) = FindAnchors(pass1);
        if (vitals is null)
        {
            snap.RawSeen = string.Join("\n", rawLines);
            log?.Invoke("Inventory window not found — open your Inventory (default_modern skin) and try again.");
            return null;
        }

        // ---- Solve the grid.
        // The two headers are exactly one column stride apart, so their measured X distance IS
        // the column offset in pixels — used directly, with no assumption about UI scale. The
        // vertical scale is then seeded from that same measurement and refined by probing, so a
        // few percent of error in the stride can't accumulate into a half-row drift by the
        // bottom of the grid.
        RectangleF v = vitals.Value;
        bool haveStats = stats is RectangleF st && st.X - v.X > 20;
        double seed = haveStats
            ? (stats!.Value.X - v.X) / InventoryLayout.ColumnStride
            : v.Height / 11.0;                                   // fallback: glyph height in a 14-unit row
        seed = Math.Clamp(seed, 0.4, 6.0);
        double colPx = haveStats ? stats!.Value.X - v.X : InventoryLayout.ColumnStride * seed;

        double originX = v.X;                                    // column 0, field X = 0
        double originY = v.Y;                                    // top of row 0's text
        if (!haveStats) snap.Warnings.Add("Only one column header found — scale is estimated, values may be off.");

        double scale = await CalibrateVerticalScale(frame, originX, originY, seed, colPx, log);
        snap.UiScale = scale;
        snap.AnchorX = originX;
        snap.AnchorY = originY;

        // ---- Pass 2: one OCR per value box.
        var diag = new StringBuilder();
        diag.AppendLine($"window {w}x{h} @ {r.Left},{r.Top}   scale {scale:0.000}   origin {originX:0},{originY:0}");
        int read = 0;
        foreach (InventoryLayout.Row row in InventoryLayout.Rows)
        {
            if (row.Fields.Length == 0 || row.Key.Length == 0) continue;
            int col = InventoryLayout.ColumnOf(row.Order), ri = InventoryLayout.RowInColumn(row.Order);
            var values = new List<double>();
            bool any = false;
            foreach (InventoryLayout.Field f in row.Fields)
            {
                Rectangle box = Box(originX, originY, scale, colPx, col, ri, f);
                (double? num, string seen) = await ReadNumberBox(frame, box);
                diag.AppendLine($"  {row.Key,-14} [{f.Index}] {box.X},{box.Y} {box.Width}x{box.Height} -> {seen,-12} = {(num?.ToString("0.##") ?? "—")}");
                if (num is double d) { values.Add(d); any = true; }
                else values.Add(double.NaN);
            }
            // Safety net for divider bleed. Every paired row is current-then-maximum, so a
            // current value several times its own maximum means a stray glyph was appended.
            // Re-read that box with the right edge pulled in and keep the result only if it
            // now makes sense.
            if (values.Count >= 2 && !double.IsNaN(values[0]) && !double.IsNaN(values[1])
                && values[1] > 0 && values[0] > values[1] * 2)
            {
                Rectangle tight = Box(originX, originY, scale, colPx, col, ri, row.Fields[0], trim: 2);
                (double? retry, string seenAgain) = await ReadNumberBox(frame, tight);
                diag.AppendLine($"  {row.Key,-14} [0] RETRY tighter -> {seenAgain,-12} = {(retry?.ToString("0.##") ?? "—")}");
                if (retry is double rv && rv <= values[1] * 2) values[0] = rv;
                else snap.Warnings.Add($"'{row.Key}' current ({values[0]:0}) exceeds its maximum — suspect a misread.");
            }

            while (values.Count > 0 && double.IsNaN(values[^1])) values.RemoveAt(values.Count - 1);
            if (any && values.Count > 0 && !double.IsNaN(values[0]))
            {
                snap.Fields[row.Key] = values.Where(x => !double.IsNaN(x)).ToList();
                read++;
            }
        }
        snap.Method = "geometry";

        // ---- Insurance: if the grid read came up thin, fall back to row-band pairing over the
        // whole window and merge anything the geometry pass missed.
        if (read < 8)
        {
            log?.Invoke($"Geometry pass read only {read} rows — falling back to a row-band parse.");
            Rectangle winBox = WindowBox(originX, originY, scale, colPx, w, h);
            using Bitmap crop = ImagePrep.Prepare(frame, winBox, 3.0);
            OcrResult pass3 = await Recognize(crop);
            foreach (OcrLine l in pass3.Lines) rawLines.Add(l.Text);
            int added = RowBandParse(pass3, snap);
            snap.Method = read > 0 ? "geometry+bands" : "bands";
            log?.Invoke($"Row-band parse added {added} rows.");
        }

        // ---- Weight hangs off the window's right edge, not the stat grid.
        await ReadWeight(frame, pass1, snap, scale, diag);

        // ---- Header (name / level / classes) and the coin row still come from text.
        ParseHeaderAndCoins(pass1, snap, originX, originY, scale);

        // ---- The equipment grid hangs off the same window, so the anchor we just solved
        // locates it for free — no second search.
        try
        {
            snap.Equipment = EquipmentReader.Read(gameHwnd, originX, originY, scale, log,
                                                  diagnostics ?? false);
        }
        catch (Exception ex) { log?.Invoke("Equipment read failed: " + ex.Message); }

        snap.RawSeen = string.Join("\n", rawLines);

        if (snap.Fields.Count < 6)
            snap.Warnings.Add($"Only {snap.Fields.Count} stat rows parsed — is the Inventory window fully visible?");
        foreach (string need in new[] { "hp", "mana", "ac", "strength", "sv magic" })
            if (!snap.Fields.ContainsKey(need)) snap.Warnings.Add($"'{need}' was not read.");

        log?.Invoke($"parsed {snap.Fields.Count} rows via {snap.Method} at UI scale {snap.UiScale:0.00}"
                  + (snap.Warnings.Count > 0 ? $" — {snap.Warnings.Count} warning(s)" : ""));

        // A thin read is the one worth keeping evidence for; a good one needs no artefacts.
        bool wantDump = diagnostics ?? (snap.Fields.Count < 20);
        if (wantDump) snap.DiagPath = DumpDiagnostics(frame, originX, originY, scale, colPx, diag, snap, log);
        return snap;
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// Pin down the vertical scale by trying the seed and a few nearby values, and keeping
    /// whichever one actually lands on numbers. Three probe boxes are used, spread to the
    /// corners of the grid — HP (column 0, row 1), Strength (column 1, row 1) and SV. Void
    /// (column 1, row 13) — because a wrong scale still hits the top rows and only misses at
    /// the bottom. A 3% error in the assumed column stride would drift half a row by SV. Void,
    /// so this is what stops a plausible-looking read from being quietly wrong.
    /// </summary>
    private static async Task<double> CalibrateVerticalScale(Bitmap frame, double ox, double oy,
                                                             double seed, double colPx, Action<string>? log)
    {
        (int Order, int Field)[] probes = { (1, 0), (15, 0), (27, 0) };
        double best = seed; int bestHits = -1;

        foreach (double k in new[] { 1.0, 0.985, 1.015, 0.97, 1.03 })
        {
            double sc = seed * k;
            int hits = 0;
            foreach ((int order, int fi) in probes)
            {
                InventoryLayout.Row row = InventoryLayout.Rows[order];
                if (row.Fields.Length <= fi) continue;
                Rectangle b = Box(ox, oy, sc, colPx, InventoryLayout.ColumnOf(order),
                                  InventoryLayout.RowInColumn(order), row.Fields[fi]);
                (double? num, _) = await ReadNumberBox(frame, b);
                if (num is not null) hits++;
            }
            if (hits > bestHits) { bestHits = hits; best = sc; }
            if (hits == probes.Length) break;               // nothing to improve on
        }
        if (bestHits < probes.Length)
            log?.Invoke($"Scale calibration settled at {best:0.000} ({bestHits}/{probes.Length} probes hit).");
        return best;
    }

    /// <summary>
    /// The screen rectangle for one value box. Horizontal bounds come from the field's clip span
    /// and are NEVER padded outwards: one unit of overshoot clips the neighbouring "/" divider,
    /// which OCRs as a "1" and turns 257 into 2571. Vertical padding is safe and stays.
    /// </summary>
    /// <param name="trim">Extra units shaved off the right edge, used by the retry pass when a
    /// value comes back implausibly larger than its own maximum.</param>
    private static Rectangle Box(double ox, double oy, double s, double colPx,
                                 int col, int rowInCol, InventoryLayout.Field f, double trim = 0)
    {
        double x0 = ox + col * colPx + f.ClipL * s;
        double x1 = ox + col * colPx + (f.ClipR - trim) * s;
        double y = oy + (rowInCol * InventoryLayout.RowPitch - BoxPad) * s;
        double bh = (InventoryLayout.RowHeight + BoxPad * 2) * s;
        return new Rectangle((int)Math.Round(x0), (int)Math.Round(y),
                             Math.Max(2, (int)Math.Round(x1 - x0)), (int)Math.Round(bh));
    }

    /// <summary>The whole stat grid plus a margin, for the fallback parse and the diagnostic image.</summary>
    private static Rectangle WindowBox(double ox, double oy, double s, double colPx, int w, int h)
    {
        int x0 = (int)Math.Max(0, ox - 12 * s);
        int y0 = (int)Math.Max(0, oy - 12 * s);
        int x1 = (int)Math.Min(w, ox + colPx + (InventoryLayout.ColumnPitch + 24) * s);
        int y1 = (int)Math.Min(h, oy + (InventoryLayout.RowsPerColumn * InventoryLayout.RowPitch + 24) * s);
        return new Rectangle(x0, y0, Math.Max(8, x1 - x0), Math.Max(8, y1 - y0));
    }

    // ---------------------------------------------------------------- OCR plumbing

    private static async Task<OcrResult> Recognize(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        BitmapDecoder dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using SoftwareBitmap sw = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return await Engine!.RecognizeAsync(sw);
    }

    /// <summary>OCR one value box and pull a single number out of it.</summary>
    private static async Task<(double?, string)> ReadNumberBox(Bitmap frame, Rectangle box)
    {
        if (box.Width < 2 || box.Height < 2) return (null, "");
        using Bitmap prepped = ImagePrep.Prepare(frame, box, 6.0, MinBoxPixels);
        OcrResult res = await Recognize(prepped);
        string text = string.Join(" ", res.Lines.Select(l => l.Text)).Trim();
        return (ParseNumber(text), text);
    }

    /// <summary>
    /// A value box holds exactly one number, so anything else in the string is OCR noise.
    /// Common confusions are folded back (O→0, l/I→1, S→5, B→8) before the digits are taken.
    /// </summary>
    private static double? ParseNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder();
        bool neg = text.TrimStart().StartsWith("-", StringComparison.Ordinal);
        foreach (char c in text)
        {
            char k = c switch
            {
                'O' or 'o' or 'Q' or 'D' => '0',
                'l' or 'I' or 'i' or '|' or '!' => '1',
                'S' or 's' => '5',
                'B' => '8',
                'Z' or 'z' => '2',
                'G' => '6',
                _ => c,
            };
            if (char.IsDigit(k)) sb.Append(k);
            else if (k == ',' || k == '.') continue;
            else if (sb.Length > 0) break;              // stop at the first junk after the number
        }
        if (sb.Length == 0) return null;
        if (!double.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out double d)) return null;
        return neg ? -d : d;
    }

    private static (RectangleF?, RectangleF?) FindAnchors(OcrResult ocr)
    {
        RectangleF? vitals = null, stats = null;
        foreach (OcrLine line in ocr.Lines)
        {
            string t = line.Text.ToLowerInvariant();
            List<OcrWord> words = line.Words.ToList();
            if (words.Count == 0) continue;

            if (vitals is null && t.Contains(InventoryLayout.VitalsAnchor, StringComparison.Ordinal))
            {
                OcrWord a = words.FirstOrDefault(x => x.Text.StartsWith("Character", StringComparison.OrdinalIgnoreCase)) ?? words[0];
                vitals = new RectangleF((float)a.BoundingRect.X, (float)a.BoundingRect.Y,
                                        (float)a.BoundingRect.Width, (float)a.BoundingRect.Height);
            }
            if (stats is null && t.Contains(InventoryLayout.StatsAnchor, StringComparison.Ordinal))
            {
                OcrWord a = words.FirstOrDefault(x => x.Text.StartsWith("Stats", StringComparison.OrdinalIgnoreCase)) ?? words[0];
                stats = new RectangleF((float)a.BoundingRect.X, (float)a.BoundingRect.Y,
                                       (float)a.BoundingRect.Width, (float)a.BoundingRect.Height);
            }
        }
        // Both headers sit on the same row; if OCR merged them into one line, split by X instead.
        if (vitals is not null && stats is null)
        {
            foreach (OcrLine line in ocr.Lines)
            {
                string t = line.Text.ToLowerInvariant();
                if (!t.Contains(InventoryLayout.VitalsAnchor, StringComparison.Ordinal)) continue;
                OcrWord? a = line.Words.FirstOrDefault(x => x.Text.StartsWith("Stats", StringComparison.OrdinalIgnoreCase));
                if (a is not null)
                    stats = new RectangleF((float)a.BoundingRect.X, (float)a.BoundingRect.Y,
                                           (float)a.BoundingRect.Width, (float)a.BoundingRect.Height);
                break;
            }
        }
        return (vitals, stats);
    }

    /// <summary>
    /// Weight and worn weight. Their labels are anchored to the window's RIGHT edge and the
    /// window is resizable, so there is no fixed offset from the stat grid to reach them by.
    /// Instead the "Weight" caption is located by OCR: it is left-aligned exactly
    /// <see cref="InventoryLayout.WeightCaptionFromRight"/> units in from the right edge, so
    /// finding it fixes the edge, and the value boxes are simple offsets back from there. The
    /// caption's own line also gives the row's Y, and "Weight (Worn)" gives the row below it.
    /// Reading current and max as separate boxes is what recovers the "/" the OCR loses.
    /// </summary>
    private static async Task ReadWeight(Bitmap frame, OcrResult pass1, InventorySnapshot snap,
                                         double s, StringBuilder diag)
    {
        RectangleF? caption = null, worn = null;
        foreach (OcrLine line in pass1.Lines)
        {
            foreach (OcrWord wd in line.Words)
            {
                if (!wd.Text.StartsWith("Weight", StringComparison.OrdinalIgnoreCase)) continue;
                var rc = new RectangleF((float)wd.BoundingRect.X, (float)wd.BoundingRect.Y,
                                        (float)wd.BoundingRect.Width, (float)wd.BoundingRect.Height);
                // "Weight (Worn)" sits on the line below plain "Weight"; OCR may read the
                // bracketed part as (Wom)/(Wor) so the caption word alone is matched and the
                // lower of the two occurrences is taken as the worn row.
                if (caption is null) caption = rc;
                else if (rc.Y > caption.Value.Y + rc.Height * 0.5) worn ??= rc;
                else if (rc.Y < caption.Value.Y - rc.Height * 0.5) { worn = caption; caption = rc; }
            }
        }
        if (caption is not RectangleF cap) return;

        double right = cap.X + InventoryLayout.WeightCaptionFromRight * s;
        double top = cap.Y - BoxPad * s;
        double h = (InventoryLayout.RowHeight + BoxPad * 2) * s;

        Rectangle Span(int fromRight, int toRight, double y) => new(
            (int)Math.Round(right - fromRight * s), (int)Math.Round(y),
            Math.Max(2, (int)Math.Round((fromRight - toRight) * s)), (int)Math.Round(h));

        var vals = new List<double>();
        (double? cur, string a) = await ReadNumberBox(frame,
            Span(InventoryLayout.WeightCurFromRight, InventoryLayout.WeightCurToRight, top));
        (double? max, string b) = await ReadNumberBox(frame,
            Span(InventoryLayout.WeightMaxFromRight, InventoryLayout.WeightMaxToRight, top));
        diag.AppendLine($"  {"weight",-14} cur -> {a,-12} = {(cur?.ToString("0.##") ?? "—")}   max -> {b,-12} = {(max?.ToString("0.##") ?? "—")}");
        if (cur is double c) vals.Add(c);
        if (max is double m && cur is not null) vals.Add(m);
        if (vals.Count > 0) snap.Fields["weight"] = vals;

        if (worn is RectangleF wr)
        {
            (double? wv, string t) = await ReadNumberBox(frame,
                Span(InventoryLayout.WornWeightFromRight, InventoryLayout.WornWeightToRight, wr.Y - BoxPad * s));
            diag.AppendLine($"  {"weight worn",-14} -> {t,-12} = {(wv?.ToString("0.##") ?? "—")}");
            if (wv is double w2) snap.Fields["weight worn"] = new List<double> { w2 };
        }
    }

    // ---------------------------------------------------------------- fallback parse

    private static readonly (string Key, string[] Words)[] BandLabels =
    {
        ("attack speed", new[]{ "attack", "speed" }),
        ("hp regen", new[]{ "hp", "regen" }), ("mana regen", new[]{ "mana", "regen" }), ("end regen", new[]{ "end", "regen" }),
        ("primary dps", new[]{ "primary", "dps" }), ("secondary dps", new[]{ "secondary", "dps" }), ("ranged dps", new[]{ "ranged", "dps" }),
        ("sv magic", new[]{ "sv", "magic" }), ("sv fire", new[]{ "sv", "fire" }), ("sv cold", new[]{ "sv", "cold" }),
        ("sv disease", new[]{ "sv", "disease" }), ("sv poison", new[]{ "sv", "poison" }), ("sv void", new[]{ "sv", "void" }),
        ("strength", new[]{ "strength" }), ("stamina", new[]{ "stamina" }), ("intelligence", new[]{ "intelligence" }),
        ("wisdom", new[]{ "wisdom" }), ("agility", new[]{ "agility" }), ("dexterity", new[]{ "dexterity" }),
        ("charisma", new[]{ "charisma" }), ("velocity", new[]{ "velocity" }),
        ("attack", new[]{ "attack" }), ("hp", new[]{ "hp" }), ("mana", new[]{ "mana" }), ("end", new[]{ "end" }), ("ac", new[]{ "ac" }),
    };

    /// <summary>
    /// Cluster every OCR word into rows by its Y centre, then walk each row left to right pairing
    /// a label with the numbers that follow it. Unlike the old line walk this doesn't care how
    /// OCR chose to group words into lines, so a wide label→value gap no longer breaks the pair.
    /// </summary>
    private static int RowBandParse(OcrResult ocr, InventorySnapshot snap)
    {
        var words = new List<(string Norm, double X, double CY, double H)>();
        foreach (OcrLine line in ocr.Lines)
            foreach (OcrWord wd in line.Words)
                words.Add((Norm(wd.Text), wd.BoundingRect.X, wd.BoundingRect.Y + wd.BoundingRect.Height / 2.0, wd.BoundingRect.Height));
        if (words.Count == 0) return 0;

        double medianH = words.Select(t => t.H).OrderBy(x => x).ElementAt(words.Count / 2);
        double tol = Math.Max(3.0, medianH * 0.6);

        var bands = new List<List<(string Norm, double X, double CY, double H)>>();
        foreach ((string Norm, double X, double CY, double H) wd in words.OrderBy(t => t.CY))
        {
            if (bands.Count > 0 && Math.Abs(bands[^1][0].CY - wd.CY) <= tol) bands[^1].Add(wd);
            else bands.Add(new List<(string Norm, double X, double CY, double H)> { wd });
        }

        int added = 0;
        foreach (var band in bands)
        {
            List<(string Norm, double X, double CY, double H)> row = band.OrderBy(t => t.X).ToList();
            int i = 0;
            while (i < row.Count)
            {
                string? key = null; int span = 0;
                foreach ((string k, string[] lw) in BandLabels)
                {
                    if (i + lw.Length > row.Count) continue;
                    bool ok = true;
                    for (int j = 0; j < lw.Length; j++) if (row[i + j].Norm != lw[j]) { ok = false; break; }
                    if (ok) { key = k; span = lw.Length; break; }
                }
                if (key is null) { i++; continue; }
                i += span;
                var nums = new List<double>();
                while (i < row.Count && IsNumericish(row[i].Norm)) { nums.AddRange(NumbersIn(row[i].Norm)); i++; }
                if (nums.Count > 0 && !snap.Fields.ContainsKey(key)) { snap.Fields[key] = nums; added++; }
            }
        }
        return added;
    }


    /// <summary>
    /// Turn the header band's lines into a name, a level and a loadout.
    ///
    /// The name is taken ONLY from the line that carries the loadout, or from the line directly
    /// above it. It used to be "any capitalised word in the box", and the box contains the
    /// window's own labels — which is how the character came to be called Weight on one read and
    /// Mana on the next.
    /// </summary>
    private static void ResolveHeader(List<(double Y, double X, string Text)> lines,
                                      InventorySnapshot snap, double scale)
    {
        if (lines.Count == 0) return;
        lines.Sort((a, b) => a.Y.CompareTo(b.Y));

        int at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!HeaderParse.TryParseLoadout(lines[i].Text, out int lv, out string cls)) continue;
            snap.Level ??= lv;
            snap.Classes ??= cls;
            at = i;
            break;
        }
        if (at < 0 || snap.Name is not null) return;

        // On the same line, ahead of the level: "Bryari 50 WAR/DRU/BRD".
        var lead = System.Text.RegularExpressions.Regex.Match(lines[at].Text, @"^\s*([A-Za-z]{3,14})\b");
        if (lead.Success && HeaderParse.LooksLikeName(lead.Groups[1].Value))
        { snap.Name = lead.Groups[1].Value; return; }

        // Otherwise the line directly above it, and only if it is genuinely close: the name sits
        // one row up in the same column, so anything further away is some other part of the window.
        for (int i = at - 1; i >= 0; i--)
        {
            if (lines[at].Y - lines[i].Y > 40 * scale) break;
            if (Math.Abs(lines[i].X - lines[at].X) > 120 * scale) continue;
            string t = lines[i].Text.Trim();
            if (HeaderParse.LooksLikeName(t)) { snap.Name = t; return; }
        }
    }


    /// <summary>EverQuest's sixteen playable races, longest first so "Half Elf" is not eaten by
    /// a substring match on "Elf".</summary>
    private static readonly string[] EqRaces =
    {
        "Half Elf", "Wood Elf", "High Elf", "Dark Elf", "Vah Shir", "Barbarian",
        "Halfling", "Erudite", "Froglok", "Drakkin", "Human", "Dwarf", "Troll",
        "Ogre", "Gnome", "Iksar",
    };

    private static string Norm(string word) => word.ToLowerInvariant().Trim().Trim('.', ':', ',', '(', ')', ';');

    private static bool IsNumericish(string n) =>
        n.Length > 0 && n.All(c => char.IsDigit(c) || c is '/' or ',' or '.' or '+' or '-' or '%' or '|');

    private static IEnumerable<double> NumbersIn(string norm)
    {
        foreach (string piece in norm.Split('/', '|', '+'))
        {
            string p = piece.Replace(",", "").Trim().TrimEnd('%');
            if (p.Length == 0 || p == "-") continue;
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) yield return d;
        }
    }

    // ---------------------------------------------------------------- header + coins

    /// <summary>
    /// Name, level and class combination ("16 PAL/MNK/ENC") and the coin row. Both are drawn
    /// outside the stat grid, so they are still recovered from text — but only from lines that
    /// fall inside the inventory window's own band, which keeps the rest of the screen out.
    /// </summary>
    private static void ParseHeaderAndCoins(OcrResult ocr, InventorySnapshot snap,
                                            double ox, double oy, double scale)
    {
        double left = ox - 24 * scale, right = ox + (InventoryLayout.ColumnPitch * 2 + 140) * scale;
        // The name sits ~199 units above the stat anchor — right on the old 200-unit lip, so any
        // drift in the anchor or the scale pushed it out of the box entirely and the header was
        // never seen. 260 clears it with room. Widening is safe now that a name is only accepted
        // from the loadout line or the line directly above it.
        double top = oy - 260 * scale, bottom = oy + 260 * scale;
        var coinRows = new List<(double Y, List<long> Nums)>();

        // The header is drawn in the window's RIGHT-hand column — name, then "50 WAR/DRU/BRD" —
        // so it is normally two stacked lines, not one line to regex. Collect the candidates and
        // resolve them together rather than deciding line by line.
        var headerLines = new List<(double Y, double X, string Text)>();

        foreach (OcrLine line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x = line.Words[0].BoundingRect.X, y = line.Words[0].BoundingRect.Y;
            if (x < left || x > right || y < top || y > bottom) continue;

            headerLines.Add((y, x, line.Text));

            // Race, if the skin prints it anywhere in the header band. Free to look for: it is a
            // closed list of sixteen names, so a match is a match and a miss costs nothing.
            if (snap.Race is null)
                foreach (string r in EqRaces)
                    if (line.Text.Contains(r, StringComparison.OrdinalIgnoreCase)) { snap.Race = r; break; }

        }

        ResolveHeader(headerLines, snap, scale);

        foreach (OcrLine line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x = line.Words[0].BoundingRect.X, y = line.Words[0].BoundingRect.Y;
            if (x < left || x > right || y < top || y > bottom) continue;

            var pure = new List<long>();
            bool onlyNumbers = true;
            foreach (OcrWord wd in line.Words)
            {
                string p = Norm(wd.Text).Replace(",", "");
                if (p.Length > 0 && p.All(char.IsDigit) && long.TryParse(p, out long val)) pure.Add(val);
                else { onlyNumbers = false; break; }
            }
            if (onlyNumbers && pure.Count >= 3 && pure.All(val => val < 10_000_000))
                coinRows.Add((y, pure));
        }

        if (coinRows.Count > 0)
        {
            List<long> coins = coinRows.OrderByDescending(t => t.Y).First().Nums;
            if (coins.Count >= 4) { snap.Plat = coins[0]; snap.Gold = coins[1]; snap.Silver = coins[2]; snap.Copper = coins[3]; }
            else if (coins.Count == 3) { snap.Plat = coins[0]; snap.Gold = coins[1]; snap.Silver = coins[2]; snap.Warnings.Add("Only 3 coin values read."); }
        }
    }

    // ---------------------------------------------------------------- diagnostics

    /// <summary>
    /// Drop the located window, the box grid drawn over it, and the per-box read log into
    /// %AppData%\EQAvatar\logs\ocr\&lt;timestamp&gt;. When a read goes wrong these three
    /// artefacts say exactly where the geometry landed and what each box actually contained.
    /// </summary>
    private static string? DumpDiagnostics(Bitmap frame, double ox, double oy, double scale, double colPx,
                                           StringBuilder log, InventorySnapshot snap, Action<string>? note)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "EQAvatar", "logs", "ocr", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);

            Rectangle win = WindowBox(ox, oy, scale, colPx, frame.Width, frame.Height);
            using (var shot = new Bitmap(win.Width, win.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(shot))
                {
                    g.DrawImage(frame, new Rectangle(0, 0, win.Width, win.Height), win, GraphicsUnit.Pixel);
                    using var pen = new Pen(Color.Magenta, 1);
                    foreach (InventoryLayout.Row row in InventoryLayout.Rows)
                    {
                        int col = InventoryLayout.ColumnOf(row.Order), ri = InventoryLayout.RowInColumn(row.Order);
                        foreach (InventoryLayout.Field f in row.Fields)
                        {
                            Rectangle b = Box(ox, oy, scale, colPx, col, ri, f);
                            b.Offset(-win.X, -win.Y);
                            g.DrawRectangle(pen, b);
                        }
                    }
                }
                shot.Save(Path.Combine(dir, "window-boxes.png"), ImageFormat.Png);
            }

            var report = new StringBuilder();
            report.AppendLine($"EQ Avatar inventory read — {snap.CapturedAt:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"method {snap.Method}   rows {snap.Fields.Count}   scale {snap.UiScale:0.000}");
            if (snap.Warnings.Count > 0) report.AppendLine("warnings: " + string.Join(" · ", snap.Warnings));
            report.AppendLine().AppendLine("--- per-box reads ---").Append(log);
            report.AppendLine().AppendLine("--- every OCR line seen ---").AppendLine(snap.RawSeen);
            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());

            note?.Invoke("Diagnostics written to " + dir);
            return dir;
        }
        catch (Exception ex) { note?.Invoke("Couldn't write diagnostics: " + ex.Message); return null; }
    }
}
