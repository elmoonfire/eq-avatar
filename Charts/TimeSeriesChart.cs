using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace EQAvatar.Spike.Charts;

/// <summary>One plotted series: evenly spaced values (one per bucket) drawn as a line.</summary>
public sealed record ChartSeries(string Label, Color Color, IReadOnlyList<double> Values, bool Fill = false);

/// <summary>
/// The app's one chart control — a lightweight dark-theme time-series line chart used by the
/// Command Center (live DPS), the Combat panel (per-fight damage), and Session History
/// (per-minute session timeline). No dependencies: pure OnRender. Series share the x axis
/// (bucket index) and the y axis (max of all series, headroom-padded).
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private string _xLeft = "", _xRight = "", _empty = "no data yet";

    public void SetSeries(IReadOnlyList<ChartSeries> series, string xLeft = "", string xRight = "", string emptyText = "no data yet")
    {
        _series = series; _xLeft = xLeft; _xRight = xRight; _empty = emptyText;
        InvalidateVisual();
    }

    private FormattedText Txt(string s, double size, Color c, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x0C, 0x0F, 0x13)), null, new Rect(0, 0, w, h), 6, 6);

        double max = _series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
        int n = _series.Select(s => s.Values.Count).DefaultIfEmpty(0).Max();
        if (max <= 0 || n < 2)
        {
            FormattedText ft = Txt(_empty, 11.5, Color.FromRgb(0x5D, 0x68, 0x78));
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        max *= 1.12;                                             // headroom so peaks don't kiss the top

        const double padL = 8, padR = 8, padT = 8, padB = 16;
        double cw = w - padL - padR, ch = h - padT - padB;

        // gridlines (quarters) + y max label
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x28, 0x4F, 0xC3, 0xF7)), 1);
        for (int g = 1; g <= 3; g++)
        {
            double y = padT + ch * g / 4.0;
            dc.DrawLine(grid, new Point(padL, y), new Point(w - padR, y));
        }
        FormattedText maxT = Txt(FormatNum(max / 1.12), 10, Color.FromRgb(0x5D, 0x68, 0x78));
        dc.DrawText(maxT, new Point(padL + 1, padT - 2));

        // series
        foreach (ChartSeries s in _series)
        {
            if (s.Values.Count < 2) continue;
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                Point P(int i) => new(padL + cw * i / (n - 1), padT + ch - ch * Math.Min(1, s.Values[i] / max));
                ctx.BeginFigure(P(0), s.Fill, s.Fill);
                for (int i = 1; i < s.Values.Count; i++) ctx.LineTo(P(i), true, true);
                if (s.Fill)
                {
                    ctx.LineTo(new Point(padL + cw * (s.Values.Count - 1) / (n - 1), padT + ch), false, false);
                    ctx.LineTo(new Point(padL, padT + ch), false, false);
                }
            }
            geo.Freeze();
            if (s.Fill)
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, s.Color.R, s.Color.G, s.Color.B)), null, geo);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(s.Color), 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }

        // x labels + legend
        var dim = Color.FromRgb(0x5D, 0x68, 0x78);
        if (_xLeft.Length > 0) dc.DrawText(Txt(_xLeft, 10, dim), new Point(padL, h - padB + 2));
        if (_xRight.Length > 0)
        {
            FormattedText rt = Txt(_xRight, 10, dim);
            dc.DrawText(rt, new Point(w - padR - rt.Width, h - padB + 2));
        }
        double lx = w - padR;
        foreach (ChartSeries s in _series.Reverse())
        {
            FormattedText lt = Txt(s.Label, 10, s.Color, bold: true);
            lx -= lt.Width;
            dc.DrawText(lt, new Point(lx, padT));
            lx -= 14;
        }
    }

    private static string FormatNum(double v) =>
        v >= 10000 ? $"{v / 1000:0.#}k" : v >= 100 ? $"{v:0}" : $"{v:0.#}";
}
