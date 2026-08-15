using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace EQAvatar.Spike;

/// <summary>
/// The wordmark in the title bar (partial class).
///
/// SELF-CONTAINED, like <c>MainWindow.Sections.cs</c> and <c>MainWindow.Support.cs</c>. It edits
/// no XAML: it finds the TextBlock the markup already created — the one sitting next to the ghost
/// — and restyles it in code. That matters more here than usual, because the title lives in the
/// same forty lines of <c>MainWindow.xaml</c> as the ghost animation and the profile chip, which
/// belong to other workstreams.
///
/// THE FONT IS EMBEDDED, not installed. <c>assets/fonts/Michroma-Regular.ttf</c> is compiled into
/// the exe as a WPF resource and referenced by pack URI, so it renders identically on a machine
/// that has never heard of it and there is nothing for a member to install. Michroma is SIL Open
/// Font License with <c>fsType 0</c> — embedding is explicitly permitted, and the licence ships
/// beside it in the same folder.
///
/// EVERY NUMBER IS DERIVED FROM THE FONT'S OWN METRICS. "Fill 60% of the title bar" is a statement
/// about the CAPITALS, which is not the same as a point size: cap height varies from 0.64 to 0.75
/// of the em across the faces considered, so a hard-coded FontSize means 60% in one font and 54%
/// in the next. The size and the vertical nudge below are both computed from
/// <c>GlyphTypeface.CapsHeight</c> and <c>FontFamily.Baseline</c>, so swapping the face to
/// something else stays correct without anyone re-measuring by eye.
/// </summary>
public partial class MainWindow
{
    /// <summary>The title bar's height, as MainWindow.xaml sets it.</summary>
    private const double BrandBarHeight = 52;

    /// <summary>How much of that height the capitals should occupy.</summary>
    private const double BrandCapFraction = 0.60;

    /// <summary>Asked for: start the wordmark 40px further right.</summary>
    private const double BrandIndent = 40;

    private bool _brandDone;

    internal static void HookTitleBrand()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), LoadedEvent, new RoutedEventHandler(
            (s, _) =>
            {
                if (s is not MainWindow w) return;
                w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(w.ApplyTitleBrand));
            }));
    }

    /// <summary>
    /// Restyle the wordmark. Any failure leaves the original 13px bold exactly as it was — a title
    /// that did not get its new font is a cosmetic disappointment; a window that will not open
    /// because of one is a dead release.
    /// </summary>
    private void ApplyTitleBrand()
    {
        if (_brandDone) return;
        try
        {
            TextBlock? title = FindBrandText();
            if (title is null) return;

            // The pack URI ends in the FAMILY name after the '#', not the file name.
            var family = new FontFamily(new Uri("pack://application:,,,/"), "./assets/fonts/#Michroma");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs)) return;   // not embedded: leave it alone

            double cap = glyphs.CapsHeight > 0 ? glyphs.CapsHeight : 0.7;
            double size = BrandBarHeight * BrandCapFraction / cap;

            title.FontFamily = family;
            // Michroma ships one weight. Asking for Bold would make WPF synthesise a fake one,
            // which on a wide geometric face reads as a smudge rather than emphasis.
            title.FontWeight = FontWeights.Normal;
            title.FontSize = size;
            title.Margin = new Thickness(BrandIndent, 0, 0, 0);
            title.Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFF));

            // Faint, and the app's own cyan — the same light the ghost beside it gives off, so the
            // corner reads as one lit object rather than two competing ones.
            title.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x4F, 0xC3, 0xF7),
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.38,
            };

            title.RenderTransform = new TranslateTransform(0, CapCentringNudge(family, size, cap));

            _brandDone = true;
        }
        catch (Exception ex)
        {
            Diag.BotLog.Log("brand", "title not restyled: " + ex.Message);
        }
    }

    /// <summary>
    /// How far to lift the text so the CAPITALS sit centred in the bar, rather than the line box.
    ///
    /// A line box is not symmetrical about its capitals: it reserves room below the baseline for
    /// descenders that "EQ · AVATAR" does not have, so centring the box leaves the letters sitting
    /// low. At this size that is a visible three pixels. Worked out rather than eyeballed:
    ///
    ///   the box, centred, puts its top at  (bar − lineSpacing·size) / 2
    ///   the baseline sits  baseline·size   below that
    ///   the capitals should straddle the middle, so the baseline wants to be at
    ///                                      (bar + cap·size) / 2
    ///
    /// A RenderTransform rather than a margin, deliberately: it moves the pixels without touching
    /// layout, so nothing else in the title bar shifts by a fraction of a pixel in response.
    /// </summary>
    private static double CapCentringNudge(FontFamily family, double size, double cap)
    {
        double boxTop = (BrandBarHeight - family.LineSpacing * size) / 2;
        double baselineNow = boxTop + family.Baseline * size;
        double baselineWanted = (BrandBarHeight + cap * size) / 2;
        return baselineWanted - baselineNow;
    }

    /// <summary>
    /// The wordmark, found through the ghost rather than by name — the TextBlock has no x:Name and
    /// giving it one would mean editing the XAML this file exists to avoid touching.
    ///
    /// The ghost image sits in a Grid, that Grid sits in the StackPanel, and the wordmark is the
    /// TextBlock beside it. Every step is checked, so a rearrangement upstairs makes this return
    /// null and change nothing, rather than restyling whatever it happened to land on.
    /// </summary>
    private TextBlock? FindBrandText()
    {
        if (ArtGhostLogo?.Parent is not FrameworkElement slot) return null;
        if (slot.Parent is not StackPanel brand) return null;
        return brand.Children.OfType<TextBlock>()
                    .FirstOrDefault(t => (t.Text ?? "").StartsWith("EQ", StringComparison.Ordinal));
    }
}

/// <summary>
/// Registers the title-bar hook as the assembly loads. A second module initializer beside the
/// support one: C# allows any number of them, and one per feature keeps each file standing alone.
/// </summary>
internal static class TitleBrandBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        try { MainWindow.HookTitleBrand(); }
        catch { /* never stop the app from starting over a font */ }
    }
}
