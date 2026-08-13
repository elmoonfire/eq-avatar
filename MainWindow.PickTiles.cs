using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The scene-tile pickers and the fire progress bar (partial class) — shared by the Questing
/// automation card and the Auto Merge page.
///
/// These are the Grind page's mode tiles (scene, TITLE, subtitle) grown a state: every pick a
/// role needs before it can run is a tile you click, and the tile itself says whether it has been
/// made. Not picked = an orange "✕ Not Ready" in the top-right; picked = a glowing green
/// "✓ Ready" in the top-left. The whole strip of tiles answers "can I press Run?" at a glance,
/// which a column of label + "picked"/"not picked" text never quite did.
///
/// Built in code, not XAML, for the same reason as the nav sections: the Grind page's SceneTile
/// styles live in PanelGrind's LOCAL Grid.Resources, referencing them from another panel is
/// exactly the out-of-scope StaticResource lookup that killed 0.9.25 at startup, and a tile
/// factory shared by two pages wants to exist once anyway.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One pick tile: scene art, TITLE, subtitle, optional tertiary line ("1 of 2"), a
    /// ready/not-ready badge, and a click that runs the pick.
    /// </summary>
    /// <param name="onBadgeInspect">Optional: makes the READY badge itself clickable, without
    /// running the pick. This is the "show me what you actually learned" door — a pick that has
    /// gone wrong looks exactly like one that hasn't until you can see the picture behind it.</param>
    private FrameworkElement MakePickTile(string artName, string title, string subtitle,
                                          string tertiary, bool ready, string tip, Action onClick,
                                          Action? onBadgeInspect = null)
    {
        var stack = new StackPanel();

        // ---- the scene, with the badge floated over it
        var sceneHost = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var art = new Image { Stretch = Stretch.UniformToFill };
        ArtCache.Bind(art, artName);
        sceneHost.Children.Add(new Border
        {
            Height = 62, CornerRadius = new CornerRadius(7), ClipToBounds = true,
            Background = Hex("#0B121E"), Child = art,
        });

        if (ready)
        {
            var check = new TextBlock
            {
                Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Hex("#49F27E"),
                Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x49, 0xF2, 0x7E), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.95 },
            };
            var word = new TextBlock
            {
                Text = "Ready", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Hex("#49F27E"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, word } };
            if (onBadgeInspect is not null)
                badgeRow.Children.Add(new TextBlock
                {
                    Text = "  🔍", FontSize = 9, Foreground = Hex("#BFE3FF"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6), Background = Hex("#C40E2416"), BorderBrush = Hex("#2E7D4F"),
                BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                Child = badgeRow,
                ToolTip = onBadgeInspect is null ? null
                    : "Click the badge to SEE what she compares against — the picture this pick learned, "
                    + "with your box drawn on it. The tile itself re-picks; the badge only looks.",
            };
            if (onBadgeInspect is not null)
            {
                badge.Cursor = Cursors.Hand;
                badge.MouseEnter += (_, _) => badge.BorderBrush = Hex("#49F27E");
                badge.MouseLeave += (_, _) => badge.BorderBrush = Hex("#2E7D4F");
                // Handled, or the tile underneath opens the picker and the user loses the very pick
                // they were trying to inspect.
                badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; onBadgeInspect(); };
            }
            sceneHost.Children.Add(badge);
        }
        else
        {
            var x = new TextBlock
            {
                Text = "✕", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Hex("#FF5A5A"),
                Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var word = new TextBlock
            {
                Text = "Not Ready", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Hex("#FFA24D"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6), Background = Hex("#C4241207"), BorderBrush = Hex("#7A4E20"),
                BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { x, word } },
            };
            sceneHost.Children.Add(badge);
        }
        stack.Children.Add(sceneHost);

        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold,
            Foreground = Hex("#DDE7F0"), HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 9.5, Foreground = Hex("#7E93A8"),
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (tertiary.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = tertiary, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Hex("#4FC3F7"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
            });

        var tile = new Border
        {
            Width = 158,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = ready ? Hex("#2E7D4F") : Hex("#26303F"),
            Background = Hex("#0B121E"),
            Padding = new Thickness(3, 3, 3, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = tip,
        };
        if (ready)
            tile.Effect = new DropShadowEffect { Color = Color.FromRgb(0x2E, 0x7D, 0x4F), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.45 };

        tile.MouseEnter += (_, _) => tile.BorderBrush = Hex("#4FC3F7");
        tile.MouseLeave += (_, _) => tile.BorderBrush = ready ? Hex("#2E7D4F") : Hex("#26303F");
        tile.MouseLeftButtonUp += (_, _) => onClick();
        return tile;
    }

    /// <summary>
    /// The fire bar: a progress track whose fill glows orange, pulses a highlight slowly to the
    /// right, and flickers like firelight.
    ///
    /// The animations are all self-contained (RelativeTransform on the highlight brush, keyframes
    /// on the glow's opacity, both RepeatBehavior.Forever), so the caller just rebuilds the bar
    /// when the fraction changes and never has to manage a storyboard.
    /// </summary>
    private static FrameworkElement MakeFireBar(double fraction, string caption)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;

        var host = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };

        var track = new Border
        {
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            Background = Hex("#10161F"),
            BorderBrush = Hex("#2A3646"),
            BorderThickness = new Thickness(1),
        };
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });

        if (fraction > 0.001)
        {
            var fireFill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xB3, 0x3A, 0x0E), 0.0),
                    new GradientStop(Color.FromRgb(0xE8, 0x6A, 0x1A), 0.55),
                    new GradientStop(Color.FromRgb(0xFF, 0x9E, 0x3D), 1.0),
                },
            };
            var glow = new DropShadowEffect
            { Color = Color.FromRgb(0xFF, 0x7A, 0x28), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.85 };

            // the slow pulse crossing the fill left → right, forever
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xE0, 0xA8), 0.30),
                    new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xE0, 0xA8), 0.50),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xE0, 0xA8), 0.70),
                },
            };
            var slide = new TranslateTransform(-1, 0);
            sheen.RelativeTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(2.6)) { RepeatBehavior = RepeatBehavior.Forever });

            // the firelight flicker: uneven keyframes so it never looks metronomic
            var flicker = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            double[] levels = { 0.85, 0.55, 0.95, 0.62, 0.78, 0.99, 0.58, 0.88 };
            double[] at = { 0.00, 0.23, 0.41, 0.55, 0.74, 0.86, 1.05, 1.30 };
            for (int i = 0; i < levels.Length; i++)
                flicker.KeyFrames.Add(new LinearDoubleKeyFrame(levels[i],
                    KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at[i]))));
            flicker.Duration = TimeSpan.FromSeconds(1.45);
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, flicker);

            var fill = new Border
            {
                CornerRadius = new CornerRadius(4.5),
                Background = fireFill,
                Effect = glow,
                Child = new Border { CornerRadius = new CornerRadius(4.5), Background = sheen },
            };
            Grid.SetColumn(fill, 0);
            split.Children.Add(fill);
        }

        track.Child = split;
        host.Children.Add(track);
        host.Children.Add(new TextBlock
        {
            Text = caption, FontSize = 9.5, Foreground = Hex("#B98A5A"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 2, 0),
        });
        return host;
    }
}
