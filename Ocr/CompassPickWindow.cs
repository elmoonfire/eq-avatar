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
/// One-shot region picker: shows a captured game frame, the user marks a box on it, OK saves it.
/// Built in code (no XAML) so it stays a single portable file. Returns a rect NORMALIZED to the
/// frame, so window moves/resizes don't break the pick later.
///
/// TWO WAYS TO MARK IT.
///
/// FREE DRAG is the original, and it is right for anything whose size is a judgement call — a
/// compass strip, an HP bar, a bag area, a button.
///
/// THE SWATCH is for inventory slots, and it exists because free drag cannot do that job. An
/// inventory icon is about thirty pixels square, and this window shows a 2560-wide capture inside
/// about 1280 points — so one pixel of mouse movement is TWO frame pixels, and the box you dragged
/// is a pixel or two out on each side every time, differently each time. That matters more than it
/// sounds: the box's size sets the stride the bot searches with and the box's contents are the
/// reference it compares against, so a drag that spills into the next slot puts a neighbour's
/// pixels into the reference, and a drag that varies makes every pick a different experiment.
///
/// The swatch is a FIXED square, sized in real frame pixels, that you place with a click and nudge
/// with the arrow keys — one keypress, one pixel, however the image is scaled on screen. What you
/// see magnified beside it is exactly the pixels that get stored, which is the whole point: the
/// comparison size and the picked size are the same number by construction.
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
        Padding = new Thickness(10, 6, 10, 6), FontSize = 13, TextWrapping = TextWrapping.Wrap,
    };
    private readonly Button _ok = new() { Content = "Use this region (Enter)", Padding = new Thickness(14, 6, 14, 6), IsEnabled = false };

    private enum DragMode { None, New, Move, L, R, T, B, TL, TR, BL, BR }
    private DragMode _mode = DragMode.None;
    private Point _start;
    private double _ox, _oy, _ow, _oh;               // the band's rect when the drag began
    private readonly double _frameW, _frameH;
    private readonly BitmapSource _frameSrc;

    /// <summary>Normalized selection, valid when ShowDialog() returned true.</summary>
    public double NX, NY, NW, NH;

    // ---------------------------------------------------------------- swatch mode

    /// <summary>True while the fixed square is the thing being placed. False = the original drag.</summary>
    private bool _swatch;
    /// <summary>The swatch's size and position in REAL FRAME PIXELS — never in canvas points. Every
    /// number the user is shown, and every number that ends up stored, is measured here, so the
    /// scaling this window does to fit the frame on screen can never leak into the result.</summary>
    private int _swPx, _swX, _swY;
    private bool _placed;
    /// <summary>Magnified by a WHOLE number, never scaled to fit. At 168 px over a 32 px swatch the
    /// factor is 5.25, so nearest-neighbour draws some source columns six device pixels wide and
    /// others five — an uneven grid, in the one picture the user is asked to judge single-pixel
    /// alignment from.</summary>
    private readonly Image _loupe = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center };
    private const int LoupeTarget = 168;
    private readonly TextBlock _readout = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB6, 0xCC)), FontSize = 11.5,
        FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly Border _loupeBox;
    /// <summary>NOT focusable, deliberately. A WPF Button activates on Enter once it has focus, and
    /// clicking one gives it focus — so a user who switched to free drag, drew a box, and pressed
    /// Enter exactly as the hint tells them to would toggle the mode again and watch their box
    /// vanish instead of accepting it.</summary>
    private readonly Button _modeBtn = new()
    { Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Focusable = false };

    /// <summary>The size the user settled on, so the caller can offer it again next time. Same
    /// square every pick is the entire point — a reference that changes size between picks is a
    /// different experiment each time.</summary>
    public int SwatchPx => _swPx;

    /// <summary>True when the result came from the FIXED SQUARE. The caller persists the size only
    /// then — a size wheeled to and then abandoned for a free drag was never settled on.</summary>
    public bool UsedSwatch { get; private set; }

    /// <summary>Smallest and largest square worth offering. The floor is about the size of the
    /// glyph inside a slot; the ceiling is comfortably past any slot on any display.</summary>
    public const int MinSwatch = 8, MaxSwatch = 128;

    /// <param name="frame">A captured game frame to draw the box on.</param>
    /// <param name="title">Window title — names whatever is being picked.</param>
    /// <param name="hint">The one line of instruction shown above the frame.</param>
    /// <param name="swatchPx">Non-zero offers the fixed square at this size, in frame pixels, and
    /// starts in that mode. Zero keeps the original drag-only picker, which is what every caller
    /// that isn't picking an inventory icon wants.</param>
    public CompassPickWindow(System.Drawing.Bitmap frame,
                             string title = "Pick the compass region",
                             string hint = "Drag a box around the COMPASS strip (make it opaque in-game first), then press Enter.",
                             int swatchPx = 0)
    {
        Title = title;
        Width = Math.Min(1280, frame.Width + 40);
        Height = Math.Min(900, frame.Height + 190);
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
        _frameSrc = src;

        _swatch = swatchPx > 0;
        _swPx = Math.Clamp(swatchPx > 0 ? swatchPx : 32, MinSwatch, MaxSwatch);
        _swX = (int)(_frameW / 2); _swY = (int)(_frameH / 2);
        _hint.Text = _swatch ? SwatchHint(hint) : hint;

        RenderOptions.SetBitmapScalingMode(_loupe, BitmapScalingMode.NearestNeighbor);
        _loupeBox = new Border
        {
            CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0F, 0x13)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x57)), BorderThickness = new Thickness(1),
            Padding = new Thickness(5), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, Width = LoupeTarget + 14,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "EXACTLY WHAT GETS STORED", FontSize = 8.5, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x7C, 0x9A)),
                        Margin = new Thickness(0, 0, 0, 4),
                    },
                    _loupe, _readout,
                },
            },
            Visibility = _swatch ? Visibility.Visible : Visibility.Collapsed,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { Margin = new Thickness(0) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_hint, 0); head.Children.Add(_hint);
        Grid.SetColumn(_loupeBox, 1); head.Children.Add(_loupeBox);
        Grid.SetRow(head, 0); grid.Children.Add(head);

        var host = new Grid();
        host.Children.Add(_img);
        _canvas.Children.Add(_band);
        host.Children.Add(_canvas);
        Grid.SetRow(host, 1); grid.Children.Add(host);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _ok.Click += (_, _) => Accept();
        _modeBtn.Visibility = swatchPx > 0 ? Visibility.Visible : Visibility.Collapsed;
        _modeBtn.Click += (_, _) => SetMode(!_swatch);
        UpdateModeButton();
        bar.Children.Add(_modeBtn);
        bar.Children.Add(cancel); bar.Children.Add(_ok);
        Grid.SetRow(bar, 2); grid.Children.Add(bar);
        Content = grid;

        // ---- draw, then adjust: edges and corners resize, the middle moves, outside starts over.
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            Point p = e.GetPosition(_canvas);
            if (_swatch)
            {
                // Inside the square, a press starts a drag that moves it; anywhere else it jumps
                // here. Either way it is placed, so Enter becomes available immediately.
                _mode = HitTest(p) == DragMode.Move ? DragMode.Move : DragMode.New;
                _start = p;
                if (_mode == DragMode.New) CentreSwatchOn(p);
                _ox = _swX; _oy = _swY;
                _placed = true;
                _ok.IsEnabled = true;
                DrawSwatch();
                _canvas.CaptureMouse();
                Focus();
                return;
            }
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
            Focus();                     // so Enter lands on this window, not on whatever was clicked last
        };
        _canvas.MouseMove += (_, e) =>
        {
            Point p = e.GetPosition(_canvas);
            if (_swatch)
            {
                if (_mode == DragMode.Move)
                {
                    // Converted through the view scale so a drag moves the square by the number of
                    // FRAME pixels the mouse actually travelled, not by canvas points.
                    (double sc, _, _) = View();
                    if (sc > 0)
                    {
                        _swX = ClampX((int)Math.Round(_ox + (p.X - _start.X) / sc));
                        _swY = ClampY((int)Math.Round(_oy + (p.Y - _start.Y) / sc));
                    }
                }
                else if (!_placed || _mode == DragMode.New) CentreSwatchOn(p);
                else { _canvas.Cursor = HitTest(p) == DragMode.Move ? Cursors.SizeAll : Cursors.Cross; return; }
                DrawSwatch();
                return;
            }
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
            if (_swatch) return;                            // placement is what enables OK there
            // Small on purpose: this picker is also used for HP/mana bars, and a vertical bar is
            // only a few pixels wide on screen.
            _ok.IsEnabled = _band.Width > 5 && _band.Height > 3;
        };

        // The wheel resizes the square. One notch, one pixel — this is the number that has to match
        // an inventory slot, and it is easier to see the fit than to know it.
        _canvas.MouseWheel += (_, e) =>
        {
            if (!_swatch) return;
            Resize(_swPx + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); return; }
            if (e.Key == Key.Enter && _ok.IsEnabled) { Accept(); return; }
            if (!_swatch) return;

            // ONE KEYPRESS, ONE FRAME PIXEL — regardless of how far the frame has been scaled down
            // to fit this window. It is the only way to place a thirty-pixel square exactly when the
            // mouse cannot resolve better than two pixels.
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left: _swX = ClampX(_swX - step); break;
                case Key.Right: _swX = ClampX(_swX + step); break;
                case Key.Up: _swY = ClampY(_swY - step); break;
                case Key.Down: _swY = ClampY(_swY + step); break;
                // ONE PIXEL, always. `+` is Shift and `=` is not, so a step scaled by Shift made the
                // key labelled + jump ten while the key labelled − moved one.
                case Key.OemPlus or Key.Add: Resize(_swPx + 1); return;
                case Key.OemMinus or Key.Subtract: Resize(_swPx - 1); return;
                default: return;
            }
            _placed = true;
            _ok.IsEnabled = true;
            DrawSwatch();
            e.Handled = true;
        };

        Loaded += (_, _) => { if (_swatch) { Focus(); DrawSwatch(); } };
        // The band is positioned through the view scale, and maximising the window changes it. Without
        // this the square is drawn over the wrong part of the frame until the next mouse move — and
        // worse, HitTest is measuring against that stale rectangle, so a click meant to place it
        // reads as a grab and the square jumps.
        SizeChanged += (_, _) => { if (_swatch) DrawSwatch(); };
    }

    private static string SwatchHint(string hint) =>
        hint + "\n\nA FIXED SQUARE, the exact size the bot compares with. Click to place it, drag it or use "
             + "the ARROW KEYS to nudge it one pixel at a time (Shift = ten), and the WHEEL or +/− to resize it. "
             + "Match it to one inventory slot — what you see magnified on the right is exactly the pixels that "
             + "get stored. Switch to free drag for anything bigger.";

    private void SetMode(bool swatch)
    {
        _swatch = swatch;
        _loupeBox.Visibility = swatch ? Visibility.Visible : Visibility.Collapsed;
        _band.Visibility = swatch ? Visibility.Visible : Visibility.Collapsed;
        _ok.IsEnabled = swatch && _placed;
        _mode = DragMode.None;
        UpdateModeButton();
        if (swatch) DrawSwatch();
    }

    private void UpdateModeButton() =>
        _modeBtn.Content = _swatch ? "Free drag instead" : "Back to the fixed square";

    private void Resize(int px)
    {
        _swPx = Math.Clamp(px, MinSwatch, MaxSwatch);
        _swX = ClampX(_swX); _swY = ClampY(_swY);
        DrawSwatch();
    }

    private int ClampX(int x) => (int)Math.Clamp(x, 0, Math.Max(0, _frameW - _swPx));
    private int ClampY(int y) => (int)Math.Clamp(y, 0, Math.Max(0, _frameH - _swPx));

    private void CentreSwatchOn(Point p)
    {
        (double sc, double ox, double oy) = View();
        if (sc <= 0) return;
        _swX = ClampX((int)Math.Round((p.X - ox) / sc - _swPx / 2.0));
        _swY = ClampY((int)Math.Round((p.Y - oy) / sc - _swPx / 2.0));
    }

    /// <summary>Scale and letterbox offsets of the frame inside the canvas — the image is drawn
    /// Stretch=Uniform, so everything has to be converted through these.</summary>
    private (double Scale, double OffX, double OffY) View()
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return (0, 0, 0);
        double sc = Math.Min(_canvas.ActualWidth / _frameW, _canvas.ActualHeight / _frameH);
        return (sc, (_canvas.ActualWidth - _frameW * sc) / 2, (_canvas.ActualHeight - _frameH * sc) / 2);
    }

    private void DrawSwatch()
    {
        (double sc, double ox, double oy) = View();
        if (sc <= 0) return;
        _band.Visibility = Visibility.Visible;
        Canvas.SetLeft(_band, ox + _swX * sc);
        Canvas.SetTop(_band, oy + _swY * sc);
        _band.Width = Math.Max(1, _swPx * sc);
        _band.Height = Math.Max(1, _swPx * sc);

        _readout.Text = $"{_swPx} × {_swPx} px  at {_swX},{_swY}\n{_swPx * _swPx * 3:N0} numbers compared";
        try
        {
            // Cropped straight out of the frame the user is looking at, so the magnified picture and
            // the stored pixels cannot be different things.
            int w = (int)Math.Min(_swPx, _frameW - _swX), h = (int)Math.Min(_swPx, _frameH - _swY);
            if (w > 0 && h > 0)
            {
                _loupe.Source = new CroppedBitmap(_frameSrc, new Int32Rect(_swX, _swY, w, h));
                // Stretch.Fill at an exactly integral size: every source pixel becomes a k×k block,
                // so the grid the user is judging alignment against is even. (Stretch.None would
                // ignore the size, and a LayoutTransform on top of it would scale by k twice.)
                int k = Math.Max(1, LoupeTarget / Math.Max(1, _swPx));
                _loupe.Width = w * k; _loupe.Height = h * k;
            }
        }
        catch { /* a crop against the very edge is not worth an exception */ }
    }

    /// <summary>What a press at this point would do to the existing box. Corners win over edges,
    /// edges over the middle, and anywhere outside starts a fresh box — so a bad first drag is
    /// never a reason to start over, just grab a side and pull.</summary>
    private DragMode HitTest(Point p)
    {
        if (_band.Visibility != Visibility.Visible || _band.Width < 2 || _band.Height < 2) return DragMode.New;
        double x = Canvas.GetLeft(_band), y = Canvas.GetTop(_band), w = _band.Width, h = _band.Height;
        // The swatch has no resize grips — its size is a number, not a drag — so the only question
        // is whether the press landed on it.
        if (_swatch)
            return p.X > x && p.X < x + w && p.Y > y && p.Y < y + h ? DragMode.Move : DragMode.New;

        const double grip = 8;
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
        if (_swatch)
        {
            // Straight from the pixel numbers. No canvas coordinates, no scale, no rounding that
            // could make the stored box a pixel different from the one that was shown.
            NX = _swX / _frameW; NY = _swY / _frameH;
            NW = _swPx / _frameW; NH = _swPx / _frameH;
            UsedSwatch = true;
            DialogResult = true;
            Close();
            return;
        }

        // The image is Stretch=Uniform inside its host — convert canvas coords → frame coords.
        (double scale, double offX, double offY) = View();
        if (scale <= 0) { DialogResult = false; Close(); return; }
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
