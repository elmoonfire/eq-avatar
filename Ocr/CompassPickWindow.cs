using System;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// One-shot region picker: shows a captured game frame, the user drags a box over the compass,
/// OK saves it. Built in code (no XAML) so it stays a single portable file. Returns a rect
/// NORMALIZED to the frame, so window moves/resizes don't break the compass later.
/// </summary>
public sealed class CompassPickWindow : Window
{
    private readonly Image _img = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Rectangle _band = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        StrokeThickness = 1.6,
        Fill = new SolidColorBrush(Color.FromArgb(0x28, 0x4F, 0xC3, 0xF7)),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _hint = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF6, 0xFF)),
        Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x12, 0x1E)),
        Padding = new Thickness(10, 6, 10, 6), FontSize = 13,
    };
    private readonly Button _ok = new() { Content = "Use this region (Enter)", Padding = new Thickness(14, 6, 14, 6), IsEnabled = false };

    private enum DragMode { None, New, Move, L, R, T, B, TL, TR, BL, BR }
    private DragMode _mode = DragMode.None;
    private Point _start;
    private double _ox, _oy, _ow, _oh;               // the band's rect when the drag began
    private readonly double _frameW, _frameH;

    /// <summary>Normalized selection, valid when ShowDialog() returned true.</summary>
    public double NX, NY, NW, NH;

    /// <param name="frame">A captured game frame to draw the box on.</param>
    /// <param name="title">Window title — names whatever is being picked.</param>
    /// <param name="hint">The one line of instruction shown above the frame.</param>
    public CompassPickWindow(System.Drawing.Bitmap frame,
                             string title = "Pick the compass region",
                             string hint = "Drag a box around the COMPASS strip (make it opaque in-game first), then press Enter.")
    {
        Title = title;
        _hint.Text = hint;
        Width = Math.Min(1280, frame.Width + 40);
        Height = Math.Min(860, frame.Height + 120);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x18));
        _frameW = frame.Width; _frameH = frame.Height;

        using var ms = new MemoryStream();
        frame.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var src = new BitmapImage();
        src.BeginInit(); src.CacheOption = BitmapCacheOption.OnLoad; src.StreamSource = ms; src.EndInit();
        src.Freeze();
        _img.Source = src;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_hint, 0); grid.Children.Add(_hint);

        var host = new Grid();
        host.Children.Add(_img);
        _canvas.Children.Add(_band);
        host.Children.Add(_canvas);
        Grid.SetRow(host, 1); grid.Children.Add(host);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _ok.Click += (_, _) => Accept();
        bar.Children.Add(cancel); bar.Children.Add(_ok);
        Grid.SetRow(bar, 2); grid.Children.Add(bar);
        Content = grid;

        // ---- draw, then adjust: edges and corners resize, the middle moves, outside starts over.
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            Point p = e.GetPosition(_canvas);
            _mode = HitTest(p);
            _start = p;
            _ox = Canvas.GetLeft(_band); _oy = Canvas.GetTop(_band);
            _ow = _band.Width; _oh = _band.Height;
            if (_mode == DragMode.New)
            {
                _band.Visibility = Visibility.Visible;
                Canvas.SetLeft(_band, p.X); Canvas.SetTop(_band, p.Y);
                _band.Width = 0; _band.Height = 0;
            }
            _canvas.CaptureMouse();
        };
        _canvas.MouseMove += (_, e) =>
        {
            Point p = e.GetPosition(_canvas);
            if (_mode == DragMode.None)
            {
                _canvas.Cursor = CursorFor(HitTest(p));       // feedback before any button goes down
                return;
            }
            double dx = p.X - _start.X, dy = p.Y - _start.Y;
            double x = _ox, y = _oy, w = _ow, h = _oh;
            switch (_mode)
            {
                case DragMode.New:
                    x = Math.Min(p.X, _start.X); y = Math.Min(p.Y, _start.Y);
                    w = Math.Abs(p.X - _start.X); h = Math.Abs(p.Y - _start.Y);
                    break;
                case DragMode.Move: x = _ox + dx; y = _oy + dy; break;
                case DragMode.L: x = _ox + dx; w = _ow - dx; break;
                case DragMode.R: w = _ow + dx; break;
                case DragMode.T: y = _oy + dy; h = _oh - dy; break;
                case DragMode.B: h = _oh + dy; break;
                case DragMode.TL: x = _ox + dx; w = _ow - dx; y = _oy + dy; h = _oh - dy; break;
                case DragMode.TR: w = _ow + dx; y = _oy + dy; h = _oh - dy; break;
                case DragMode.BL: x = _ox + dx; w = _ow - dx; h = _oh + dy; break;
                case DragMode.BR: w = _ow + dx; h = _oh + dy; break;
            }
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            Canvas.SetLeft(_band, x); Canvas.SetTop(_band, y);
            _band.Width = w; _band.Height = h;
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _mode = DragMode.None; _canvas.ReleaseMouseCapture();
            // Small on purpose: this picker is also used for HP/mana bars, and a vertical bar is
            // only a few pixels wide on screen.
            _ok.IsEnabled = _band.Width > 5 && _band.Height > 3;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Enter && _ok.IsEnabled) Accept(); if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }

    /// <summary>What a press at this point would do to the existing box. Corners win over edges,
    /// edges over the middle, and anywhere outside starts a fresh box — so a bad first drag is
    /// never a reason to start over, just grab a side and pull.</summary>
    private DragMode HitTest(Point p)
    {
        if (_band.Visibility != Visibility.Visible || _band.Width < 2 || _band.Height < 2) return DragMode.New;
        const double grip = 8;
        double x = Canvas.GetLeft(_band), y = Canvas.GetTop(_band), w = _band.Width, h = _band.Height;
        bool nearL = Math.Abs(p.X - x) <= grip, nearR = Math.Abs(p.X - (x + w)) <= grip;
        bool nearT = Math.Abs(p.Y - y) <= grip, nearB = Math.Abs(p.Y - (y + h)) <= grip;
        bool inX = p.X >= x - grip && p.X <= x + w + grip;
        bool inY = p.Y >= y - grip && p.Y <= y + h + grip;
        if (nearL && nearT && inX && inY) return DragMode.TL;
        if (nearR && nearT && inX && inY) return DragMode.TR;
        if (nearL && nearB && inX && inY) return DragMode.BL;
        if (nearR && nearB && inX && inY) return DragMode.BR;
        if (nearL && inY) return DragMode.L;
        if (nearR && inY) return DragMode.R;
        if (nearT && inX) return DragMode.T;
        if (nearB && inX) return DragMode.B;
        if (p.X > x && p.X < x + w && p.Y > y && p.Y < y + h) return DragMode.Move;
        return DragMode.New;
    }

    private static Cursor CursorFor(DragMode m) => m switch
    {
        DragMode.L or DragMode.R => Cursors.SizeWE,
        DragMode.T or DragMode.B => Cursors.SizeNS,
        DragMode.TL or DragMode.BR => Cursors.SizeNWSE,
        DragMode.TR or DragMode.BL => Cursors.SizeNESW,
        DragMode.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void Accept()
    {
        // The image is Stretch=Uniform inside its host — convert canvas coords → frame coords.
        double scale = Math.Min(_canvas.ActualWidth / _frameW, _canvas.ActualHeight / _frameH);
        double offX = (_canvas.ActualWidth - _frameW * scale) / 2;
        double offY = (_canvas.ActualHeight - _frameH * scale) / 2;
        double fx = (Canvas.GetLeft(_band) - offX) / scale;
        double fy = (Canvas.GetTop(_band) - offY) / scale;
        double fw = _band.Width / scale, fh = _band.Height / scale;
        fx = Math.Clamp(fx, 0, _frameW); fy = Math.Clamp(fy, 0, _frameH);
        fw = Math.Clamp(fw, 1, _frameW - fx); fh = Math.Clamp(fh, 1, _frameH - fy);
        NX = fx / _frameW; NY = fy / _frameH; NW = fw / _frameW; NH = fh / _frameH;
        DialogResult = true;
        Close();
    }
}
