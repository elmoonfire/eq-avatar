using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// The ghost, floating free of the window.
///
/// Inside the app he is an <c>Image</c> in the title bar, and WPF clips a child to its parent —
/// no negative margin escapes the window, so he could only ever hover *inside* the chrome. A
/// mascot that overlaps whatever is behind the app has to be a window of its own.
///
/// So this is a second window: borderless, fully transparent, no taskbar entry, and — the part
/// that matters — CLICK-THROUGH. `WS_EX_TRANSPARENT` makes the whole thing invisible to the
/// mouse, so he floats over your browser without ever swallowing a click meant for it. He tracks
/// the owner's top-left corner, hides while it is minimised, and mirrors its topmost state, which
/// is what keeps him out of EverQuest's way: the app already drops Topmost when the game takes
/// focus, and the ghost drops with it.
///
/// The glow is a white DropShadow rather than the blue one the in-window version used. Over an
/// arbitrary background — a bright browser, a dark game — a white bloom reads as "lit from
/// within" against both, where a coloured one only works against dark.
/// </summary>
public sealed class GhostWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TOOLWINDOW = 0x80;      // keeps it out of Alt-Tab

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private readonly Window _owner;
    private readonly Image _art = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(0.5, 0.5),
        Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 },
    };

    /// <summary>Where the ghost sits relative to the owner's top-left, in device-independent
    /// pixels, and how big he is. He overhangs up and left on purpose — that overhang is the
    /// whole point of moving him out of the window.</summary>
    public double OffsetX { get; set; } = -14;
    public double OffsetY { get; set; } = -16;
    public double Size { get; set; } = 86;

    public GhostWindow(Window owner)
    {
        _owner = owner;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        IsHitTestVisible = false;
        Focusable = false;
        Width = Size;
        Height = Size;

        var rot = new RotateTransform(-2);
        var slide = new TranslateTransform();
        _art.RenderTransform = new TransformGroup { Children = { rot, slide } };
        Content = new Grid { Children = { _art } };

        // The same bob and tilt as the in-window ghost, so moving him out of the frame does not
        // change how he behaves — only what he can float over.
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2.5, 3.5, TimeSpan.FromSeconds(2.7))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-2.5, 2.5, TimeSpan.FromSeconds(3.6))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        };

        _owner.LocationChanged += (_, _) => Reposition();
        _owner.SizeChanged     += (_, _) => Reposition();
        _owner.StateChanged    += (_, _) => Reposition();
        _owner.IsVisibleChanged += (_, _) => Reposition();
        _owner.Closed          += (_, _) => Close();
    }

    /// <summary>Point him at the same cached art the in-window ghost uses.</summary>
    public void Bind(string artName)
    {
        ArtCache.Bind(_art, artName);
    }

    public void Follow()
    {
        Reposition();
        if (!IsVisible) Show();
    }

    private void Reposition()
    {
        try
        {
            bool visible = _owner.IsVisible && _owner.WindowState != WindowState.Minimized;
            if (!visible) { if (IsVisible) Hide(); return; }
            if (!IsVisible) Show();

            // PointToScreen returns DEVICE pixels; a Window's Left/Top are device-independent
            // ones. On a scaled display the two differ, and using the wrong one parks the ghost
            // a couple of centimetres away from the corner it is supposed to hug.
            Point deviceOrigin = _owner.PointToScreen(new Point(0, 0));
            double scale = 1.0;
            if (PresentationSource.FromVisual(_owner)?.CompositionTarget is { } ct)
                scale = ct.TransformToDevice.M11 is > 0 and var m ? m : 1.0;

            Left = deviceOrigin.X / scale + OffsetX;
            Top  = deviceOrigin.Y / scale + OffsetY;
            Width = Size;
            Height = Size;

            // Follow the app's own topmost state so the ghost never covers EverQuest at the one
            // moment the app itself has politely stepped aside.
            if (Topmost != _owner.Topmost) Topmost = _owner.Topmost;
        }
        catch { /* a mascot is never a reason to throw out of a layout pass */ }
    }
}
