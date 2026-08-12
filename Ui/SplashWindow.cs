using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// The load-in moment: the mascot appears large over the app and FADES INTO it — a beat of
/// presence before the tools take over. Frameless, click-to-skip, closes itself. Built in code
/// so it stays one portable file; art comes from the packaged assets/mascot.jpg resource.
/// </summary>
public sealed class SplashWindow : Window
{
    public SplashWindow(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToOwner();
        owner.LocationChanged += (_, _) => SizeToOwner();
        owner.SizeChanged += (_, _) => SizeToOwner();

        var img = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(70),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x4F, 0xC3, 0xF7), BlurRadius = 70, ShadowDepth = 0, Opacity = 0.6 },
        };
        // The ROBOT — the app's own icon — filling the window as it fades into the app.
        try { img.Source = new BitmapImage(new Uri("pack://application:,,,/assets/eqavatar-512.png")); }
        catch { /* no art, no splash */ }

        // Fully opaque veil: nothing of the app leaks through until the robot fades out.
        var veil = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x06, 0x07, 0x0B)) };
        veil.Children.Add(img);
        Content = veil;
        Opacity = 0;
        MouseLeftButtonDown += (_, _) => FadeOutNow(fast: true);

        Loaded += (_, _) =>
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase() };
            fadeIn.Completed += (_, _) =>
            {
                var hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                hold.Tick += (_, _) => { hold.Stop(); FadeOutNow(fast: false); };
                hold.Start();
            };
            BeginAnimation(OpacityProperty, fadeIn);
        };
    }

    private bool _closing;
    private void FadeOutNow(bool fast)
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(fast ? 250 : 1000))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void SizeToOwner()
    {
        if (Owner is null) return;
        Left = Owner.Left; Top = Owner.Top;
        Width = Math.Max(200, Owner.ActualWidth > 0 ? Owner.ActualWidth : Owner.Width);
        Height = Math.Max(200, Owner.ActualHeight > 0 ? Owner.ActualHeight : Owner.Height);
    }
}
