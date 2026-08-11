using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Reads the in-game Inventory window (default_modern skin) off the screen — the most-trusted
/// source of character data, because it is literally what the game displays. Nothing is entered
/// by hand: vitals, attributes (with caps + heroic bonus), resists, regens, weight, currency,
/// and the name / level / class header all come from one capture.
///
/// HOW IT FINDS THE WINDOW: not by pixel offsets. The inventory is a movable child window, so
/// the reader OCRs the whole game frame once and looks for the text anchors the skin always
/// draws — "Character Vitals" and "Stats and Resists". Their positions define the crop, which
/// makes the whole thing resolution- and position-independent. The crop is then UPSCALED 3×
/// (the stat text is small; Windows OCR reads the enlarged copy far more reliably) and parsed
/// by walking each OCR line: known label → the numbers that follow it, until the next label.
/// A "5263/5263" token is two numbers; "295/510 + 0" is current / cap / heroic.
/// </summary>
public sealed class InventorySnapshot
{
    public string? Name;
    public int? Level;
    public string? Classes;                                  // "WAR/DRU/BRD"
    public readonly Dictionary<string, List<double>> Fields = new();   // label -> numbers, raw
    public long? Plat, Gold, Silver, Copper;
    public DateTime CapturedAt = DateTime.Now;
    public readonly List<string> Warnings = new();
    public string RawSeen = "";                              // every OCR line, for remote debugging

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

    // Longest-match-first label set. Normalized: lowercase, no dots/colons/parens.
    private static readonly string[][] Labels =
    {
        new[]{"attack","speed","%"}, new[]{"attack","speed"},
        new[]{"hp","regen"}, new[]{"mana","regen"}, new[]{"end","regen"},
        new[]{"primary","dps"}, new[]{"secondary","dps"}, new[]{"ranged","dps"},
        new[]{"weight","worn"}, new[]{"next","level"}, new[]{"next","aa"},
        new[]{"sv","magic"}, new[]{"sv","fire"}, new[]{"sv","cold"},
        new[]{"sv","disease"}, new[]{"sv","poison"}, new[]{"sv","void"},
        new[]{"strength"}, new[]{"stamina"}, new[]{"intelligence"}, new[]{"wisdom"},
        new[]{"agility"}, new[]{"dexterity"}, new[]{"charisma"},
        new[]{"velocity"}, new[]{"weight"}, new[]{"attack"},
        new[]{"hp"}, new[]{"mana"}, new[]{"end"}, new[]{"ac"},
    };

    public static async Task<InventorySnapshot?> ReadAsync(IntPtr gameHwnd, Action<string>? log = null)
    {
        if (gameHwnd == IntPtr.Zero || !GetWindowRect(gameHwnd, out RECT r)) { log?.Invoke("No game window."); return null; }
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        if (Engine is null) { log?.Invoke("Windows OCR engine unavailable."); return null; }

        using var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(frame))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        // Pass 1 — find the anchors anywhere in the frame.
        OcrResult pass1 = await Recognize(frame);
        (Rect? vitals, Rect? stats) = FindAnchors(pass1);
        if (vitals is null)
        {
            log?.Invoke("Inventory window not found — open your Inventory (default_modern skin) and try again.");
            return null;
        }
        Rect v = vitals.Value;
        double lineH = v.H * 1.55;                                  // row pitch, in anchor units
        double colSpan = stats is Rect s ? s.X - v.X : v.W * 3.4;   // vitals column → stats column

        // Generous crop: left of "Character Vitals", up past the header (name/level/weight),
        // right past the third column, down past the currency row. Clamped to the frame.
        int cx0 = (int)Math.Max(0, v.X - lineH * 1.2);
        int cy0 = (int)Math.Max(0, v.Y - lineH * 10.5);
        int cx1 = (int)Math.Min(w, v.X + colSpan * 3.3);
        int cy1 = (int)Math.Min(h, v.Y + lineH * 17.5);
        if (cx1 - cx0 < 40 || cy1 - cy0 < 40) { log?.Invoke("Inventory crop came out degenerate — is the window near the screen edge?"); return null; }

        const int ScaleFactor = 3;
        using var crop = new Bitmap((cx1 - cx0) * ScaleFactor, (cy1 - cy0) * ScaleFactor, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(frame, new Rectangle(0, 0, crop.Width, crop.Height),
                        new Rectangle(cx0, cy0, cx1 - cx0, cy1 - cy0), GraphicsUnit.Pixel);
        }

        // Pass 2 — the real read, on the enlarged copy.
        OcrResult pass2 = await Recognize(crop);
        var snap = new InventorySnapshot();
        Parse(pass2, snap);

        // The vitals column is the one that matters — if HP didn't land, try once more at 4×.
        if (!snap.Fields.ContainsKey("hp"))
        {
            log?.Invoke("hp row missed at 3× — retrying the read at 4× upscale…");
            using var crop4 = new Bitmap((cx1 - cx0) * 4, (cy1 - cy0) * 4, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(crop4))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(frame, new Rectangle(0, 0, crop4.Width, crop4.Height),
                            new Rectangle(cx0, cy0, cx1 - cx0, cy1 - cy0), GraphicsUnit.Pixel);
            }
            var snap4 = new InventorySnapshot();
            Parse(await Recognize(crop4), snap4);
            foreach ((string k2, List<double> v2) in snap4.Fields)
                if (!snap.Fields.ContainsKey(k2)) snap.Fields[k2] = v2;
            snap.Level ??= snap4.Level; snap.Classes ??= snap4.Classes; snap.Name ??= snap4.Name;
            snap.Plat ??= snap4.Plat; snap.Gold ??= snap4.Gold; snap.Silver ??= snap4.Silver; snap.Copper ??= snap4.Copper;
        }
        if (snap.Fields.Count < 6)
            snap.Warnings.Add($"Only {snap.Fields.Count} stat rows parsed — OCR may have struggled at this resolution.");
        foreach (string need in new[] { "hp", "mana", "ac", "strength", "sv magic" })
            if (!snap.Fields.ContainsKey(need)) snap.Warnings.Add($"'{need}' was not read.");
        return snap;
    }

    private readonly record struct Rect(double X, double Y, double W, double H);

    private static async Task<OcrResult> Recognize(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        BitmapDecoder dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using SoftwareBitmap sw = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return await Engine!.RecognizeAsync(sw);
    }

    private static (Rect?, Rect?) FindAnchors(OcrResult ocr)
    {
        Rect? vitals = null, stats = null;
        foreach (OcrLine line in ocr.Lines)
        {
            string t = line.Text.ToLowerInvariant();
            var words = line.Words.ToList();
            int iv = t.IndexOf("character vitals", StringComparison.Ordinal);
            if (iv >= 0 && vitals is null && words.Count > 0)
            {
                OcrWord anchor = words.FirstOrDefault(x => x.Text.StartsWith("Character", StringComparison.OrdinalIgnoreCase)) ?? words[0];
                vitals = new Rect(anchor.BoundingRect.X, anchor.BoundingRect.Y, anchor.BoundingRect.Width, anchor.BoundingRect.Height);
            }
            if (t.Contains("stats and resists") && stats is null && words.Count > 0)
            {
                OcrWord anchor = words.FirstOrDefault(x => x.Text.StartsWith("Stats", StringComparison.OrdinalIgnoreCase)) ?? words[0];
                stats = new Rect(anchor.BoundingRect.X, anchor.BoundingRect.Y, anchor.BoundingRect.Width, anchor.BoundingRect.Height);
            }
        }
        return (vitals, stats);
    }

    private static string Norm(string word) =>
        word.ToLowerInvariant().Trim().Trim('.', ':', ',', '(', ')', ';');

    private static bool IsNumericish(string norm) =>
        norm.Length > 0 && norm.All(c => char.IsDigit(c) || c is '/' or ',' or '.' or '+' or '-' or '%' or '|');

    /// <summary>Same length, at most one differing character — catches single OCR misreads.</summary>
    private static bool OneOff(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i] && ++diff > 1) return false;
        return diff <= 1;
    }

    private static IEnumerable<double> NumbersIn(string norm)
    {
        foreach (string piece in norm.Split('/', '|', '+'))
        {
            string p = piece.Replace(",", "").Trim().TrimEnd('%');
            if (p.Length == 0 || p == "-") continue;
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) yield return d;
        }
    }

    private static void Parse(OcrResult ocr, InventorySnapshot snap)
    {
        var raw = new List<string>();
        var numberRows = new List<(double y, List<long> nums)>();

        foreach (OcrLine line in ocr.Lines)
        {
            raw.Add(line.Text);
            var words = line.Words.Select(x => (norm: Norm(x.Text), x.Text, y: x.BoundingRect.Y)).ToList();

            // 1) label → numbers walk
            int i = 0;
            while (i < words.Count)
            {
                string[]? matched = null;
                foreach (string[] lab in Labels)
                {
                    if (i + lab.Length > words.Count) continue;
                    bool ok = true;
                    for (int k = 0; k < lab.Length; k++) if (words[i + k].norm != lab[k]) { ok = false; break; }
                    if (ok) { matched = lab; break; }
                }
                string mergedRest = "";
                if (matched is null)
                {
                    // OCR sometimes glues the label to its number ("hp5263/5263") — peel it apart.
                    foreach (string[] lab in Labels)
                    {
                        if (lab.Length != 1) continue;
                        string wn = words[i].norm;
                        if (wn.Length > lab[0].Length && wn.StartsWith(lab[0], StringComparison.Ordinal)
                            && IsNumericish(wn[lab[0].Length..]))
                        { matched = lab; mergedRest = wn[lab[0].Length..]; break; }
                    }
                    // Light fuzz for LONG labels only (strength/stamina/…): one wrong character.
                    if (matched is null)
                        foreach (string[] lab in Labels)
                        {
                            if (lab.Length != 1 || lab[0].Length < 5) continue;
                            if (OneOff(words[i].norm, lab[0])) { matched = lab; break; }
                        }
                }
                if (matched is null) { i++; continue; }
                if (mergedRest.Length == 0) i += matched.Length; else i++;
                var nums = new List<double>();
                if (mergedRest.Length > 0) nums.AddRange(NumbersIn(mergedRest));
                while (i < words.Count && IsNumericish(words[i].norm))
                {
                    nums.AddRange(NumbersIn(words[i].norm));
                    i++;
                }
                string key = string.Join(" ", matched).TrimEnd('%').Trim();
                if (nums.Count > 0 && !snap.Fields.ContainsKey(key)) snap.Fields[key] = nums;
            }

            // 2) character header: "50 WAR/DRU/BRD" (+ the name on the line above or same line)
            var m = System.Text.RegularExpressions.Regex.Match(line.Text, @"\b(\d{1,2})\s+([A-Z]{2,4}(?:/[A-Z]{2,4}){0,2})\b");
            if (m.Success && snap.Level is null)
            {
                snap.Level = int.Parse(m.Groups[1].Value);
                snap.Classes = m.Groups[2].Value;
                string before = line.Text[..m.Index].Trim();
                if (before.Length >= 2 && before.All(c => char.IsLetter(c))) snap.Name ??= before;
            }
            else if (snap.Level is null && line.Words.Count == 1 && line.Text.Length is >= 3 and <= 14
                     && line.Text.All(char.IsLetter) && char.IsUpper(line.Text[0]))
            {
                snap.Name ??= line.Text;      // a lone capitalized word just above the level line
            }

            // 3) currency candidate: a row of ≥3 plain numbers and nothing else
            var pure = new List<long>();
            bool onlyNumbers = words.Count > 0;
            foreach ((string norm, _, _) in words)
            {
                string p = norm.Replace(",", "");
                if (p.Length > 0 && p.All(char.IsDigit) && long.TryParse(p, out long v)) pure.Add(v);
                else if (p.Length > 0) { onlyNumbers = false; break; }
            }
            if (onlyNumbers && pure.Count >= 3 && pure.All(v => v < 10_000_000))
                numberRows.Add((line.Words[0].BoundingRect.Y, pure));
        }

        // The lowest all-number row inside the window is the coin row: plat gold silver copper.
        if (numberRows.Count > 0)
        {
            List<long> coins = numberRows.OrderByDescending(t => t.y).First().nums;
            if (coins.Count >= 4) { snap.Plat = coins[0]; snap.Gold = coins[1]; snap.Silver = coins[2]; snap.Copper = coins[3]; }
            else if (coins.Count == 3) { snap.Plat = coins[0]; snap.Gold = coins[1]; snap.Silver = coins[2]; snap.Warnings.Add("Only 3 coin values read."); }
        }

        snap.RawSeen = string.Join("\n", raw);
    }
}
