using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// Makes a screen grab legible to Windows OCR before the OCR ever sees it.
///
/// WHY THIS EXISTS: with Windows Auto HDR on, GDI's CopyFromScreen hands back the tone-mapped
/// SDR surface, and EQ's small pale-on-dark stat text comes out washed out — greys against
/// greys. Windows OCR still recognises the digits, but it loses thin glyphs: the "/" between
/// current and max vanishes, so "861/861" arrives as "8611861" and "87/257" as "871257", and
/// "Attack" gets misread as "Mack". Rather than ask anyone to turn HDR off, every crop gets a
/// per-crop contrast stretch first, which puts the text back at full black-on-white before the
/// upscale. Because the stretch is computed from the crop's own histogram it adapts to whatever
/// the compositor did, HDR or not.
/// </summary>
public static class ImagePrep
{
    /// <summary>
    /// Grayscale, stretch the crop's own 2nd–98th percentile to full range, invert so the text
    /// is dark on light (Windows OCR is trained on documents and does measurably better that
    /// way), then upscale. Returns a new bitmap; the caller owns it.
    /// </summary>
    /// <param name="src">Source image.</param>
    /// <param name="area">Region of <paramref name="src"/> to prepare.</param>
    /// <param name="scale">Upscale factor applied after the stretch.</param>
    /// <param name="minHeight">Pad the upscale so the result is at least this tall — Windows
    /// OCR gets unreliable on very small bitmaps, and a three-digit stat box is tiny.</param>
    public static Bitmap Prepare(Bitmap src, Rectangle area, double scale, int minHeight = 0)
    {
        area = Rectangle.Intersect(area, new Rectangle(0, 0, src.Width, src.Height));
        if (area.Width <= 0 || area.Height <= 0) area = new Rectangle(0, 0, Math.Min(1, src.Width), Math.Min(1, src.Height));

        if (minHeight > 0 && area.Height * scale < minHeight)
            scale = (double)minHeight / area.Height;

        int dw = Math.Max(1, (int)Math.Round(area.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(area.Height * scale));

        // 1) lift the crop out at native size so the histogram is measured on real pixels
        using var native = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(native))
            g.DrawImage(src, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);

        // 2) grayscale + percentile stretch + invert, in place
        Stretch(native);

        // 3) upscale the cleaned copy
        var dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(native, new Rectangle(0, 0, dw, dh),
                        new Rectangle(0, 0, area.Width, area.Height), GraphicsUnit.Pixel);
        }
        return dst;
    }

    /// <summary>Grayscale → 2nd/98th-percentile linear stretch → invert. Operates in place.</summary>
    private static void Stretch(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride, h = bmp.Height, w = bmp.Width;
            byte[] buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            // luminance histogram
            Span<int> hist = stackalloc int[256];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;                                    // BGRA
                    int lum = (buf[i + 2] * 299 + buf[i + 1] * 587 + buf[i] * 114) / 1000;
                    buf[i] = buf[i + 1] = buf[i + 2] = (byte)lum;           // grayscale now
                    hist[lum]++;
                }
            }

            long total = (long)w * h;
            int lo = Percentile(hist, total, 0.02), hi = Percentile(hist, total, 0.98);
            if (hi - lo < 12) { lo = 0; hi = 255; }                          // flat crop — don't amplify noise

            // build the LUT once: stretch to 0..255, then invert (dark text on light ground)
            Span<byte> lut = stackalloc byte[256];
            double span = Math.Max(1, hi - lo);
            for (int v = 0; v < 256; v++)
            {
                int s = (int)Math.Round((v - lo) * 255.0 / span);
                lut[v] = (byte)(255 - Math.Clamp(s, 0, 255));
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte v = lut[buf[i]];
                    buf[i] = buf[i + 1] = buf[i + 2] = v;
                    buf[i + 3] = 255;
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static int Percentile(ReadOnlySpan<int> hist, long total, double p)
    {
        long want = (long)(total * p), seen = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += hist[v];
            if (seen >= want) return v;
        }
        return 255;
    }
}
