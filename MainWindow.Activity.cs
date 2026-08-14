using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EQAvatar.Spike.Data;

namespace EQAvatar.Spike;

/// <summary>
/// The ACTIVITY CONSOLE (partial class): every module's narration in one stream, filtered.
///
/// WHY IT EXISTS. Each role used to shout into the Grind console because that was the only console
/// there was. That ruined both readings at once — a grind log full of quest lines, and a quest run
/// whose story you had to pick out of someone else's. Now each page reads only its own source, and
/// this page is where they are deliberately put back together: when a merge sweep and a quest run
/// disagree about who owns the cursor, the ORDER of their lines is the evidence, and no per-page
/// console can show you that.
///
/// The filter bar is chips rather than checkboxes so the state is legible from across the room:
/// lit = shown, dim = hidden, and each chip carries how many lines that source has produced.
/// </summary>
public partial class MainWindow
{
    private bool _actInit;
    /// <summary>Sources the user has switched OFF. Absent = shown, so a source that first speaks
    /// mid-session appears immediately instead of being silently filtered out of its own debut.</summary>
    private readonly HashSet<string> _actHidden = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rendering is throttled: a run narrating every click would otherwise rebuild a
    /// 400-line panel on the UI thread several times a second.</summary>
    private bool _actDirty;

    private void InitActivityUi()
    {
        if (!_actInit)
        {
            _actInit = true;
            ActivityLog.Record("App", "Activity Console opened.");
        }
        RenderActivity();
    }

    /// <summary>Called from the log's own event (any thread) — marshals and coalesces.</summary>
    private void OnActivityAdded(ActivityEntry e)
    {
        if (_actDirty) return;
        _actDirty = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _actDirty = false;
            if (PanelActivity is { Visibility: Visibility.Visible }) RenderActivity();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Put the visible stream on the clipboard.
    ///
    /// The console is TextBlocks in a panel, which cannot be selected with a mouse — so the one
    /// time a user most wants these lines (pasting a failure to someone who can read it) was the
    /// one time they could not have them. A button is a smaller answer than making every line
    /// selectable, and it copies exactly what the filters are showing.
    /// </summary>
    private void ActCopy_Click(object sender, RoutedEventArgs e)
    {
        List<ActivityEntry> lines = ActivityLog.Snapshot()
            .Where(x => !_actHidden.Contains(x.Source)).ToList();
        if (lines.Count == 0) { ShowToast("Nothing to copy"); return; }
        var sb = new System.Text.StringBuilder();
        foreach (ActivityEntry x in lines)
            sb.Append(x.When.ToString("HH:mm:ss")).Append("  ")
              .Append(x.Source.PadRight(6)).Append("  ").AppendLine(x.Text);
        try
        {
            Clipboard.SetText(sb.ToString());
            ShowToast($"Copied {lines.Count} line(s)");
        }
        catch { ShowToast("Couldn't reach the clipboard"); }
    }

    private void ActClear_Click(object sender, RoutedEventArgs e)
    {
        ActivityLog.Clear();
        RenderActivity();
    }

    private void RenderActivity()
    {
        // ActLines is the LAST of this panel's named controls in document order, so a null check on
        // it proves the rest were created (this can run before InitializeComponent has finished).
        if (ActFilterHost is null || ActNowText is null || ActLines is null) return;

        List<string> sources = ActivityLog.Sources();
        List<ActivityEntry> all = ActivityLog.Snapshot();

        // ---- the filter bar. Rebuilt only when the SET of chips or their counts change: this
        // method runs on every line a running role speaks, and re-creating chips under the user's
        // cursor mid-click is both wasteful and rude.
        string chipSig = string.Join("|", sources.Select(sc =>
            sc + ":" + all.Count(x => string.Equals(x.Source, sc, StringComparison.OrdinalIgnoreCase))
               + ":" + (_actHidden.Contains(sc) ? "0" : "1")));
        if (chipSig != _actChipSig)
        {
            _actChipSig = chipSig;
            BuildChips(sources, all);
        }

        RenderStream(all);
    }

    private string _actChipSig = "\u0000";

    private void BuildChips(List<string> sources, List<ActivityEntry> all)
    {
        ActFilterHost.Children.Clear();
        if (sources.Count == 0)
            ActFilterHost.Children.Add(new TextBlock
            {
                Text = "nothing has run yet this session", FontSize = 11, Foreground = Hex("#5E7C9A"),
            });
        foreach (string src in sources)
        {
            string captured = src;
            bool shown = !_actHidden.Contains(src);
            int count = all.Count(x => string.Equals(x.Source, src, StringComparison.OrdinalIgnoreCase));
            Color tone = SourceColor(src);

            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(tone), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = shown ? 1 : 0.35,
            };
            if (shown)
                dot.Effect = new DropShadowEffect { Color = tone, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };

            var label = new TextBlock
            {
                Text = src, FontSize = 11, FontWeight = shown ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = shown ? new SolidColorBrush(tone) : Hex("#5E7C9A"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var num = new TextBlock
            {
                Text = "  " + count, FontSize = 9.5, Foreground = Hex("#5E7C9A"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var chip = new Border
            {
                CornerRadius = new CornerRadius(999),
                Background = shown ? Hex("#14202E") : Hex("#0E141C"),
                BorderBrush = shown ? new SolidColorBrush(tone) : Hex("#26303F"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 7, 6),
                Cursor = Cursors.Hand,
                ToolTip = shown ? $"Hide {src} lines" : $"Show {src} lines",
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label, num } },
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (!_actHidden.Remove(captured)) _actHidden.Add(captured);
                RenderActivity();
            };
            ActFilterHost.Children.Add(chip);
        }

        if (sources.Count > 1)
        {
            var allChip = new Border
            {
                CornerRadius = new CornerRadius(999), Background = Hex("#0E141C"),
                BorderBrush = Hex("#3A4A5E"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(6, 0, 0, 6), Cursor = Cursors.Hand,
                ToolTip = _actHidden.Count > 0 ? "Show every source again" : "Hide every source",
                Child = new TextBlock
                {
                    Text = _actHidden.Count > 0 ? "show all" : "hide all",
                    FontSize = 10.5, Foreground = Hex("#9FB6CC"),
                },
            };
            allChip.MouseLeftButtonUp += (_, _) =>
            {
                if (_actHidden.Count > 0) _actHidden.Clear();
                else foreach (string s in sources) _actHidden.Add(s);
                RenderActivity();
            };
            ActFilterHost.Children.Add(allChip);
        }
    }

    private void RenderStream(List<ActivityEntry> all)
    {
        // ---- the stream
        List<ActivityEntry> shownLines = all.Where(x => !_actHidden.Contains(x.Source)).ToList();
        // Rendering every line of a long night would build tens of thousands of visuals; the tail
        // is what anyone reads, and the count says plainly what is above it.
        const int RenderCap = 300;
        int hidden = Math.Max(0, shownLines.Count - RenderCap);
        List<ActivityEntry> tail = hidden > 0 ? shownLines.GetRange(hidden, RenderCap) : shownLines;

        ActCount.Text = $"{shownLines.Count} line(s) shown · {all.Count} recorded"
                      + (hidden > 0 ? $" · showing the last {RenderCap}" : "");

        // Only follow the tail if the user was ALREADY at the tail. Snapping them back down on
        // every new line makes scrolling back impossible for as long as anything is running — which
        // is precisely when someone reads this page.
        bool atEnd = ActScroll is null || ActScroll.ScrollableHeight <= 0
                  || ActScroll.VerticalOffset >= ActScroll.ScrollableHeight - 4;

        ActLines.Children.Clear();
        foreach (ActivityEntry e in tail)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = e.When.ToString("HH:mm:ss"), FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Foreground = Hex("#4A5A6C"), Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = e.Source, FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(SourceColor(e.Source)), Width = 58,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = e.Text, FontFamily = new FontFamily("Consolas"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 980,
                Foreground = e.IsBad ? Hex("#FFCB6B") : e.IsGood ? Hex("#7CE38B")
                           : e.IsStep ? Hex("#8AA0B6") : Hex("#C6D2DE"),
            });
            ActLines.Children.Add(row);
        }
        if (atEnd) ActScroll?.ScrollToEnd();

        // ---- the latest line, big. Taken from EVERYTHING, not from the filtered view: a chip
        // hides chatter from the stream, it must not make an hour-old line masquerade as "NOW"
        // while the sweep you filtered out is the thing actually running.
        ActivityEntry? now = all.Count > 0 ? all[^1] : null;
        bool live = _grind is { Running: true } || _hunt is { Running: true }
                 || _questRun is { Running: true } || _mergeRun is { Running: true } || _questStarting;
        ActNowLabel.Text = live ? "NOW" : "LATEST";
        ActNowLabel.Foreground = live ? Hex("#49F27E") : Hex("#5E7C9A");
        ActNowText.Text = now is null ? "nothing yet" : $"[{now.Source}]  {now.Text}";
        ActNowText.Foreground = now is null ? Hex("#5E7C9A")
                              : now.IsBad ? Hex("#FFCB6B")
                              : now.IsGood ? Hex("#49F27E") : Hex("#DDE7F0");
        ActNowBorder.BorderBrush = live ? Hex("#3FCB74") : Hex("#26303F");
        ActNowBorder.Background = live ? Hex("#10301F") : Hex("#0C1420");
    }

    /// <summary>A stable colour per source so the eye can track one module down the stream.
    /// Derived from the name, so a source added later still gets its own without a registry to
    /// keep in step.</summary>
    private static Color SourceColor(string source) => source.ToLowerInvariant() switch
    {
        "quest" => Color.FromRgb(0x6F, 0xD3, 0xFF),
        "grind" => Color.FromRgb(0x9F, 0xE0, 0xB8),
        "hunt" => Color.FromRgb(0x7C, 0xE3, 0x8B),
        "merge" => Color.FromRgb(0xFF, 0xB4, 0x6B),
        "login" => Color.FromRgb(0xC9, 0xA7, 0xFF),
        "app" => Color.FromRgb(0x8A, 0xA0, 0xB6),
        _ => ColorFromName(source),
    };

    private static Color ColorFromName(string s)
    {
        int h = 0;
        foreach (char c in s) h = (h * 31 + c) & 0x7FFFFFFF;
        // Keep it in the app's palette range: bright enough on near-black, never muddy.
        byte r = (byte)(120 + h % 110);
        byte g = (byte)(140 + (h / 7) % 100);
        byte b = (byte)(150 + (h / 13) % 100);
        return Color.FromRgb(r, g, b);
    }
}
