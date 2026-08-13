using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace EQAvatar.Spike.Roles;

/// <summary>
/// A picture of what a pick actually learned, kept with the pick.
///
/// WHY. Until now a learned icon was 108 numbers in a file. When a run failed to find the item
/// there was no way to answer the only question that matters — "what is she comparing against?" —
/// so every failure looked the same as every other, and a signature quietly learned from an empty
/// slot, a neighbouring item, or a bag that had moved was indistinguishable from a good one. The
/// bytes cost a few kilobytes per step; not being able to see them cost a night of farming.
///
/// It stores the pixels AROUND the pick as well as the pick itself (<see cref="Png"/> is the wider
/// patch, <see cref="RX"/>–<see cref="RH"/> locate the picked box inside it) so the popup can draw
/// the box in context. A crop of exactly the picked region would prove nothing: an empty slot looks
/// like a perfectly good icon when you can't see what is next to it.
/// </summary>
public sealed class PickShot
{
    /// <summary>The context patch, PNG, base64. Small by construction — the patch is a few hundred
    /// pixels a side and PNG is generous with flat UI art.</summary>
    public string Png { get; set; } = "";
    /// <summary>The picked box within the patch, 0–1.</summary>
    public double RX { get; set; }
    public double RY { get; set; }
    public double RW { get; set; }
    public double RH { get; set; }
    /// <summary>When it was learned, so "is this still current?" has an answer.</summary>
    public DateTime When { get; set; } = DateTime.Now;
    /// <summary>Pixel size of the picked box at learn time — the sliding search's window size.</summary>
    public int BoxW { get; set; }
    public int BoxH { get; set; }

    /// <summary>Longest side of the stored patch, and the ceiling on the encoded string. A pick
    /// snapshot is evidence, not a screenshot: it rides inside questscripts.json, which is
    /// rewritten in full every time the user edits ANY field, so a megabyte of base64 here would
    /// be paid again on every keystroke that leaves a text box.</summary>
    private const int MaxSide = 320;
    private const int MaxBase64 = 120_000;

    /// <summary>
    /// Cut a context patch out of the pick frame around a normalized box.
    /// <paramref name="pad"/> is how much of the box's own size to add on every side.
    ///
    /// Oversized patches are SCALED, never cropped: the box coordinates are stored relative to the
    /// patch, so cropping would leave the orange "this is what she matches" rectangle pointing off
    /// the edge of a picture that is itself secretly incomplete — a verification window that lies
    /// is worse than no verification window.
    /// </summary>
    public static PickShot? From(Bitmap frame, double nx, double ny, double nw, double nh, double pad = 1.6)
    {
        try
        {
            int fx = frame.Width, fy = frame.Height;
            int bx = (int)Math.Round(nx * fx), by = (int)Math.Round(ny * fy);
            int bw = Math.Max(1, (int)Math.Round(nw * fx)), bh = Math.Max(1, (int)Math.Round(nh * fy));

            int padX = (int)Math.Round(bw * pad), padY = (int)Math.Round(bh * pad);
            int px = Math.Clamp(bx - padX, 0, Math.Max(0, fx - 1));
            int py = Math.Clamp(by - padY, 0, Math.Max(0, fy - 1));
            int pw = Math.Clamp(bw + padX * 2, 1, fx - px);
            int ph = Math.Clamp(bh + padY * 2, 1, fy - py);

            var shot = new PickShot
            {
                RX = (double)(bx - px) / pw,
                RY = (double)(by - py) / ph,
                RW = (double)bw / pw,
                RH = (double)bh / ph,
                BoxW = bw,
                BoxH = bh,
                When = DateTime.Now,
            };

            // Shrink until the encoded form is small enough to live in a file we rewrite constantly.
            for (int side = MaxSide; side >= 80; side /= 2)
            {
                double scale = Math.Min(1.0, (double)side / Math.Max(pw, ph));
                int dw = Math.Max(1, (int)Math.Round(pw * scale)), dh = Math.Max(1, (int)Math.Round(ph * scale));
                using var patch = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(patch))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(frame, new Rectangle(0, 0, dw, dh), new Rectangle(px, py, pw, ph), GraphicsUnit.Pixel);
                }
                using var ms = new MemoryStream();
                patch.Save(ms, ImageFormat.Png);
                string b64 = Convert.ToBase64String(ms.ToArray());
                if (b64.Length <= MaxBase64 || side <= 80)
                {
                    shot.Png = b64;
                    return shot;
                }
            }
            return null;
        }
        catch { return null; }      // a missing picture must never cost you the pick
    }

    public byte[]? Bytes()
    {
        try { return Png.Length == 0 ? null : Convert.FromBase64String(Png); }
        catch { return null; }
    }
}
