using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Effects;
using EQAvatar.Spike.Data;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// One console, used by every page that has one.
///
/// WHY IT IS A CONTROL AND NOT A RENDER METHOD. Both consoles started life as "clear a StackPanel
/// and rebuild it from the log", which is fine until you actually need one: the instant a role
/// speaks, the panel is thrown away and rebuilt, so the scrollbar jumps back to where a fresh
/// ScrollViewer starts and whatever you were reading is gone. A console you cannot scroll back
/// through while something is running is a console that only works when you don't need it. So the
/// scrollback is built ONCE and appended to, and the thing that changes per line is one TextBlock.
///
/// It also means the two pages cannot drift apart. The Questing card and Auto Merge asked for "the
/// same console", and the only way two consoles stay the same is by being the same console with a
/// different filter on the front — one source each, and nothing from any other module, which is
/// the entire reason the per-page consoles exist instead of everyone shouting into one box.
///
/// WHAT IT ADDS OVER THE OLD ONE:
///  · scroll back without being yanked to the bottom every time she speaks, with a pill that says
///    how many lines arrived while you were reading;
///  · copy AND save — the lines are TextBlocks, so the mouse cannot select them, and the moment
///    you most want them is the moment you are trying to show someone else what went wrong;
///  · a DETAIL switch that makes the roles narrate the numbers behind their decisions, not just
///    the decisions;
///  · the drag grip, because "about five steps" is right for glancing and wrong for reading;
///  · a FIND box with the same query language as the Activity Console — the same words, the same
///    OR / NOR / -word / "phrase" / brackets — because a console you can search is the difference
///    between reading a night's log and scrolling past it.
///
/// The body is a read-only RichTextBox for the reason the Activity Console's is: TextBlocks cannot
/// be selected with a mouse, and dragging out the three lines that went wrong is what anyone
/// actually wants. That in turn is why lines are APPENDED and never re-rendered — a rebuild throws
/// the selection away, so the feature would work right up until there was something worth
/// selecting.
/// </summary>
public sealed class ModuleConsole : StackPanel
{
    /// <summary>Rendered lines are capped: a night's farming is tens of thousands of visuals, and
    /// the tail is what anyone reads. The count line says plainly what is above it, and Save
    /// writes the whole buffer regardless of what is drawn.</summary>
    private const int RenderCap = 400;

    private readonly string _source;
    private readonly string _tag;
    private readonly Func<ActivityEntry, bool> _mine;
    private readonly Action _openActivityConsole;
    private readonly Action<string> _toast;
    private readonly Func<bool> _readDetail;
    private readonly Action<bool> _writeDetail;
    private readonly string _emptyText;

    private readonly TextBlock _nowLabel, _nowText, _countText, _detailChip, _searchNote;
    private readonly Border _nowBorder, _newPill;
    private readonly TextBlock _newPillText;
    private readonly TextBox _search;
    private readonly RichTextBox _body;
    /// <summary>The RichTextBox's own scroller, dug out of its template once. Needed to answer "is
    /// the user at the bottom?", which is the only thing that decides whether a new line drags them
    /// away from what they were reading.</summary>
    private ScrollViewer? _scroll;
    /// <summary>The parsed FIND box. Same language as the Activity Console's, by construction —
    /// it is the same parser.</summary>
    private TextFilter _query = TextFilter.None;
    /// <summary>How many lines the document is holding.</summary>
    private int _drawn;

    private bool _running;
    /// <summary>Lines that arrived while the user was scrolled up.</summary>
    private int _unread;
    /// <summary>
    /// Whether the console should stay pinned to the newest line.
    ///
    /// Tracked as its own flag rather than inferred from <see cref="_unread"/>, because the two
    /// answer different questions. A user who has just scrolled up to read has an unread count of
    /// zero until the next line arrives — and this control gets re-parented into a freshly built
    /// card on every page render, which raises Loaded again. Reading "0 unread" as "wants to
    /// follow" there snapped them straight back to the bottom, which is the exact behaviour this
    /// class exists to stop.
    /// </summary>
    private bool _follow = true;
    /// <summary>Where the user was reading when this console was last taken out of a card.</summary>
    private double _savedOffset;
    /// <summary>The highest entry id already drawn. A line can reach this console twice — once in
    /// the snapshot the constructor reads, once in the dispatcher callback that was already queued
    /// for it — and drawing it twice is a console that appears to stutter at the exact moment
    /// someone is reading it closely.</summary>
    private long _lastSeq;
    /// <summary>Running total, kept rather than recounted: recounting means filtering all 5,000
    /// buffered entries under a lock, on the UI thread, once per line — in a method whose whole
    /// purpose is to be the cheap path.</summary>
    private int _total;
    private int _sinceRecount;
    private const int RecountEvery = 128;

    /// <param name="source">The ActivityLog source this console shows, and NOTHING else.</param>
    /// <param name="tag">Optional: the instance inside that source (a quest name). Blank shows
    /// every tag under the source.</param>
    /// <param name="tagMatches">How to compare tags — quest names are compared normalized, so the
    /// caller supplies the comparison rather than this control guessing at it.</param>
    /// <param name="wrapBody">Puts the scrollback in its bordered box with the drag grip under it.
    /// Supplied by the page rather than built here, because the grip writes the shared height
    /// setting and one grip for every console is the whole point of it being shared.</param>
    public ModuleConsole(string source, string tag, Func<string, string, bool>? tagMatches,
                         string title, string emptyText,
                         Action openActivityConsole, Action<string> toast,
                         Func<FrameworkElement, string, FrameworkElement> wrapBody,
                         Func<bool> readDetail, Action<bool> writeDetail)
    {
        _source = source;
        _tag = tag ?? "";
        _emptyText = emptyText;
        _openActivityConsole = openActivityConsole;
        _toast = toast;
        _readDetail = readDetail;
        _writeDetail = writeDetail;
        Margin = new Thickness(0, 10, 0, 0);

        Func<string, string, bool> cmp = tagMatches
            ?? ((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
        // Captured once. A console that re-derives "is this mine?" per line from mutable page state
        // is a console that can start showing someone else's evidence halfway through a run.
        string mySource = source, myTag = _tag;
        _mine = e => string.Equals(e.Source, mySource, StringComparison.OrdinalIgnoreCase)
                  && (myTag.Length == 0 || cmp(e.Tag, myTag));

        // ---------------------------------------------------------------- header
        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock
        {
            Text = title, FontSize = 9.5, FontWeight = FontWeights.Bold,
            Foreground = Hex("#5E7C9A"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        head.Children.Add(Link("open the Activity Console →",
            "Every module's activity in one place, with filters. This console shows only "
            + source + ".", (_, _) => _openActivityConsole()));
        head.Children.Add(Link("copy", "Copy your selection if you've made one, and everything the find "
            + "box is showing if you haven't. (You can also drag out lines below and press Ctrl+C.)",
            (_, _) => Copy()));
        head.Children.Add(Link("save", "Write this module's activity to a file in your Documents folder, "
            + "with the app version and a full timestamp on every line. Ignores the find box — the "
            + "file is the whole story.", (_, _) => Save()));
        _detailChip = Link("detail", "", (_, _) => ToggleDetail());
        head.Children.Add(_detailChip);
        Children.Add(head);

        // ---------------------------------------------------------------- find
        _search = new TextBox
        {
            FontSize = 11, Width = 260, Padding = new Thickness(6, 2, 6, 3),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Two words = both must appear. OR · NOR · -word to drop it · \"a phrase\" · brackets.",
        };
        _search.TextChanged += (_, _) => ApplySearch();
        var findRow = new StackPanel
        { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        findRow.Children.Add(new TextBlock
        {
            Text = "FIND", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hex("#5E7C9A"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        findRow.Children.Add(_search);
        findRow.Children.Add(Link("clear", "Empty the find box.", (_, _) => { _search.Text = ""; _search.Focus(); }));
        _searchNote = new TextBlock
        {
            FontSize = 9, Foreground = Hex("#5E7C9A"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        findRow.Children.Add(_searchNote);
        Children.Add(findRow);

        // ---------------------------------------------------------------- NOW
        _nowLabel = new TextBlock { Text = "LAST", FontSize = 8.5, FontWeight = FontWeights.Bold };
        _nowText = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        _nowBorder = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 8),
            Child = new StackPanel { Children = { _nowLabel, _nowText } },
        };
        Children.Add(_nowBorder);

        // ---------------------------------------------------------------- scrollback
        _body = new RichTextBox
        {
            Document = new FlowDocument
            {
                // Wide enough that nothing wraps: a wrapped line breaks the column alignment AND
                // makes a dragged selection pick up half of the line below it.
                PageWidth = 4000, PagePadding = new Thickness(2),
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
            },
            IsReadOnly = true, IsDocumentEnabled = false,
            Background = Hex("#0C0F13"), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x4D, 0x6B)),
        };
        // The moment the user scrolls away from the bottom we stop following. Snapping them back
        // down on every new line makes reading back impossible for exactly as long as something is
        // running — which is the only time anyone opens this.
        _body.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, ev) =>
        {
            // Only a change in POSITION says anything about intent. Content growing under a reader
            // who hasn't moved (ExtentHeightChange) must not be read as them choosing to follow.
            if (ev.VerticalChange == 0) return;
            _follow = AtBottom();
            // Kept CURRENT, not only captured on the way out of a card. Restoring the reading
            // position also happens when the size or detail switch is flipped, and on the Auto Merge
            // page the console is never re-parented at all — so an offset saved only on detach was
            // permanently zero there, and clicking "detail" threw the reader to the very top.
            _savedOffset = Scroll()?.VerticalOffset ?? 0;
            if (_follow) { _unread = 0; ShowPill(); }
        }));

        _newPillText = new TextBlock { FontSize = 10.5, Foreground = Hex("#0B0F18"), FontWeight = FontWeights.Bold };
        _newPill = new Border
        {
            CornerRadius = new CornerRadius(999), Background = Hex("#49F27E"),
            Padding = new Thickness(10, 3, 10, 4), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 19), Visibility = Visibility.Collapsed,
            Child = _newPillText,
            ToolTip = "Jump back to the newest line and follow along again.",
        };
        _newPill.MouseLeftButtonUp += (_, _) => { _follow = true; _unread = 0; ShowPill(); _body.ScrollToEnd(); };

        // The grip lives under the box, so the pill has to float over the BODY rather than over the
        // wrapper — otherwise "3 new lines" sits on top of the thing you resize with.
        var scrollHost = new Grid();
        scrollHost.Children.Add(wrapBody(_body, source.ToLowerInvariant()));
        scrollHost.Children.Add(_newPill);
        Children.Add(scrollHost);

        _countText = new TextBlock
        {
            FontSize = 9.5, Foreground = Hex("#4A5A6C"), Margin = new Thickness(2, 3, 0, 0),
        };
        Children.Add(_countText);

        ApplyDetailChip();
        Rebuild();
        // Loaded is NOT one-shot: it fires again every time this control is put into a rebuilt card.
        // Queued, because the ScrollViewer has not measured its content at this point and scrolling
        // now scrolls against the old extent.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreReadingPosition),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- public surface

    /// <summary>Repaint the live/idle chrome. Cheap — safe to call from a render pass.</summary>
    public void SetRunning(bool running)
    {
        _running = running;
        _nowLabel.Text = running ? "NOW" : "LAST";
        _nowLabel.Foreground = running ? Hex("#49F27E") : Hex("#5E7C9A");
        _nowBorder.Background = running ? Hex("#10301F") : Hex("#0C1420");
        _nowBorder.BorderBrush = running ? Hex("#3FCB74") : Hex("#26303F");
        ApplyNowGlow();
    }

    /// <summary>
    /// One new line. This is the hot path — it appends a single TextBlock and touches one more.
    ///
    /// Entries that belong to another source or another quest are dropped here rather than at the
    /// call site, so a page that forgets to filter still cannot leak another module's evidence into
    /// this box.
    /// </summary>
    public void Append(ActivityEntry e)
    {
        if (!_mine(e)) return;
        if (e.Seq != 0 && e.Seq <= _lastSeq) return;              // already drawn by a rebuild
        if (e.Seq != 0 && e.Seq <= ActivityLog.ClearedThrough) return;   // deleted before we got to it
        _lastSeq = Math.Max(_lastSeq, e.Seq);

        // NOW is set from every line of this module, search or no search. A find box narrows what
        // you are READING BACK; it must never make an hour-old line masquerade as what she is doing
        // this second — the same rule the Activity Console's chips follow.
        SetNow(e);
        // The ring evicts once the SHARED buffer fills, and it evicts whatever is oldest — which may
        // belong to any module. There is no arithmetic this console can do to know its own share, so
        // it recounts now and then instead: cheap amortised, and bounded wrong in between. Counting
        // appends forever made it claim twelve thousand lines while its own copy button offered five.
        if (++_sinceRecount >= RecountEvery) { _sinceRecount = 0; _total = ActivityLog.Snapshot(_mine).Count; }
        else _total++;

        if (!Passes(e)) { UpdateCount(); return; }
        FlowDocument doc = _body.Document;
        doc.Blocks.Add(Row(e));
        _drawn++;
        while (_drawn > RenderCap && doc.Blocks.FirstBlock is not null)
        { doc.Blocks.Remove(doc.Blocks.FirstBlock); _drawn--; }

        if (_follow) _body.ScrollToEnd();
        else { _unread++; ShowPill(); }
        UpdateCount();
    }

    /// <summary>Mine AND wanted by the find box.</summary>
    private bool Passes(ActivityEntry e) => _query.Matches(e.Text, e.Source, e.Tag);

    private void ApplySearch()
    {
        _query = TextFilter.Parse(_search.Text);
        // A query that doesn't read straight SAYS SO and shows everything meanwhile. A filter that
        // silently empties a console looks exactly like a bot that did nothing, which is the one
        // mistake a console cannot afford.
        _searchNote.Text = _query.Error is null
            ? (_query.IsEmpty ? "" : "showing matching lines only")
            : "✖ " + _query.Error + " — showing everything until it reads straight";
        _searchNote.Foreground = _query.Error is null ? Hex("#5E7C9A") : Hex("#FFCB6B");
        Rebuild();
    }

    /// <summary>
    /// Re-read the shared chrome (the detail switch, the scrollback height) from settings.
    ///
    /// Both consoles show the SAME two switches, because they are one setting each and a page that
    /// disagreed with the other about whether detail was on would be lying about what the log is
    /// going to contain. Whichever console you flip it on, they all repaint.
    /// </summary>
    public void RefreshChrome()
    {
        ApplyDetailChip();
        UpdateCount();
    }

    /// <summary>Redraw the whole scrollback from the log — on creation, and whenever the console
    /// has been off screen long enough that appending would leave a hole.</summary>
    public void Rebuild()
    {
        List<ActivityEntry> mine = ActivityLog.Snapshot(_mine);
        List<ActivityEntry> shown = mine.Where(Passes).ToList();
        if (shown.Count > RenderCap) shown = shown.GetRange(shown.Count - RenderCap, RenderCap);

        FlowDocument doc = _body.Document;
        doc.Blocks.Clear();
        foreach (ActivityEntry e in shown) doc.Blocks.Add(Row(e));
        _drawn = shown.Count;
        _lastSeq = mine.Count > 0 ? mine[^1].Seq : 0;
        _total = mine.Count;
        SetNow(mine.Count > 0 ? mine[^1] : null);
        _unread = 0;
        _follow = true;
        ShowPill();
        UpdateCount();
        // Queued rather than immediate: the ScrollViewer has not measured its new content yet, so
        // scrolling now scrolls to the end of the OLD extent and leaves the newest lines below the
        // fold — which reads as "she stopped talking" at the exact moment she didn't.
        Dispatcher.BeginInvoke(new Action(() => _body.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// True while the user is doing something this control must not be yanked out from under: typing
    /// in the find box, or dragging out a selection.
    ///
    /// The Questing card is rebuilt on a timer while a run narrates, and rebuilding re-parents this
    /// console. WPF drops keyboard focus on removal and does not give it back, so a find box typed
    /// into mid-run lost the caret after a character or two, and a drag-selection ended itself. The
    /// page asks before it redraws.
    /// </summary>
    public bool IsUserBusy => IsKeyboardFocusWithin || !_body.Selection.IsEmpty;

    /// <summary>Take this console out of whatever panel currently holds it, so a page that rebuilds
    /// its card can put the SAME console back and keep the scrollback and the reading position.
    /// Adding an element that still has a parent throws, and the throw lands inside a render pass.</summary>
    public void Detach()
    {
        // Remember where they were reading BEFORE the element leaves the tree — once it is out, the
        // ScrollViewer's offset is no longer something worth trusting.
        _follow = AtBottom();
        _savedOffset = Scroll()?.VerticalOffset ?? _savedOffset;
        (Parent as Panel)?.Children.Remove(this);
    }

    private void RestoreReadingPosition()
    {
        if (_follow) _body.ScrollToEnd();
        else Scroll()?.ScrollToVerticalOffset(_savedOffset);
    }

    /// <summary>The RichTextBox's own scroller, out of its template. Cached, and re-looked-up while
    /// null: the template is not applied until the control has been through a layout pass, so the
    /// first few calls legitimately find nothing.</summary>
    private ScrollViewer? Scroll()
    {
        if (_scroll is not null) return _scroll;
        _body.ApplyTemplate();
        _scroll = _body.Template?.FindName("PART_ContentHost", _body) as ScrollViewer;
        return _scroll;
    }

    // ---------------------------------------------------------------- header actions

    private void Copy()
    {
        // A selection wins over everything else. If someone went to the trouble of dragging out
        // four lines, putting four hundred on the clipboard is not being helpful.
        if (!_body.Selection.IsEmpty)
        {
            try { Clipboard.SetText(_body.Selection.Text); _toast("Copied the selection"); }
            catch { _toast("Couldn't reach the clipboard"); }
            return;
        }
        // What you are LOOKING AT, filter and all — the same rule the Activity Console's copy
        // follows, because it is the same query language and a gesture that means one thing on one
        // page and something else on the next is worse than either. (Save is the deliberate
        // exception: the file is the whole story, and its tooltip says so.)
        List<ActivityEntry> mine = ActivityLog.Snapshot(_mine).Where(Passes).ToList();
        if (mine.Count == 0) { _toast(_query.IsEmpty ? "Nothing to copy" : "Nothing matches the find box"); return; }
        try
        {
            Clipboard.SetText(Transcript(mine, header: false));
            _toast($"Copied {mine.Count} line(s)" + (_query.IsEmpty ? "" : " matching the find box"));
        }
        catch { _toast("Couldn't reach the clipboard"); }
    }

    private void Save()
    {
        List<ActivityEntry> mine = ActivityLog.Snapshot(_mine);
        if (mine.Count == 0) { _toast("Nothing to save"); return; }
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EQAvatar");
            Directory.CreateDirectory(dir);
            string safe = new((_source + (_tag.Length > 0 ? "-" + _tag : ""))
                .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            string path = Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Transcript(mine, header: true), Encoding.UTF8);
            try { Clipboard.SetText(path); } catch { }
            _toast($"Saved {mine.Count} line(s) — path copied");
        }
        // A save that fails must SAY which failure it was: "the folder is read-only" and "there is
        // no disk space" send you to different places, and "couldn't save" sends you to neither.
        catch (Exception ex) { _toast("Couldn't save: " + ex.Message); }
    }

    private string Transcript(List<ActivityEntry> mine, bool header)
    {
        var sb = new StringBuilder();
        if (header)
        {
            sb.AppendLine($"EQ Avatar {EQAvatar.Spike.Config.AppSettings.AppVersion} — {_source}"
                        + (_tag.Length > 0 ? $" · {_tag}" : ""));
            sb.AppendLine($"{mine.Count} line(s), {mine.Count(m => m.Detail)} of them detail. "
                        + $"Written {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            sb.AppendLine(new string('-', 60));
        }
        foreach (ActivityEntry e in mine)
            sb.Append(e.When.ToString(header ? "yyyy-MM-dd HH:mm:ss.fff" : "HH:mm:ss"))
              .Append(e.Detail ? "  [detail] " : "  ").AppendLine(e.Text);
        return sb.ToString();
    }

    private void ToggleDetail()
    {
        bool on = !_readDetail();
        _writeDetail(on);
        ActivityLog.DetailEnabled = on;
        ApplyDetailChip();
        // Recorded, not just toggled. When you read this log back a week later, "detail was on from
        // here" is the difference between a quiet stretch and a stretch nobody was watching.
        ActivityLog.Record(_source, on
            ? "· detail ON — she'll narrate the numbers behind each decision from here."
            : "· detail off — back to the plain narration.", _tag);
    }

    private void ApplyDetailChip()
    {
        bool on = _readDetail();
        _detailChip.Text = on ? "  detail: ON" : "  detail: off";
        _detailChip.Foreground = on ? Hex("#FF9E3D") : Hex("#4FC3F7");
        _detailChip.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        _detailChip.ToolTip = on
            ? "She is narrating match distances, click coordinates and raw OCR text. Click to go back "
            + "to the plain narration."
            : "Make her narrate the numbers behind each decision — match distances, click coordinates, "
            + "the raw text an OCR read before anything parsed it. Verbose on purpose; turn it on when "
            + "something has gone wrong.";
    }

    // ---------------------------------------------------------------- drawing

    private bool AtBottom()
    {
        ScrollViewer? sv = Scroll();
        return sv is null || sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 4;
    }

    private void ShowPill()
    {
        _newPill.Visibility = _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        _newPillText.Text = _unread == 1 ? "▼  1 new line" : $"▼  {_unread} new lines";
    }

    private void UpdateCount()
    {
        _countText.Text = _total == 0 ? ""
            : $"{_total} line(s)" + (_total > _drawn ? $" · showing {_drawn}" : "")
              + (_readDetail() ? " · detail on" : "");
    }

    private void SetNow(ActivityEntry? now)
    {
        // Remembered rather than re-derived from the text later. The halo is the whole reason this
        // panel is oversized, and a warning wearing the green "all is well" glow is worse than no
        // panel at all — so it is decided by the SAME IsBad the colour is decided by, not by
        // guessing at prefixes a second time and missing "Can't…" and "Stopped…".
        _nowBad = now?.IsBad ?? false;
        _nowEmpty = now is null;
        _nowText.Text = now?.Text ?? _emptyText;
        _nowText.Foreground = now is null ? Hex("#5E7C9A")
                            : now.IsBad ? Hex("#FFCB6B")
                            : now.IsGood ? Hex("#49F27E")
                            : now.Detail ? Hex("#9FB6CC") : Hex("#DDE7F0");
        ApplyNowGlow();
    }

    private bool _nowBad, _nowEmpty = true;

    private void ApplyNowGlow()
    {
        bool warm = _running && !_nowEmpty && !_nowBad;
        _nowText.Effect = warm
            ? new DropShadowEffect
              { Color = Color.FromRgb(0x49, 0xF2, 0x7E), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 }
            : null;
    }

    private static Paragraph Row(ActivityEntry e)
    {
        // Detail lines are deliberately quieter than the narration they explain: same stream, but
        // the eye should fall on "✔ merged" and not on the thirty distances underneath it.
        var p = new Paragraph(new Run($"{e.When:HH:mm:ss}  {(e.Detail ? "· " : "")}{e.Text}"))
        {
            Margin = new Thickness(0),
            Foreground = e.IsBad ? Hex("#FFCB6B")
                       : e.IsGood ? Hex("#7CE38B")
                       : e.Detail ? Hex("#6B7F94")
                       : e.IsStep ? Hex("#8AA0B6") : Hex("#C6D2DE"),
        };
        if (e.Detail) p.FontStyle = FontStyles.Italic;
        return p;
    }

    private static TextBlock Link(string text, string tip, MouseButtonEventHandler onClick)
    {
        var tb = new TextBlock
        {
            Text = "  " + text, FontSize = 9.5, Foreground = Hex("#4FC3F7"),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
        };
        if (tip.Length > 0) tb.ToolTip = tip;
        tb.MouseLeftButtonUp += onClick;
        return tb;
    }

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
