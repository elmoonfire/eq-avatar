using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EQAvatar.Spike.Map;

namespace EQAvatar.Spike.Overlay;

/// <summary>
/// The in-game map overlay: a transparent, always-on-top window hosting the SAME
/// <see cref="MapViewElement"/> the Maps page uses — real zone walls, the heat overlay, the
/// trail, and the live marker. The Maps page drives it (ShowMap/SetLayers/SetHeat/PushLoc), so
/// whatever you have on screen is what floats over the game. "ghost" toggles click-through.
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
    private bool _clickThrough;   // start interactive so it can be dragged into place

    public MapOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 24;
            Top = wa.Top + 24;
            OverlayMap.Fit();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        ApplyClickThrough();
    }

    // ---- driven by the Maps page -----------------------------------------------------------

    public void ShowMap(MapData? data, string zoneName)
    {
        OverlayMap.SetMap(data);
        OverlayMap.SetLayers(labels: false, legend: false, extra: true);   // clean in-game look
        TitleLabel.Text = "◈  " + zoneName.ToUpperInvariant();
        OverlayMap.Fit();
    }

    /// <summary>
    /// Get out of the frame while something OCRs the game underneath.
    ///
    /// This window is unconditionally Topmost and deliberately sits over the game, which is
    /// exactly right for a map and exactly wrong for a screen read: ScreenText copies the
    /// ON-SCREEN pixels of the game's rectangle, so whatever this is drawing is read as though the
    /// game had drawn it. Hidden rather than merely un-topmosted, because "not topmost" still
    /// leaves it in front of a window that has just been brought forward.
    /// </summary>
    public void StepAside(bool aside)
    {
        // Show() ACTIVATES by default, and this window is the last thing that should. Coming back
        // after a re-instance it would take the foreground from EverQuest at the exact moment the
        // role is about to cast levitate and take a position fix — and every one of those checks
        // "is EQ the front window?" first. The run would spend a real instance charge, arrive in a
        // new instance, find it could do nothing, and park blaming the log. ShowActivated="False"
        // in the XAML is what actually stops it; this is the note explaining why it is there.
        if (aside) { if (IsVisible) { _hiddenForCapture = true; Hide(); } }
        else if (_hiddenForCapture) { _hiddenForCapture = false; Show(); }
    }

    private bool _hiddenForCapture;

    public void SetLayers(bool showHeat, bool showTrail)
    {
        OverlayMap.ShowHeat = showHeat;
        OverlayMap.ShowTrail = showTrail;
        OverlayMap.InvalidateVisual();
    }

    public void SetHeat(IReadOnlyList<Point> mapSpacePoints) => OverlayMap.SetHeat(mapSpacePoints);

    public void SetTether(double mapX, double mapY, double radiusUnits, bool on)
        => OverlayMap.SetTether(mapX, mapY, radiusUnits, on);

    public void PushLoc(double mapX, double mapY)
    {
        OverlayMap.PushLoc(mapX, mapY);
        CoordLabel.Text = $"map {mapX:0}, {mapY:0}";
    }

    // ---- chrome ----------------------------------------------------------------------------

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (!_clickThrough && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => OverlayMap.Fit();

    private void Grab_Click(object sender, RoutedEventArgs e)
    {
        _clickThrough = !_clickThrough;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = _clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        ClickThroughTag.Text = _clickThrough ? "click-through ON" : "interactive";
        ClickThroughTag.Foreground = _clickThrough
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0xE3, 0x8B))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA7, 0xB4));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
