using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    /// <summary>The parsed search box. Chips answer "which module"; this answers "which lines".</summary>
    private TextFilter _actQuery = TextFilter.None;
    /// <summary>Exactly what is in the document right now, in order — the basis for appending
    /// instead of rebuilding. See <see cref="RenderStream"/> for why that distinction matters.</summary>
    private List<ActivityEntry> _actRendered = new();

    private void InitActivityUi()
    {
        if (!_actInit)
        {
            _actInit = true;
            if (ActStream is not null)
            {
                // Wide enough that nothing wraps: a wrapped line breaks the column alignment AND
                // makes a dragged selection pick up half of the line below it.
                ActStream.Document.PageWidth = 4000;
                ActStream.Document.PagePadding = new Thickness(2);
            }
            ActivityLog.Record("App", "Activity Console opened.");
        }
        RenderActivity();
    }

    /// <summary>
    /// Called from the log's own event (any thread) — marshals, then hands the line to whoever
    /// wants it.
    ///
    /// The per-page consoles are fed from HERE rather than from each role's own Log event, which is
    /// what lets a role narrate detail lines straight into ActivityLog without every page having to
    /// know about them. One line in, one place it is dispatched from, and a page cannot be showing
    /// something the Activity Console doesn't have.
    ///
    /// The Activity Console's own repaint is coalesced (it redraws a whole stream); the module
    /// consoles are NOT, because each of them appends exactly one TextBlock and dropping a line
    /// would leave a hole in the middle of the evidence.
    /// </summary>
    private void OnActivityAdded(ActivityEntry e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { RouteToModuleConsoles(e); } catch { /* a console must never break a run */ }
        }), System.Windows.Threading.DispatcherPriority.Background);

        if (_actDirty) return;
        _actDirty = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _actDirty = false;
            if (PanelActivity is { Visibility: Visibility.Visible }) RenderActivity();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Does this line survive BOTH filters — the source chips and the search box?</summary>
    private bool ActPasses(ActivityEntry x)
        => !_actHidden.Contains(x.Source) && _actQuery.Matches(x.Text, x.Source, x.Tag);

    private void ActSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // ActStream is the last-declared control on this panel, so its existence proves the rest;
        // this fires once during InitializeComponent, before any of them exist.
        if (ActStream is null || ActSearchNote is null) return;
        _actQuery = TextFilter.Parse(ActSearch.Text);
        ActSearchNote.Text = _actQuery.Error is null
            ? "two words = both must appear · OR · NOR · -word to drop it · \"a phrase\" · source:quest"
            : "✖ " + _actQuery.Error + " — showing everything until it reads straight";
        ActSearchNote.Foreground = _actQuery.Error is null ? Hex("#5E7C9A") : Hex("#FFCB6B");
        RenderActivity();
    }

    private void ActSearchClear_Click(object sender, RoutedEventArgs e)
    {
        if (ActSearch is null) return;
        ActSearch.Text = "";
        ActSearch.Focus();
    }

    private void ActSearchHelp_Click(object sender, MouseButtonEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Hex("#C6D2DE"),
            FontSize = 12.5, LineHeight = 19, Margin = new Thickness(18), Text = SearchHelpText,
        };
        new Window
        {
            Title = "Searching the console",
            Owner = this, Width = 620, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        }.ShowDialog();
    }

    private const string SearchHelpText =
        "The chips above choose WHICH MODULE you're reading. The search box chooses WHICH LINES.\n\n" +
        "PUTTING WORDS TOGETHER\n" +
        "totem offered            both words must appear — a space means AND\n" +
        "totem AND offered        the same thing, spelled out\n" +
        "totem OR orders          either one is enough\n" +
        "totem NOR orders         neither — the lines that mention one of them go away\n\n" +
        "TAKING THINGS OUT\n" +
        "-stopped                 drop every line containing it\n" +
        "!stopped                 the same\n" +
        "NOT stopped              the same again\n\n" +
        "PHRASES AND BRACKETS\n" +
        "\"has been assigned\"      matched with its spaces, as one thing\n" +
        "(totem OR orders) -miss  brackets, so the junction you meant is the junction you get\n\n" +
        "FIELDS\n" +
        "source:quest             match the module name, not the words of the line\n" +
        "tag:kerra                match what inside that module spoke — a quest name, say\n" +
        "text:offered             match only the line itself, ignoring source and tag\n\n" +
        "OR and NOR bind loosest and read left to right; NOT binds tightest. Everything ignores "
        + "case, including the keywords.\n\n" +
        "A query that doesn't read straight — a bracket left open, say — says so under the box and "
        + "shows you everything meanwhile. A filter that silently empties the console looks exactly "
        + "like a bot that did nothing, and that is the one mistake this page cannot afford to make.";

    /// <summary>Hand one line to every per-page console that owns it. Each console filters again on
    /// its own source and tag, so this is a fan-out and not a routing decision.</summary>
    private void RouteToModuleConsoles(ActivityEntry e)
    {
        _mrgConsole?.Append(e);
        foreach (EQAvatar.Spike.Ui.ModuleConsole c in _questConsoles.Values) c.Append(e);
    }

    /// <summary>
    /// Put the visible stream on the clipboard.
    ///
    /// The lines themselves are selectable now, so this is the "all of it" path: it copies exactly
    /// what the chips and the search are showing, which is usually what someone pasting a failure
    /// into a chat window actually wants.
    /// </summary>
    private void ActCopy_Click(object sender, RoutedEventArgs e)
    {
        // A selection wins over the filters: if the user went to the trouble of dragging out four
        // lines, copying four hundred is not being helpful.
        if (ActStream is not null && !ActStream.Selection.IsEmpty)
        {
            string picked = ActStream.Selection.Text;
            if (!string.IsNullOrWhiteSpace(picked))
            {
                try
                {
                    Clipboard.SetText(picked);
                    // TrimEnd first: a FlowDocument's TextRange ends every paragraph with a
                    // newline, including the last, so Ctrl+A would have reported one line too many.
                    ShowToast($"Copied the {picked.TrimEnd('\r', '\n').Split('\n').Length} selected line(s)");
                }
                catch { ShowToast("Couldn't reach the clipboard"); }
                return;
            }
        }

        List<ActivityEntry> lines = ActivityLog.Snapshot().Where(ActPasses).ToList();
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
        // The per-page consoles hold their own copy of the scrollback and are only ever APPENDED to,
        // so clearing the shared log leaves them showing lines that no longer exist anywhere else —
        // and their "copy" would then put a different set on the clipboard than the page displays.
        _mrgConsole?.Rebuild();
        foreach (EQAvatar.Spike.Ui.ModuleConsole c in _questConsoles.Values) c.Rebuild();
    }

    private void RenderActivity()
    {
        // ActStream is the LAST of this panel's named controls in document order, so a null check on
        // it proves the rest were created (this can run before InitializeComponent has finished).
        if (ActFilterHost is null || ActNowText is null || ActStream is null) return;

        List<string> sources = ActivityLog.Sources();
        List<ActivityEntry> all = ActivityLog.Snapshot();

        // ---- the filter bar. Rebuilt only when the SET of chips or their counts change: this
        // method runs on every line a running role speaks, and re-creating chips under the user's
        // cursor mid-click is both wasteful and rude.
        // The count on a chip is how many of that source's lines match the SEARCH — so typing
        // "totem" tells you at a glance which module ever mentioned one. It deliberately ignores
        // the chips' own on/off state; a chip that reported 0 because it was off would be lying
        // about the thing its own click is meant to reveal.
        // Counted in ONE pass over the log rather than one pass per chip: this runs on every line
        // a running role speaks, and a five-thousand-line night times six sources is thirty
        // thousand string comparisons for a bar that usually hasn't changed.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string sc in sources) counts[sc] = 0;
        foreach (ActivityEntry x in all)
            if (_actQuery.Matches(x.Text, x.Source, x.Tag))
                counts[x.Source] = counts.TryGetValue(x.Source, out int c) ? c + 1 : 1;

        string chipSig = string.Join("|", sources.Select(sc =>
            sc + ":" + (counts.TryGetValue(sc, out int n) ? n : 0)
               + ":" + (_actHidden.Contains(sc) ? "0" : "1")));
        if (chipSig != _actChipSig)
        {
            _actChipSig = chipSig;
            BuildChips(sources, counts);
        }

        RenderStream(all);
    }

    private string _actChipSig = "\u0000";

    private void BuildChips(List<string> sources, Dictionary<string, int> counts)
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
            int count = counts.TryGetValue(src, out int n) ? n : 0;
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

    /// <summary>
    /// Draw the filtered stream into the selectable console.
    ///
    /// WHY THIS APPENDS INSTEAD OF REBUILDING. The whole point of the RichTextBox is that you can
    /// drag out three lines and press Ctrl+C. Rebuilding the document is what a running role would
    /// force several times a second — and every rebuild throws the selection away, so the feature
    /// would work perfectly right up until the moment there was something worth copying. So: the
    /// entries currently in the document are remembered, and when the new list is the old one with
    /// lines added (the overwhelmingly common case) only the new paragraphs are appended. A rebuild
    /// happens only when the filters change, which is a moment the user asked for anyway.
    /// </summary>
    private void RenderStream(List<ActivityEntry> all)
    {
        List<ActivityEntry> shownLines = all.Where(ActPasses).ToList();
        // Rendering every line of a long night would build tens of thousands of visuals; the tail
        // is what anyone reads, and the count says plainly what is above it.
        const int RenderCap = 300;
        int over = Math.Max(0, shownLines.Count - RenderCap);
        List<ActivityEntry> tail = over > 0 ? shownLines.GetRange(over, RenderCap) : shownLines;

        ActCount.Text = $"{shownLines.Count} line(s) shown · {all.Count} recorded"
                      + (over > 0 ? $" · showing the last {RenderCap}" : "");

        // Only follow the tail if the user was ALREADY at the tail. Snapping them back down on
        // every new line makes scrolling back impossible for as long as anything is running — which
        // is precisely when someone reads this page.
        ScrollViewer? sv = ActStreamScroll();
        bool atEnd = sv is null || sv.ScrollableHeight <= 0
                  || sv.VerticalOffset >= sv.ScrollableHeight - 4;

        FlowDocument doc = ActStream.Document;

        // How much of what is on screen is still wanted, and where does it sit in the new tail?
        int drop = ActPrefixDrop(tail);
        if (drop < 0)
        {
            doc.Blocks.Clear();
            _actRendered = new List<ActivityEntry>();
            foreach (ActivityEntry e in tail) doc.Blocks.Add(MakeStreamLine(e));
            _actRendered.AddRange(tail);
        }
        else
        {
            for (int i = 0; i < drop && doc.Blocks.FirstBlock is not null; i++)
                doc.Blocks.Remove(doc.Blocks.FirstBlock);
            for (int i = _actRendered.Count - drop; i < tail.Count; i++)
                doc.Blocks.Add(MakeStreamLine(tail[i]));
            _actRendered = tail;
        }

        if (atEnd) ActStream.ScrollToEnd();

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

    /// <summary>
    /// How many of the lines already on screen have scrolled off the top of the window, or −1 when
    /// the document no longer matches at all and must be rebuilt.
    ///
    /// Reference equality, not the record's value equality: two lines logged in the same second
    /// with the same words are equal by value, and matching the wrong one would splice the document
    /// silently out of step with the list that describes it.
    /// </summary>
    private int ActPrefixDrop(List<ActivityEntry> tail) => PrefixDrop(_actRendered, tail);

    /// <summary>The per-page consoles solve the same problem differently — they append by entry id
    /// rather than by diffing a window — so this is now the Activity Console's alone. Kept as it is:
    /// this page really does re-derive its whole tail on every filter change, and identity is what
    /// keeps the document in step with the list that describes it.</summary>
    internal static int PrefixDrop(List<ActivityEntry> rendered, List<ActivityEntry> tail)
    {
        if (rendered.Count == 0) return 0;
        if (tail.Count == 0) return -1;

        int k = -1;
        for (int i = 0; i < rendered.Count; i++)
            if (ReferenceEquals(rendered[i], tail[0])) { k = i; break; }
        if (k < 0) return -1;

        int keep = rendered.Count - k;
        if (keep > tail.Count) return -1;
        for (int j = 0; j < keep; j++)
            if (!ReferenceEquals(rendered[k + j], tail[j])) return -1;
        return k;
    }

    /// <summary>One line: dim clock, the source in its own colour, then what it said.</summary>
    private static Paragraph MakeStreamLine(ActivityEntry e)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.Add(new Run(e.When.ToString("HH:mm:ss") + "  ") { Foreground = Hex("#4A5A6C") });
        p.Inlines.Add(new Run(Pad(e.Source, 6) + "  ")
        {
            Foreground = new SolidColorBrush(SourceColor(e.Source)),
            FontWeight = FontWeights.Bold,
        });
        p.Inlines.Add(new Run(e.Text)
        {
            Foreground = e.IsBad ? Hex("#FFCB6B") : e.IsGood ? Hex("#7CE38B")
                       : e.IsStep ? Hex("#8AA0B6") : Hex("#C6D2DE"),
        });
        return p;
    }

    /// <summary>Monospace column padding — the source names line up so the eye can skip them.</summary>
    private static string Pad(string s, int width)
        => s.Length >= width ? s.Substring(0, width) : s.PadRight(width);

    /// <summary>The RichTextBox's own scroller, so "was the user reading history" can be asked
    /// before new lines land.</summary>
    private ScrollViewer? ActStreamScroll()
    {
        if (_actScroll is not null) return _actScroll;
        if (ActStream is null) return null;
        ActStream.ApplyTemplate();
        _actScroll = ActStream.Template?.FindName("PART_ContentHost", ActStream) as ScrollViewer;
        return _actScroll;
    }

    private ScrollViewer? _actScroll;

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
