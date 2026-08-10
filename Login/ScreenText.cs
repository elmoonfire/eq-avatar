using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EQAvatar.Spike.Login;

/// <summary>One recognized word/line, with its centre already mapped to SCREEN coordinates.</summary>
public readonly record struct FoundText(string Text, double X, double Y);

/// <summary>
/// Captures a window with PrintWindow and runs Windows' built-in OCR over it, returning the
/// text it finds with each item's centre in screen space so the caller can click it. This is
/// what drives the auto-login: read the launcher/server/character screens and click by label.
/// </summary>
public static class ScreenText
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    private static OcrEngine? _engine;
    private static OcrEngine? Engine => _engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

    public static async Task<List<FoundText>> ReadAsync(IntPtr hwnd)
    {
        var result = new List<FoundText>();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return result;
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);

        // Capture the on-screen pixels of the (foregrounded) window. This works for the
        // DirectX-rendered game screens too, which PrintWindow can return black for.
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        OcrEngine? engine = Engine;
        if (engine == null) return result;

        SoftwareBitmap sw;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            sw = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        using (sw)
        {
            OcrResult ocr = await engine.RecognizeAsync(sw);
            foreach (OcrLine line in ocr.Lines)
            {
                // Whole line (for multi-word labels like "Enter World" / "SERVER SELECTION").
                double lx = 0, ly = 0; int n = 0;
                foreach (OcrWord word in line.Words)
                {
                    var b = word.BoundingRect;
                    double cx = r.Left + b.X + b.Width / 2.0;
                    double cy = r.Top + b.Y + b.Height / 2.0;
                    result.Add(new FoundText(word.Text, cx, cy));   // individual words (server name, PLAY)
                    lx += cx; ly += cy; n++;
                }
                if (n > 0)
                    result.Add(new FoundText(line.Text, lx / n, ly / n));
            }
        }
        return result;
    }

    /// <summary>
    /// Find the EQL LaunchPad's green PLAY button by colour when OCR can't read it (it's a graphic,
    /// not OS text). Scans the lower portion of the window for a cluster of saturated green and
    /// returns its centroid in SCREEN coordinates, or null if there isn't a convincing green blob.
    /// </summary>
    public static System.Windows.Point? FindGreenButton(IntPtr hwnd,
        double xf0 = 0.0, double xf1 = 1.0, double yf0 = 0.45, double yf1 = 1.0, int minPixels = 90)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return null;
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        if (w < 40 || h < 40) return null;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] buf = new byte[stride * h];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);

        int x0 = Math.Clamp((int)(w * xf0), 0, w), x1 = Math.Clamp((int)(w * xf1), 0, w);
        int y0 = Math.Clamp((int)(h * yf0), 0, h), y1 = Math.Clamp((int)(h * yf1), 0, h);
        long sx = 0, sy = 0, n = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + x * 4;             // BGRA
                byte b = buf[i], gg = buf[i + 1], rr = buf[i + 2];
                if (gg > 105 && gg > rr * 1.35 && gg > b * 1.35) { sx += x; sy += y; n++; }
            }
        }
        if (n < minPixels) return null;          // not enough green in the region
        return new System.Windows.Point(r.Left + (double)sx / n, r.Top + (double)sy / n);
    }

    /// <summary>First item whose text contains <paramref name="needle"/> (case-insensitive).</summary>
    public static bool Find(List<FoundText> items, string needle, out System.Windows.Point center)
    {
        foreach (FoundText f in items)
        {
            if (f.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                center = new System.Windows.Point(f.X, f.Y);
                return true;
            }
        }
        center = default;
        return false;
    }
}
