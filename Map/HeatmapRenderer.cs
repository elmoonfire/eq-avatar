using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQAvatar.Spike.Map;

/// <summary>
/// Turns a list of world-space (x,y) points into a heat-density bitmap, and returns the same
/// points mapped into the bitmap's pixel space so the caller can overlay the travel path.
/// EQ /loc is (Y, X, Z); we plot X horizontally and Y vertically, north up.
/// </summary>
public static class HeatmapRenderer
{
    public sealed record Result(WriteableBitmap Bitmap, List<Point> PixelPoints, int W, int H);

    public static Result Render(IReadOnlyList<Point> world, int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new List<Point>();
        var buf = new byte[w * h * 4];

        // Always draw the stylized "map" backdrop so travel reads against something (and the
        // exported PNG is self-contained) — a dark base, a faint survey grid, and a border.
        DrawBackdrop(buf, w, h);

        if (world.Count == 0)
        {
            bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
            return new Result(bmp, pixels, w, h);
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (Point p in world)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        double rangeX = Math.Max(1, maxX - minX), rangeY = Math.Max(1, maxY - minY);
        const int margin = 24;
        double MapX(double x) => margin + (x - minX) / rangeX * (w - 2 * margin);
        double MapY(double y) => margin + (maxY - y) / rangeY * (h - 2 * margin);   // flip: north up

        foreach (Point p in world)
            pixels.Add(new Point(MapX(p.X), MapY(p.Y)));

        // Accumulate soft splats into an intensity buffer.
        var intensity = new float[w * h];
        const int R = 11; const double sigma = 4.5;
        double twoSigma2 = 2 * sigma * sigma;
        foreach (Point pt in pixels)
        {
            int cx = (int)pt.X, cy = (int)pt.Y;
            for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                intensity[y * w + x] += (float)Math.Exp(-(dx * dx + dy * dy) / twoSigma2);
            }
        }
        float max = 0f;
        foreach (float v in intensity) if (v > max) max = v;

        if (max > 0)
        {
            for (int i = 0; i < intensity.Length; i++)
            {
                double v = Math.Sqrt(intensity[i] / max);   // gamma for a nicer spread
                if (v <= 0.02) continue;
                (byte r, byte g, byte b) = Heat(v);
                double a = Math.Clamp(v, 0, 1);              // blend heat over the backdrop
                int o = i * 4;
                buf[o + 0] = (byte)(buf[o + 0] * (1 - a) + b * a);
                buf[o + 1] = (byte)(buf[o + 1] * (1 - a) + g * a);
                buf[o + 2] = (byte)(buf[o + 2] * (1 - a) + r * a);
                buf[o + 3] = 255;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        return new Result(bmp, pixels, w, h);
    }

    // Dark base + faint survey grid + border, drawn straight into the BGRA buffer.
    private static void DrawBackdrop(byte[] buf, int w, int h)
    {
        void Set(int x, int y, byte b, byte g, byte r)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int o = (y * w + x) * 4; buf[o] = b; buf[o + 1] = g; buf[o + 2] = r; buf[o + 3] = 255;
        }
        for (int i = 0; i < w * h; i++) { int o = i * 4; buf[o] = 20; buf[o + 1] = 14; buf[o + 2] = 10; buf[o + 3] = 255; }
        const int step = 40;
        for (int x = 0; x < w; x += step) for (int y = 0; y < h; y++) Set(x, y, 64, 50, 30);
        for (int y = 0; y < h; y += step) for (int x = 0; x < w; x++) Set(x, y, 64, 50, 30);
        for (int x = 0; x < w; x++) { Set(x, 0, 87, 74, 42); Set(x, h - 1, 87, 74, 42); }
        for (int y = 0; y < h; y++) { Set(0, y, 87, 74, 42); Set(w - 1, y, 87, 74, 42); }
    }

    // blue → cyan → green → yellow → red
    private static (byte, byte, byte) Heat(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double r, g, b;
        if (v < 0.25) { double t = v / 0.25; r = 0; g = t; b = 1; }
        else if (v < 0.5) { double t = (v - 0.25) / 0.25; r = 0; g = 1; b = 1 - t; }
        else if (v < 0.75) { double t = (v - 0.5) / 0.25; r = t; g = 1; b = 0; }
        else { double t = (v - 0.75) / 0.25; r = 1; g = 1 - t; b = 0; }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
