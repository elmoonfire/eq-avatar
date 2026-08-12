using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EQAvatar.Spike.Ocr;

/// <summary>What one equipment slot held when the inventory was read.</summary>
public sealed class EquippedSlot
{
    public int Id;
    public string Name = "";
    public bool Occupied;
    /// <summary>The slot's icon exactly as the game drew it, 40x40, or null when the slot is empty.</summary>
    public byte[]? IconPng;
    /// <summary>Cheap fingerprint of the icon, so repeat reads can tell "unchanged" from "new gear".</summary>
    public string? IconHash;
}

public sealed class EquipmentSnapshot
{
    public readonly List<EquippedSlot> Slots = new();
    public DateTime CapturedAt = DateTime.Now;
    public readonly List<string> Warnings = new();
    public string? DiagPath;
    public int OccupiedCount => Slots.Count(s => s.Occupied);
}

/// <summary>
/// Reads the 23 equipment slots out of the open Inventory window.
///
/// This deliberately does NOT try to name the items. It captures each slot's icon as the game
/// drew it — the actual 40x40 pixels — because that alone fixes the complaint that the armory
/// shows no icons matching the inventory, and it cannot be wrong: the picture IS the game's.
/// Naming needs either an icon-hash atlas over all 379 dragitem sheets (icons are shared between
/// items, so it narrows rather than decides) or hover-tooltip OCR, and neither should hold up
/// getting the right pictures on screen.
///
/// The grid is located from the anchor the stat reader already solved, since both grids hang off
/// the same window — see <see cref="EquipmentLayout"/> for the arithmetic.
/// </summary>
public static class EquipmentReader
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>
    /// An empty slot is the skin's flat backing plate; an item icon is busy and coloured. Both
    /// thresholds are deliberately loose — a slot wrongly called occupied shows a picture of an
    /// empty socket, which is obvious and harmless, whereas wrongly calling it empty silently
    /// drops a piece of gear.
    /// </summary>
    private const double OccupiedStdDev = 11.0;
    private const double OccupiedSaturation = 0.13;

    /// <summary>
    /// Capture the equipment grid. <paramref name="anchorX"/>/<paramref name="anchorY"/> and
    /// <paramref name="scale"/> come straight from the inventory read that just succeeded.
    /// </summary>
    public static EquipmentSnapshot? Read(IntPtr gameHwnd, double anchorX, double anchorY, double scale,
                                          Action<string>? log = null, bool diagnostics = false)
    {
        if (gameHwnd == IntPtr.Zero || !GetWindowRect(gameHwnd, out RECT r)) { log?.Invoke("No game window."); return null; }
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);

        using var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(frame))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        var snap = new EquipmentSnapshot();
        var diag = new StringBuilder();
        diag.AppendLine($"anchor {anchorX:0},{anchorY:0}  scale {scale:0.000}  frame {w}x{h}");

        foreach (EquipmentLayout.Slot s in EquipmentLayout.Slots)
        {
            Rectangle box = SlotBox(anchorX, anchorY, scale, s);
            var slot = new EquippedSlot { Id = s.Id, Name = s.Name };

            if (box.Width < 4 || box.Height < 4 ||
                box.Right > frame.Width || box.Bottom > frame.Height || box.X < 0 || box.Y < 0)
            {
                snap.Warnings.Add($"Slot {s.Name} fell outside the captured frame.");
                snap.Slots.Add(slot);
                continue;
            }

            using Bitmap icon = Crop(frame, box);
            (double stdDev, double sat) = Busyness(icon);
            slot.Occupied = stdDev > OccupiedStdDev || sat > OccupiedSaturation;
            diag.AppendLine($"  {s.Name,-13} id{s.Id,-3} {box.X},{box.Y} {box.Width}x{box.Height}  " +
                            $"stddev {stdDev,6:0.0}  sat {sat:0.000}  -> {(slot.Occupied ? "ITEM" : "empty")}");

            if (slot.Occupied)
            {
                slot.IconPng = ToPng(icon);
                slot.IconHash = Fingerprint(icon);
            }
            snap.Slots.Add(slot);
        }

        log?.Invoke($"equipment: {snap.OccupiedCount} of {EquipmentLayout.Slots.Length} slots filled");
        if (diagnostics || snap.OccupiedCount == 0)
            snap.DiagPath = Dump(frame, anchorX, anchorY, scale, diag, snap, log);
        return snap;
    }

    /// <summary>Slot rectangle in screen pixels, derived from the stat grid's anchor.</summary>
    private static Rectangle SlotBox(double ax, double ay, double s, EquipmentLayout.Slot slot) => new(
        (int)Math.Round(ax + slot.X * s),
        (int)Math.Round(ay + (slot.Y - EquipmentLayout.AnchorToEquipmentY) * s),
        (int)Math.Round(EquipmentLayout.SlotSize * s),
        (int)Math.Round(EquipmentLayout.SlotSize * s));

    private static Bitmap Crop(Bitmap src, Rectangle box)
    {
        // Normalise back to the icon's native 40x40 so fingerprints are comparable between
        // machines and UI scales.
        var dst = new Bitmap(EquipmentLayout.SlotSize, EquipmentLayout.SlotSize, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), box, GraphicsUnit.Pixel);
        return dst;
    }

    /// <summary>Luminance spread and mean saturation — how "busy and coloured" a crop is.</summary>
    private static (double StdDev, double Saturation) Busyness(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] buf = new byte[d.Stride * bmp.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            double sum = 0, sumSq = 0, sat = 0;
            int n = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * d.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int i = row + x * 4;
                    double b = buf[i], gg = buf[i + 1], rr = buf[i + 2];
                    double lum = (rr * 299 + gg * 587 + b * 114) / 1000.0;
                    double mx = Math.Max(rr, Math.Max(gg, b)), mn = Math.Min(rr, Math.Min(gg, b));
                    sum += lum; sumSq += lum * lum;
                    sat += mx <= 0 ? 0 : (mx - mn) / mx;
                    n++;
                }
            }
            if (n == 0) return (0, 0);
            double mean = sum / n;
            return (Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)), sat / n);
        }
        finally { bmp.UnlockBits(d); }
    }

    /// <summary>An 8x8 average-hash of the icon, hex encoded — enough to spot "same gear as last read".</summary>
    private static string Fingerprint(Bitmap bmp)
    {
        using var small = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, new Rectangle(0, 0, 8, 8));
        }
        double[] lum = new double[64];
        for (int y = 0, k = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, k++)
            {
                Color c = small.GetPixel(x, y);
                lum[k] = (c.R * 299 + c.G * 587 + c.B * 114) / 1000.0;
            }
        double avg = lum.Average();
        ulong bits = 0;
        for (int i = 0; i < 64; i++) if (lum[i] > avg) bits |= 1UL << i;
        return bits.ToString("x16");
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// The grid drawn over the real window plus every slot's verdict and its captured icon.
    /// The Y anchor carries a two-unit estimate for glyph inset, so if the boxes ever sit off
    /// the slots this picture says so immediately.
    /// </summary>
    private static string? Dump(Bitmap frame, double ax, double ay, double s, StringBuilder log,
                                EquipmentSnapshot snap, Action<string>? note)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "EQAvatar", "logs", "ocr", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-equip");
            Directory.CreateDirectory(dir);

            int x0 = (int)Math.Max(0, ax - 20 * s), y0 = (int)Math.Max(0, ay - (EquipmentLayout.AnchorToEquipmentY + 20) * s);
            int x1 = (int)Math.Min(frame.Width, ax + 380 * s), y1 = (int)Math.Min(frame.Height, ay + 30 * s);
            var win = new Rectangle(x0, y0, Math.Max(8, x1 - x0), Math.Max(8, y1 - y0));

            using (var shot = new Bitmap(win.Width, win.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(shot))
                {
                    g.DrawImage(frame, new Rectangle(0, 0, win.Width, win.Height), win, GraphicsUnit.Pixel);
                    using var filled = new Pen(Color.Lime, 1);
                    using var empty = new Pen(Color.OrangeRed, 1);
                    foreach (EquipmentLayout.Slot sl in EquipmentLayout.Slots)
                    {
                        Rectangle b = SlotBox(ax, ay, s, sl);
                        b.Offset(-win.X, -win.Y);
                        bool occ = snap.Slots.FirstOrDefault(q => q.Id == sl.Id)?.Occupied == true;
                        g.DrawRectangle(occ ? filled : empty, b);
                    }
                }
                shot.Save(Path.Combine(dir, "equipment-boxes.png"), ImageFormat.Png);
            }

            foreach (EquippedSlot sl in snap.Slots.Where(q => q.IconPng is not null))
                File.WriteAllBytes(Path.Combine(dir, $"slot{sl.Id:00}-{Sanitize(sl.Name)}.png"), sl.IconPng!);

            File.WriteAllText(Path.Combine(dir, "equipment.txt"),
                $"EQ Avatar equipment read — {snap.CapturedAt:yyyy-MM-dd HH:mm:ss}\n" +
                $"{snap.OccupiedCount} of {EquipmentLayout.Slots.Length} slots filled\n" +
                (snap.Warnings.Count > 0 ? "warnings: " + string.Join(" · ", snap.Warnings) + "\n" : "") +
                "\n" + log);

            note?.Invoke("Equipment diagnostics written to " + dir);
            return dir;
        }
        catch (Exception ex) { note?.Invoke("Couldn't write equipment diagnostics: " + ex.Message); return null; }
    }

    private static string Sanitize(string s) => string.Concat(s.Where(char.IsLetterOrDigit));
}
