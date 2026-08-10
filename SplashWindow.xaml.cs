using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EQAvatar.Spike;

/// <summary>
/// Quick, polished launch splash: the avatar fades in (~0.3s), holds a beat, then the whole
/// splash fades out (~0.3s) to reveal the app underneath. Reusable by the real EQ Avatar app.
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly Duration D = new(TimeSpan.FromSeconds(0.6));

    public SplashWindow() => InitializeComponent();

    /// <param name="onReveal">Called right before the splash fades out — show the main window here.</param>
    public void PlayThen(Action onReveal)
    {
        var fadeIn = new DoubleAnimation(0, 1, D) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        fadeIn.Completed += (s, e) =>
        {
            var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
            hold.Tick += (s2, e2) =>
            {
                hold.Stop();
                onReveal();                       // main window appears behind the splash
                var fadeOut = new DoubleAnimation(1, 0, D) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                fadeOut.Completed += (s3, e3) => Close();
                BeginAnimation(OpacityProperty, fadeOut);   // fade the whole splash away
            };
            hold.Start();
        };
        Stage.BeginAnimation(OpacityProperty, fadeIn);       // fade the avatar in
    }
}
