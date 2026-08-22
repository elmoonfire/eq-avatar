using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EQAvatar.Spike;

/// <summary>
/// The launch splash: the avatar fades in, holds, and fades out to reveal the app underneath.
///
/// TIMED FROM ONE CLOCK, not from a chain of animation callbacks. The first version hung each
/// phase off the previous one's Completed event, which is fine until an animation is pre-empted or
/// never starts — and then onReveal is never called, the splash never closes, and the app sits
/// behind an opaque window with no way out. A single timeline that always runs to its end cannot
/// do that, and the backstop below cannot either.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Total time on screen. The fades are inside this, not on top of it — "the splash is
    /// 1.5 seconds" should mean the thing you can see lasts a second and a half.</summary>
    private const int FadeMs = 250, TotalMs = 1500;
    private const int HoldMs = TotalMs - FadeMs * 2;

    public SplashWindow() => InitializeComponent();

    private bool _revealed;

    /// <param name="onReveal">Called once the splash has finished — show/focus the main window here.
    /// Deliberately AFTER the fade-out rather than before it: calling it first let the main window
    /// activate itself over the top, so the last third of the splash played underneath a window
    /// nobody could see through and the whole thing ended in a hard cut.</param>
    public void PlayThen(Action onReveal)
    {
        void Finish()
        {
            if (_revealed) return;                 // exactly once, whichever path gets here first
            _revealed = true;
            try { onReveal(); } catch { /* the app must come up even if activation throws */ }
            Close();
        }

        // THE BACKSTOP. Whatever happens to the animations — a pre-empt, a device loss, a render
        // thread that never starts — the app is revealed. A splash that outstays its welcome is a
        // cosmetic annoyance; a splash that never leaves is an app you have to kill from Task
        // Manager, and it would look exactly like a hang on startup.
        var backstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TotalMs + 2000) };
        backstop.Tick += (_, _) => { backstop.Stop(); Finish(); };
        backstop.Start();

        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Stage.BeginAnimation(OpacityProperty, fadeIn);

        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeMs + HoldMs) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fadeOut.Completed += (_, _) => { backstop.Stop(); Finish(); };
            BeginAnimation(OpacityProperty, fadeOut);
        };
        hold.Start();
    }
}
