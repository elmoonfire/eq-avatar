using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EQAvatar.Spike.Map;

/// <summary>
/// The map surface: draws a parsed zone (walls + labels), the session heatmap, the /loc trail
/// and a live position marker, with mouse pan + wheel zoom. Rendering follows the Companion's
/// MapCanvas design (MIT): geometry is baked once per (map, floor, layers) state into frozen
/// per-colour StreamGeometries in MAP space, then drawn under a single transform — pan/zoom
/// never re-tessellates. Labels, heat, trail and the marker are drawn per frame in screen
/// space (they are few).
///
/// Colour note: classic map files draw black-on-white; our surface is dark navy, so
/// near-black strokes are lifted to a light slate rather than vanishing. Brewall's coloured
/// strokes pass through untouched.
/// </summary>
public sealed class MapViewElement : FrameworkElement
{
    private MapData? _map;
    private IReadOnlyList<FloorBand> _bands = Array.Empty<FloorBand>();
    private int? _activeBand;                       // null = All levels
    private bool[] _layers = { true, true, false, true };   // 0 geometry always; 1 labels; 2 legend; 3 extra
    public bool ShowHeat, ShowTrail = true, ShowMarker = true;

    // view state: map-space centre + scale (screen px per map unit)
    private double _scale = 1, _cx, _cy;
    private bool _fitted;
    private Point _dragStart; private (double cx, double cy) _dragOrigin; private bool _dragging;

    // live position (map space) + trail (newest last)
    private Point? _marker;
    private readonly List<Point> _trail = new();
    private const int TrailMax = 600;

    // heat: aggregated per render from raw loc points (map space)
    private IReadOnlyList<Point> _heatPts = Array.Empty<Point>();

    // baked geometry: colour -> geometry, for the current (map, band, layers)
    private List<(Pen pen, StreamGeometry geo)>? _baked;
    private double _bakedForScale = -1;

    public event Action? ViewChanged;

    public MapViewElement()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
    }

    // ---- public surface -------------------------------------------------------------------

    public MapData? Map => _map;
    public IReadOnlyList<FloorBand> Bands => _bands;
    public int? ActiveBand => _activeBand;

    public void SetMap(MapData? map)
    {
        _map = map;
        _bands = map is null || map.ZLevels.Length < 2 ? Array.Empty<FloorBand>() : FloorSlice.Bands(map.ZLevels);
        _activeBand = null;
        _baked = null; _fitted = false;
        _trail.Clear(); _marker = null;
        InvalidateVisual();
    }

    public void SetLayers(bool labels, bool legend, bool extra)
    {
        _layers = new[] { true, labels, legend, extra };
        _baked = null;
        InvalidateVisual();
    }

    public void SetBand(int? band)
    {
        _activeBand = band is null ? null : Math.Clamp(band.Value, 0, Math.Max(0, _bands.Count - 1));
        _baked = null;
        InvalidateVisual();
    }

    public void SetHeat(IReadOnlyList<Point> mapSpacePoints) { _heatPts = mapSpacePoints; InvalidateVisual(); }

    private Point? _tether;                  // tether anchor in map space
    private double _tetherR;                 // radius in map units

    /// <summary>Show (or clear) the Grind tether circle: anchor in MAP space + radius in units.</summary>
    public void SetTether(double mapX, double mapY, double radiusUnits, bool on)
    {
        Point? next = on ? new Point(mapX, mapY) : null;
        if (next == _tether && Math.Abs(radiusUnits - _tetherR) < 0.5) return;
        _tether = next; _tetherR = radiusUnits;
        InvalidateVisual();
    }

    /// <summary>The heat points currently loaded (map space) — lets the overlay mirror them.</summary>
    public IReadOnlyList<Point> HeatPoints => _heatPts;

    /// <summary>Live /loc in map space. Appends to the trail and moves the marker.</summary>
    public void PushLoc(double mapX, double mapY)
    {
        _marker = new Point(mapX, mapY);
        if (_trail.Count == 0 || (_trail[^1] - _marker.Value).Length > 1.0) _trail.Add(_marker.Value);
        if (_trail.Count > TrailMax) _trail.RemoveAt(0);
        InvalidateVisual();
    }

    public void Fit()
    {
        if (_map is null || _map.Bounds.IsEmpty || ActualWidth < 8 || ActualHeight < 8) return;
        MapBounds b = _map.Bounds;
        double pad = 1.08;
        _scale = Math.Min(ActualWidth / Math.Max(1, b.Width * pad), ActualHeight / Math.Max(1, b.Height * pad));
        _scale = Math.Clamp(_scale, 0.0005, 200);
        _cx = (b.MinX + b.MaxX) / 2; _cy = (b.MinY + b.MaxY) / 2;
        _fitted = true;
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void ZoomStep(double factor)
    {
        _scale = Math.Clamp(_scale * factor, 0.0005, 200);
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    // ---- input ----------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_map is null) return;
        Point at = e.GetPosition(this);
        (double mx, double my) = Unproject(at);
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        _scale = Math.Clamp(_scale * factor, 0.0005, 200);
        // keep the map point under the cursor fixed
        _cx = mx - (at.X - ActualWidth / 2) / _scale;
        _cy = my - (at.Y - ActualHeight / 2) / _scale;
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_map is null) return;
        _dragging = true; _dragStart = e.GetPosition(this); _dragOrigin = (_cx, _cy);
        CaptureMouse(); Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        Point p = e.GetPosition(this);
        _cx = _dragOrigin.cx - (p.X - _dragStart.X) / _scale;
        _cy = _dragOrigin.cy - (p.Y - _dragStart.Y) / _scale;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false; ReleaseMouseCapture(); Cursor = Cursors.Arrow; ViewChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!_fitted) Fit();
    }

    // ---- projection -----------------------------------------------------------------------

    private Point Project(double mapX, double mapY)
        => new((mapX - _cx) * _scale + ActualWidth / 2, (mapY - _cy) * _scale + ActualHeight / 2);

    private (double, double) Unproject(Point s)
        => ((s.X - ActualWidth / 2) / _scale + _cx, (s.Y - ActualHeight / 2) / _scale + _cy);

    // ---- rendering ------------------------------------------------------------------------

    private static Color LiftDark(byte r, byte g, byte b)
        => r + g + b < 90 ? Color.FromRgb(0xC2, 0xCB, 0xD8) : Color.FromRgb(r, g, b);

    private bool SegVisible(int i)
    {
        if (_map is null) return false;
        int layer = _map.SegLayer[i];
        if (!_layers[layer]) return false;
        if (_activeBand is int band && layer != EqMapParser.LegendLayer)
        {
            double z = FloorSlice.SegmentZ(_map.Coords[i * 6 + 2], _map.Coords[i * 6 + 5]);
            (double lo, double hi) = FloorSlice.BandRange(_bands, band);
            if (z < lo || z > hi) return false;
        }
        return true;
    }

    private void Bake()
    {
        _baked = new List<(Pen, StreamGeometry)>();
        if (_map is null) return;
        // group visible segments by colour
        var groups = new Dictionary<int, StreamGeometry>();
        var contexts = new Dictionary<int, StreamGeometryContext>();
        for (int i = 0; i < _map.SegmentCount; i++)
        {
            if (!SegVisible(i)) continue;
            Color c = LiftDark(_map.SegRgb[i * 3], _map.SegRgb[i * 3 + 1], _map.SegRgb[i * 3 + 2]);
            int key = (c.R << 16) | (c.G << 8) | c.B;
            if (!groups.TryGetValue(key, out StreamGeometry? geo))
            {
                geo = new StreamGeometry();
                groups[key] = geo;
                contexts[key] = geo.Open();
            }
            StreamGeometryContext ctx = contexts[key];
            ctx.BeginFigure(new Point(_map.Coords[i * 6], _map.Coords[i * 6 + 1]), false, false);
            ctx.LineTo(new Point(_map.Coords[i * 6 + 3], _map.Coords[i * 6 + 4]), true, false);
        }
        foreach ((int key, StreamGeometryContext ctx) in contexts) ctx.Close();
        foreach ((int key, StreamGeometry geo) in groups)
        {
            geo.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromRgb((byte)(key >> 16), (byte)(key >> 8), (byte)key)), 1);
            _baked.Add((pen, geo));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // surface
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x1E)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_map is null)
        {
            var ft = Text("No map loaded — pick a zone above.", 13, Color.FromRgb(0x5D, 0x68, 0x78));
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
            return;
        }
        if (!_fitted) Fit();
        if (_baked is null) Bake();

        // geometry under one transform; pens sized so strokes stay ~1.1px at any zoom
        var m = new Matrix(_scale, 0, 0, _scale, ActualWidth / 2 - _cx * _scale, ActualHeight / 2 - _cy * _scale);
        dc.PushTransform(new MatrixTransform(m));
        foreach ((Pen pen, StreamGeometry geo) in _baked!)
        {
            if (Math.Abs(_bakedForScale - _scale) > 0.0001) pen.Thickness = 1.1 / _scale;
            dc.DrawGeometry(null, pen, geo);
        }
        _bakedForScale = _scale;
        dc.Pop();

        DrawHeat(dc);
        DrawTrail(dc);
        DrawTether(dc);
        DrawLabels(dc);
        DrawMarker(dc);
    }

    /// <summary>The Grind tether: a dashed circle around the anchor — the pen the bot stays inside.</summary>
    private void DrawTether(DrawingContext dc)
    {
        if (_tether is not Point tp || _tetherR <= 0) return;
        Point s = Project(tp.X, tp.Y);
        double r = _tetherR * _scale;
        if (r < 3 || s.X < -r - 40 || s.Y < -r - 40 || s.X > ActualWidth + r + 40 || s.Y > ActualHeight + r + 40) return;
        var cyan = Color.FromRgb(0x4F, 0xC3, 0xF7);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x14, cyan.R, cyan.G, cyan.B)), null, s, r, r);
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(0xB4, cyan.R, cyan.G, cyan.B)), 1.5)
        { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        dc.DrawEllipse(null, ring, s, r, r);
        var cross = new Pen(new SolidColorBrush(Color.FromArgb(0xB4, cyan.R, cyan.G, cyan.B)), 1.2);
        dc.DrawLine(cross, new Point(s.X - 6, s.Y), new Point(s.X + 6, s.Y));
        dc.DrawLine(cross, new Point(s.X, s.Y - 6), new Point(s.X, s.Y + 6));
    }

    private FormattedText Text(string s, double size, Color c) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, new SolidColorBrush(c),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabels(DrawingContext dc)
    {
        if (_map is null || !_layers[1]) return;
        // zoom gating by size class: 3 (zone connections) always, 2 when zoomed in a bit, 1 close-up
        double fitScale = _map.Bounds.IsEmpty ? _scale
            : Math.Min(ActualWidth / Math.Max(1, _map.Bounds.Width), ActualHeight / Math.Max(1, _map.Bounds.Height));
        double rel = _scale / Math.Max(0.0001, fitScale);
        int minClass = rel >= 3.5 ? 1 : rel >= 1.6 ? 2 : 3;

        var shadow = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
        int drawn = 0;
        foreach (MapPoint p in _map.Points)
        {
            if (drawn >= 450) break;
            if (p.Layer == EqMapParser.LegendLayer && !_layers[2]) continue;
            if (p.Layer != EqMapParser.LegendLayer && !_layers[p.Layer <= 3 ? p.Layer : 1]) continue;
            if (p.Size < minClass) continue;
            if (_activeBand is int band && p.Layer != EqMapParser.LegendLayer)
            {
                (double lo, double hi) = FloorSlice.BandRange(_bands, band);
                if (p.Z < lo || p.Z > hi) continue;
            }
            Point s = Project(p.X, p.Y);
            if (s.X < -80 || s.Y < -20 || s.X > ActualWidth + 20 || s.Y > ActualHeight + 20) continue;

            Color c = LiftDark(p.R, p.G, p.B);
            dc.DrawEllipse(new SolidColorBrush(c), null, s, 2.2, 2.2);
            if (p.Display.Length > 0)
            {
                double size = p.Size == 3 ? 12 : p.Size == 2 ? 11 : 10;
                FormattedText ft = Text(p.Display, size, c);
                FormattedText sh = Text(p.Display, size, Color.FromArgb(0xB0, 0, 0, 0));
                dc.DrawText(sh, new Point(s.X + 5, s.Y - ft.Height / 2 + 1));
                dc.DrawText(ft, new Point(s.X + 4, s.Y - ft.Height / 2));
            }
            drawn++;
        }
    }

    private void DrawHeat(DrawingContext dc)
    {
        if (!ShowHeat || _heatPts.Count == 0) return;
        // aggregate into a screen-space grid; alpha by density
        const double cell = 14;
        var counts = new Dictionary<(int, int), int>();
        foreach (Point mp in _heatPts)
        {
            Point s = Project(mp.X, mp.Y);
            if (s.X < -cell || s.Y < -cell || s.X > ActualWidth + cell || s.Y > ActualHeight + cell) continue;
            (int, int) k = ((int)(s.X / cell), (int)(s.Y / cell));
            counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
        }
        if (counts.Count == 0) return;
        int max = counts.Values.Max();
        foreach (((int gx, int gy), int n) in counts)
        {
            double t = Math.Sqrt((double)n / max);                       // perceptual-ish ramp
            byte a = (byte)(40 + t * 150);
            Color c = t < 0.5
                ? Color.FromArgb(a, (byte)(255 * t * 2), 200, 60)         // green→amber
                : Color.FromArgb(a, 255, (byte)(200 * (1 - (t - 0.5) * 2)), 40);   // amber→red
            dc.DrawRoundedRectangle(new SolidColorBrush(c), null,
                new Rect(gx * cell, gy * cell, cell, cell), 4, 4);
        }
    }

    private void DrawTrail(DrawingContext dc)
    {
        if (!ShowTrail || _trail.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x4F, 0xC3, 0xF7)), 1.6)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(Project(_trail[0].X, _trail[0].Y), false, false);
            for (int i = 1; i < _trail.Count; i++) ctx.LineTo(Project(_trail[i].X, _trail[i].Y), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawMarker(DrawingContext dc)
    {
        if (!ShowMarker || _marker is not Point mp) return;
        Point s = Project(mp.X, mp.Y);
        var cyan = Color.FromRgb(0x4F, 0xC3, 0xF7);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x38, cyan.R, cyan.G, cyan.B)), null, s, 11, 11);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(cyan), 1.6), s, 11, 11);
        dc.DrawEllipse(new SolidColorBrush(cyan), null, s, 3.2, 3.2);
    }
}
