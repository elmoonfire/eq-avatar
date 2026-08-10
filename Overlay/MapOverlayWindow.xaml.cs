using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQAvatar.Spike.Overlay;

/// <summary>
/// A transparent, always-on-top, click-through overlay that floats above the desktop and
/// the game. It draws a stylised top-down zone, a glowing "you are here" orb, and a
/// breadcrumb trail. For the spike the orb walks a canned path so you can judge the look
/// and confirm the click-through behaviour; in the real app the points come from /loc.
/// </summary>
public partial class MapOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private IntPtr _hwnd;
    // Default: interactive (drag it anywhere, e.g. onto a second monitor).
    // "ghost" toggles click-through mode for floating it over the game.
    private bool _clickThrough = false;

    private readonly DispatcherTimer _walk = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly List<Point> _path = new();
    private int _step;

    public MapOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        _walk.Tick += Walk_Tick;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        ApplyClickThrough();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Park it in the top-right corner of the working area.
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 24;
        Top = wa.Top + 24;

        BuildCannedPath();
        _walk.Start();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = _clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        ClickThroughTag.Text = _clickThrough ? "click-through ON" : "interactive";
        ClickThroughTag.Foreground = new SolidColorBrush(
            _clickThrough ? Color.FromRgb(0x7C, 0xE3, 0x8B) : Color.FromRgb(0xFF, 0xCB, 0x6B));
    }

    /// <summary>Feed a real world position (mapped to canvas coords) once /loc is wired up.</summary>
    public void SetPlayerPosition(double canvasX, double canvasY, string? coordText = null)
    {
        Canvas.SetLeft(Orb, canvasX - Orb.Width / 2);
        Canvas.SetTop(Orb, canvasY - Orb.Height / 2);
        Trail.Points.Add(new Point(canvasX, canvasY));
        if (coordText != null) CoordLabel.Text = coordText;
    }

    private void BuildCannedPath()
    {
        _path.Clear();
        // A gentle wander inside the map area (canvas is ~400x300 after margins).
        double[,] pts =
        {
            {60,270},{95,230},{120,190},{160,175},{205,180},{240,150},
            {275,120},{300,95},{330,110},{350,150},{330,195},{300,225},
            {265,245},{225,255},{185,240},{150,210},{120,235},{95,265}
        };
        for (int i = 0; i < pts.GetLength(0); i++)
            _path.Add(new Point(pts[i, 0], pts[i, 1]));
        _step = 0;
        Trail.Points.Clear();
    }

    private void Walk_Tick(object? sender, EventArgs e)
    {
        if (_path.Count == 0) return;
        Point p = _path[_step % _path.Count];
        // Fake but plausible EQ-style coordinates for the readout.
        double locY = 500 - p.Y * 1.7;
        double locX = -350 + p.X * 1.4;
        SetPlayerPosition(p.X, p.Y, $"loc  {locY:0.0}, {locX:0.0}, 12.4");
        _step++;
        if (_step > 400) _step = 0; // keep it bounded
        if (Trail.Points.Count > 60) Trail.Points.RemoveAt(0); // fade the tail
    }

    private void Header_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_clickThrough && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void Grab_Click(object sender, RoutedEventArgs e)
    {
        _clickThrough = !_clickThrough;
        ApplyClickThrough();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _walk.Stop();
        base.OnClosed(e);
    }
}
