using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
/// So this is a second window: borderless, fully transparent, no taskbar entry, and CLICK-THROUGH.
/// He tracks the owner's top-left corner, hides while it is minimised, and mirrors its topmost
/// state, which is what keeps him out of EverQuest's way: the app already drops Topmost when the
/// game takes focus, and the ghost drops with it.
///
/// THE GLOW STOPS AT THE APP'S EDGE, AND THE GHOST DOES NOT.
/// The bloom is meant to read as light spilling onto whatever is *behind* the app — over the
/// desktop, a browser, the game. Spilling it over the app's own chrome instead just looks like a
/// smudge on the title bar. But a window is one plane: it cannot be above the app for the ghost
/// and below it for the glow. So the z-order is settled once (always above, see Owner below) and
/// the glow is dealt with by CLIPPING instead — the halo is simply not painted over the rectangle
/// the app occupies.
///
/// That is why the art is drawn TWICE, in two hosts:
///
///   glow host — the art plus its DropShadow, clipped to everything OUTSIDE the owner's rectangle
///   body host — the art alone, clipped to everything INSIDE it
///
/// Every pixel is therefore painted exactly once: bloom-and-ghost where he overhangs, crisp ghost
/// where he is over the app. The clip lives on the HOST rather than on the Image because WPF
/// applies an element's own Clip before its Effect — clipping the image directly would blur the
/// cut edge and draw a hard-edged glow along it, which is the very artefact this is removing.
/// </summary>
public sealed class GhostWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TOOLWINDOW = 0x80;      // keeps it out of Alt-Tab

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    /// <summary>How far the white bloom reaches past the art, in device-independent pixels.</summary>
    private const double GlowBlur = 22;

    /// <summary>The bob, and the tilt, as the animations below use them.</summary>
    private const double BobUp = 2.5, BobDown = 3.5, TiltDegrees = 2.5;

    private readonly Window _owner;

    /// <summary>The art with its bloom. Clipped to the world outside the app.</summary>
    private readonly Image _artGlow = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(0.5, 0.5),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = GlowBlur, ShadowDepth = 0, Opacity = 0.55 },
    };

    /// <summary>The same art, no bloom. Clipped to the app's own rectangle, so the ghost stays
    /// whole and sharp where he crosses it.</summary>
    private readonly Image _artBody = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(0.5, 0.5),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Grid _glowHost = new();
    private readonly Grid _bodyHost = new();

    /// <summary>Where the ghost sits relative to the owner's top-left, in device-independent
    /// pixels, and how big he is. He overhangs up and left on purpose — that overhang is the
    /// whole point of moving him out of the window.</summary>
    public double OffsetX { get; set; } = -14;
    public double OffsetY { get; set; } = -16;
    public double Size { get; set; } = 86;

    /// <summary>
    /// Transparent margin between the art and the edge of the window, so the bloom fades to
    /// nothing instead of being guillotined by the window rectangle.
    ///
    /// THE SHARP CUT-OFF WAS NEVER IN THE PNG. The glow is a WPF DropShadowEffect, drawn at
    /// runtime; the hard edge was this window being exactly <see cref="Size"/> square, so the
    /// blur ran straight into the boundary and stopped dead. Padding the window is the same fix
    /// as padding the canvas would have been, without touching the art — and it stays correct if
    /// anyone changes the blur later, because it is derived from it rather than baked in.
    ///
    /// The arithmetic, which is why this is a property and not a magic number:
    ///   · the bloom reaches <see cref="GlowBlur"/> past the art
    ///   · the bob carries him up to <see cref="BobDown"/> further
    ///   · the tilt swings the corners of an s-wide square out by s·(cosθ + sinθ − 1) / 2
    ///   · plus one pixel, so the last faint pixel of glow is inside the window rather than on it
    /// </summary>
    private double Pad
    {
        get
        {
            double rad = TiltDegrees * Math.PI / 180.0;
            double tilt = Size * (Math.Cos(rad) + Math.Sin(rad) - 1) / 2;
            return Math.Ceiling(GlowBlur + Math.Max(BobUp, BobDown) + tilt + 1);
        }
    }

    public GhostWindow(Window owner)
    {
        _owner = owner;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;          // he must never take focus off the app or the game
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        IsHitTestVisible = false;
        Focusable = false;

        // OWNED, so he can never fall behind. Without this he is an independent top-level window
        // and Windows re-stacks him freely: clicking the app raised the app above him, and all you
        // saw was the sliver overhanging the corner. An owned window is always above its owner —
        // there is no z-order race left to lose.
        try { Owner = owner; } catch { /* owner not shown yet; the mascot is not worth throwing over */ }

        // Both images are the same picture in the same place. One shared transform group drives
        // them, so they cannot drift apart by a frame — two copies of the animation would.
        var rot = new RotateTransform(-TiltDegrees);
        var slide = new TranslateTransform();
        var move = new TransformGroup { Children = { rot, slide } };
        _artGlow.RenderTransform = move;
        _artBody.RenderTransform = move;

        // One fetch, one bitmap: the body follows whatever the glow copy is showing.
        _artBody.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source)) { Source = _artGlow });

        _glowHost.Children.Add(_artGlow);
        _bodyHost.Children.Add(_artBody);
        Content = new Grid { Children = { _glowHost, _bodyHost } };

        // The same bob and tilt as the in-window ghost, so moving him out of the frame does not
        // change how he behaves — only what he can float over.
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-BobUp, BobDown, TimeSpan.FromSeconds(2.7))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-TiltDegrees, TiltDegrees, TimeSpan.FromSeconds(3.6))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        SourceInitialized += (_, _) => EnsureClickThrough();

        _owner.LocationChanged += (_, _) => Reposition();
        _owner.SizeChanged     += (_, _) => Reposition();
        _owner.StateChanged    += (_, _) => Reposition();
        _owner.IsVisibleChanged += (_, _) => Reposition();
        _owner.Closed          += (_, _) => Close();
    }

    /// <summary>Point him at the same cached art the in-window ghost uses.</summary>
    public void Bind(string artName)
    {
        ArtCache.Bind(_artGlow, artName);
    }

    public void Follow()
    {
        Reposition();
        if (!IsVisible) { Show(); EnsureClickThrough(); }
    }

    /// <summary>
    /// Put <c>WS_EX_TRANSPARENT</c> back on, so every click lands on whatever is behind him.
    ///
    /// Re-applied rather than set once: WPF caches this window's extended style at creation and
    /// rewrites it from that cache whenever something makes it re-apply window settings — a
    /// Topmost flip on a transparent window will do it — and our bit is not in the cache. Setting
    /// it once at <c>SourceInitialized</c> is what leaves him silently swallowing clicks an hour
    /// into a session. It is two cheap P/Invokes; call it whenever he might have lost it.
    /// </summary>
    private void EnsureClickThrough()
    {
        try
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            int want = ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            if (ex != want) SetWindowLong(h, GWL_EXSTYLE, want);
        }
        catch { /* a mascot is never a reason to throw */ }
    }

    private void Reposition()
    {
        try
        {
            bool visible = _owner.IsVisible && _owner.WindowState != WindowState.Minimized;
            if (!visible) { if (IsVisible) Hide(); return; }
            if (!IsVisible) { Show(); EnsureClickThrough(); }

            // PointToScreen returns DEVICE pixels; a Window's Left/Top are device-independent
            // ones. On a scaled display the two differ, and using the wrong one parks the ghost
            // a couple of centimetres away from the corner it is supposed to hug.
            Point deviceOrigin = _owner.PointToScreen(new Point(0, 0));
            double scale = 1.0;
            if (PresentationSource.FromVisual(_owner)?.CompositionTarget is { } ct)
                scale = ct.TransformToDevice.M11 is > 0 and var m ? m : 1.0;

            double ownerX = deviceOrigin.X / scale;
            double ownerY = deviceOrigin.Y / scale;
            double pad = Pad;

            // The window grows by the padding on every side; shifting its origin back by the same
            // amount leaves the ghost himself exactly where he was.
            Left = ownerX + OffsetX - pad;
            Top = ownerY + OffsetY - pad;
            Width = Size + pad * 2;
            Height = Size + pad * 2;
            _artGlow.Width = _artGlow.Height = Size;
            _artBody.Width = _artBody.Height = Size;

            ApplyClips(ownerX, ownerY);

            // Follow the app's own topmost state so the ghost never covers EverQuest at the one
            // moment the app itself has politely stepped aside.
            if (Topmost != _owner.Topmost) { Topmost = _owner.Topmost; EnsureClickThrough(); }
        }
        catch { /* a mascot is never a reason to throw out of a layout pass */ }
    }

    /// <summary>
    /// Split the window into "over the app" and "over everything else", and give each host the
    /// half it is allowed to paint.
    ///
    /// The rectangle is the owner's, expressed in this window's own coordinates. If it cannot be
    /// worked out — the owner has no size yet during startup — both clips come off and the ghost
    /// falls back to being drawn once, bloom and all, which is what he did before any of this.
    /// </summary>
    private void ApplyClips(double ownerX, double ownerY)
    {
        double w = _owner.ActualWidth, h = _owner.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            _glowHost.Clip = null;
            _bodyHost.Clip = Geometry.Empty;
            return;
        }

        var app = new Rect(ownerX - Left, ownerY - Top, w, h);
        var all = new Rect(0, 0, Width, Height);

        // Glow everywhere the app is not…
        _glowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                                              new RectangleGeometry(all), new RectangleGeometry(app));
        // …and the plain ghost exactly where it is.
        _bodyHost.Clip = new RectangleGeometry(app);
    }
}
