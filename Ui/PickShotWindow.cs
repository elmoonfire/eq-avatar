using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQAvatar.Spike.Roles;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// "Show me what she is comparing against."
///
/// Opens from a pick tile's Ready badge and displays the picture that pick learned, magnified,
/// with the picked box outlined in orange over the pixels that surrounded it. That one look
/// answers questions no log line can: whether the box landed on the item or on the empty slot
/// beside it, whether it was learned from the bag you are using now, and whether an update or a
/// re-pick quietly changed it behind your back.
/// </summary>
public sealed class PickShotWindow : Window
{
    public PickShotWindow(string title, string subtitle, PickShot? shot, string? note = null)
    {
        Title = title;
        Owner = Application.Current?.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x18));
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 380;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0xE3, 0xFF)),
        });
        root.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB6, 0xCC)),
            Margin = new Thickness(0, 2, 0, 10),
        });

        byte[]? bytes = shot?.Bytes();
        if (bytes is null)
        {
            root.Children.Add(new TextBlock
            {
                Text = note ?? "No snapshot was saved for this pick — it was made before the app started keeping "
                             + "them. Re-pick it once and this window will show you exactly what she compares against.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCB, 0x6B)),
            });
        }
        else
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.StreamSource = new MemoryStream(bytes);
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.EndInit();
            src.Freeze();

            // Magnify small icons so the pixels are readable — and shrink big patches so the window
            // still fits. Scaling only by WIDTH made a tall patch produce a window taller than a
            // laptop screen, with the Close button below the bottom edge of a modal dialog.
            Rect work = SystemParameters.WorkArea;
            double maxW = Math.Min(420, work.Width * 0.75);
            double maxH = Math.Min(460, work.Height * 0.62);
            double fit = Math.Min(maxW / Math.Max(1, src.PixelWidth), maxH / Math.Max(1, src.PixelHeight));
            double scale = Math.Clamp(fit, 0.15, 6.0);
            double w = Math.Max(24, src.PixelWidth * scale), h = Math.Max(24, src.PixelHeight * scale);

            var img = new Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);   // show the real pixels

            var canvas = new Grid { Width = w, Height = h };
            canvas.Children.Add(img);

            // The picked box, drawn where it actually sat.
            var box = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x3D)),
                StrokeThickness = 2,
                // Clamped to the picture: a box drawn outside the frame it is supposed to describe
                // would be the one thing this window must never do.
                Width = Math.Clamp(shot!.RW * w, 2, w),
                Height = Math.Clamp(shot.RH * h, 2, h),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(Math.Clamp(shot.RX * w, 0, w - 2), Math.Clamp(shot.RY * h, 0, h - 2), 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Color.FromRgb(0xFF, 0x9E, 0x3D), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
            };
            canvas.Children.Add(box);

            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x57)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0F, 0x13)),
                Child = canvas, HorizontalAlignment = HorizontalAlignment.Left,
            });
            root.Children.Add(new TextBlock
            {
                Text = $"orange box = what she matches ({shot.BoxW}×{shot.BoxH} px)   ·   learned {shot.When:MMM d, HH:mm}",
                FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x93, 0xA8)),
                Margin = new Thickness(2, 6, 0, 0),
            });
            if (!string.IsNullOrWhiteSpace(note))
                root.Children.Add(new TextBlock
                {
                    Text = note, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCB, 0x6B)),
                    Margin = new Thickness(2, 8, 0, 0),
                });
        }

        var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
}
