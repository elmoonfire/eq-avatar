using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// The tether slider that IS a tether: a glowing rope from the mascot (drawn beside this control)
/// out to a stake knob. The rope sags and sways gently like a reed in a stream; dragging the
/// stake lengthens the leash. Values snap to 10s below 50 and 50s above (10–1500), and the track
/// is piecewise so the tight-camp range (10–50) gets real room to grab.
/// </summary>
public sealed class TetherRope : FrameworkElement
{
    public event Action? ValueChanged;

    private double _value = 300;
    public double Value
    {
        get => _value;
        set
        {
            double snapped = Snap(value);
            if (Math.Abs(snapped - _value) < 0.01) return;
            _value = snapped;
            InvalidateVisual();
            ValueChanged?.Invoke();
        }
    }

    private static double Snap(double v)
        => Math.Clamp(v < 50 ? Math.Round(v / 10) * 10 : Math.Round(v / 50) * 50, 10, 1500);

    // piecewise track: [10,50] → first 18% (breathing room for tight camps), [50,1500] → rest
    private static double ToPos(double v) => v <= 50 ? (v - 10) / 40.0 * 0.18 : 0.18 + (v - 50) / 1450.0 * 0.82;
    private static double FromPos(double p) => p <= 0.18 ? 10 + p / 0.18 * 40 : 50 + (p - 0.18) / 0.82 * 1450;

    private readonly DispatcherTimer _sway = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _phase;
    private bool _dragging;

    public TetherRope()
    {
        MinHeight = 46;
        Cursor = Cursors.Hand;
        _sway.Tick += (_, _) => { _phase += 0.09; InvalidateVisual(); };
        Loaded += (_, _) => { if (IsVisible) _sway.Start(); };
        Unloaded += (_, _) => _sway.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _sway.Start(); else _sway.Stop(); };
    }

    private void SetFromPoint(Point p)
    {
        double margin = 14;
        double w = Math.Max(40, ActualWidth - margin * 2);
        Value = FromPos(Math.Clamp((p.X - margin) / w, 0, 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { _dragging = true; CaptureMouse(); SetFromPoint(e.GetPosition(this)); }

    protected override void OnMouseMove(MouseEventArgs e)
    { if (_dragging) SetFromPoint(e.GetPosition(this)); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { _dragging = false; ReleaseMouseCapture(); }

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, w = ActualWidth;
        if (w < 60 || h < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-test surface
        double margin = 14, midY = h * 0.52;
        double track = w - margin * 2;
        double knobX = margin + ToPos(_value) * track;

        var cyan = Color.FromRgb(0x4F, 0xC3, 0xF7);
        var dim = Color.FromRgb(0x2A, 0x3A, 0x4E);

        // the un-used track beyond the stake — faint guide with the 50-unit tick marks
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x50, dim.R, dim.G, dim.B)), 2) { DashStyle = new DashStyle(new double[] { 1, 3 }, 0) };
        dc.DrawLine(guide, new Point(knobX, midY), new Point(margin + track, midY));
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, dim.R, dim.G, dim.B)), 1);
        foreach (double tv in new[] { 50.0, 250, 500, 750, 1000, 1250, 1500 })
        {
            double tx = margin + ToPos(tv) * track;
            dc.DrawLine(tickPen, new Point(tx, midY + 7), new Point(tx, midY + 11));
        }

        // the rope: a sagging, gently swaying curve from the left edge (the mascot) to the stake.
        // Two stacked quadratic waves make it read like a rope drifting in a stream.
        double slack = Math.Min(10, 3 + (knobX - margin) * 0.03);
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            const int SEG = 26;
            ctx.BeginFigure(new Point(margin, midY - 2), false, false);
            for (int i = 1; i <= SEG; i++)
            {
                double t = i / (double)SEG;
                double x = margin + t * (knobX - margin);
                double sag = Math.Sin(t * Math.PI) * slack;
                double sway = Math.Sin(_phase + t * 4.4) * (1.6 + slack * 0.35) * Math.Sin(t * Math.PI);
                ctx.LineTo(new Point(x, midY - 2 + sag + sway), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x38, cyan.R, cyan.G, cyan.B)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(cyan), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);

        // the stake knob
        var knobC = new Point(knobX, midY - 2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, cyan.R, cyan.G, cyan.B)), null, knobC, 11, 11);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x24)), new Pen(new SolidColorBrush(cyan), 2), knobC, 7.5, 7.5);
        dc.DrawEllipse(new SolidColorBrush(cyan), null, knobC, 2.6, 2.6);

        // value label riding above the stake
        var ft = new FormattedText($"{(int)_value} units", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11.5, new SolidColorBrush(Color.FromRgb(0xEA, 0xF6, 0xFF)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double lx = Math.Clamp(knobX - ft.Width / 2, 2, w - ft.Width - 2);
        dc.DrawText(ft, new Point(lx, midY - 24));
    }
}
