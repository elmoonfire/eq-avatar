using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Login;
using EQAvatar.Spike.Ocr;
using EQAvatar.Spike.Roles;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The Questing page (partial class): every quest on the EQL wiki as a filterable table, and —
/// for the ones that end in handing an item to an NPC — a per-quest automation you build once
/// and she repeats.
///
/// The catalog itself is <see cref="QuestCatalog"/>: built on the hub from the wiki's own API,
/// downloaded as one file and cached. That is what lets the START ZONE and END ZONE filters be
/// dropdowns of real zone names rather than another box to type a guess into.
///
/// The rows are drawn by hand rather than bound to a ListView for the same reason the Key
/// Mappings rows are: the column titles are pictures, the cells are pills and badges, and a row
/// expands in place into a full walkthrough card. With 880-odd quests the drawn set is capped
/// (see <see cref="RowCap"/>) and the count line always says when rows were left off — a silent
/// truncation would read as "that's all there is".
/// </summary>
public partial class MainWindow
{
    private const int RowCap = 200;

    private bool _qstInit;
    private bool _qstFilling;                       // suppress filter events while dropdowns populate
    private bool _qstBusy;
    private string? _qstOpen;                       // the quest whose detail card is expanded
    private QuestRole? _questRun;
    /// <summary>The quest whose script <see cref="_questRun"/> is executing — so an unrelated
    /// card that happens to be open doesn't dress itself in the running quest's counters.</summary>
    private string _questRunFor = "";
    /// <summary>Scripts already offered the catalog's dialogue triggers this session — so clearing
    /// the "say after hail" box on purpose doesn't get refilled on the next render.</summary>
    private readonly HashSet<string> _sayBackfilled = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Label, int Lo, int Hi)[] LevelBands =
    {
        ("any level", 0, 999), ("1 – 9", 1, 9), ("10 – 19", 10, 19), ("20 – 29", 20, 29),
        ("30 – 39", 30, 39), ("40 – 49", 40, 49), ("50 – 59", 50, 59), ("60+", 60, 999),
    };

    private void InitQuestingUi()
    {
        if (!_qstInit)
        {
            _qstInit = true;
            ArtCache.Bind(ArtQuestingBanner, "ui-questing-banner.jpg");
            ArtCache.Bind(ArtQColQuest, "ui-q-col-quest.jpg");
            ArtCache.Bind(ArtQColZone, "ui-q-col-zone.jpg");
            ArtCache.Bind(ArtQColStartNpc, "ui-q-col-startnpc.jpg");
            ArtCache.Bind(ArtQColEndNpc, "ui-q-col-endnpc.jpg");
            ArtCache.Bind(ArtQColLevel, "ui-q-col-level.jpg");
            ArtCache.Bind(ArtQColReward, "ui-q-col-reward.jpg");
            ArtCache.Bind(ArtQColAuto, "ui-q-col-auto.jpg");
            ArtCache.Bind(ArtQColDone, "ui-q-col-done.jpg");

            _qstFilling = true;
            QstFilterLevel.Items.Clear();
            foreach ((string label, _, _) in LevelBands) QstFilterLevel.Items.Add(label);
            QstFilterLevel.SelectedIndex = 0;
            QstFilterDone.Items.Clear();
            foreach (string o in new[] { "any", "completed", "never" }) QstFilterDone.Items.Add(o);
            QstFilterDone.SelectedIndex = 0;
            QstSortDone.Items.Clear();
            foreach (string o in new[] { "sort: name", "first done ↑", "first done ↓", "times done ↓", "times done ↑" })
                QstSortDone.Items.Add(o);
            QstSortDone.SelectedIndex = 0;
            _qstFilling = false;

            _ = LoadQuestsAsync(force: false);
            return;
        }
        RenderQuests();
    }

    /// <summary>
    /// Grow the column scenes with the window.
    ///
    /// They were a fixed 66 px, which is right at 1200 px wide and wrong at 2600: the columns
    /// stretch, the pictures don't, and a row of wide letterboxed slivers is what you get. Height
    /// tracks the grid's own width so the tiles keep something close to their painted proportions,
    /// clamped so they can never eat the list. Done in code rather than a binding because a
    /// converter in XAML is one more thing that resolves at runtime and can't be seen failing here.
    /// </summary>
    private void QstHeads_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AutoHead is null) return;                        // the LAST-declared of the seven
        double h = Math.Clamp(e.NewSize.Width * 0.058, 66, 150);
        foreach (Border b in new[] { QuestHead, ZoneHead, StartNpcHead, EndNpcHead, LevelHead, RewardHead, DoneHead, AutoHead })
            if (b is not null) b.Height = h;
    }

    private async Task LoadQuestsAsync(bool force)
    {
        if (_qstBusy) return;
        _qstBusy = true;
        QstRefreshBtn.IsEnabled = false;
        QstStatus.Text = QuestCatalog.Loaded ? "checking the hub for a newer catalog…" : "loading the quest catalog…";
        try
        {
            (bool ok, string status) = await QuestCatalog.EnsureAsync(force);
            QstStatus.Text = status;
            QstStatus.Foreground = ok ? Hex("#7CE38B") : Hex("#FFCB6B");
            FillZoneDropdowns();
            RenderQuests();
            Diag.BotLog.Log("quests", $"catalog: {status}");
        }
        catch (Exception ex)
        {
            // Reached from an async void handler otherwise, which lands in the app's fatal-error
            // box. A catalog this page can't draw is a bad page, not a dead app.
            QstStatus.Text = "Couldn't show the catalog: " + ex.Message;
            QstStatus.Foreground = Hex("#FFCB6B");
            Diag.BotLog.Log("quests", "render error: " + ex);
        }
        finally
        {
            _qstBusy = false;
            QstRefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>The zone dropdowns list the zones actually present in the catalog, so a filter can
    /// never select a zone with nothing behind it.</summary>
    private void FillZoneDropdowns()
    {
        _qstFilling = true;
        try
        {
            foreach (ComboBox box in new[] { QstFilterStartZone, QstFilterEndZone })
            {
                object? was = box.SelectedItem;
                box.Items.Clear();
                box.Items.Add(box == QstFilterEndZone ? "any end zone" : "any zone");
                foreach (string z in QuestCatalog.Zones) box.Items.Add(z);
                box.SelectedItem = was is string s && box.Items.Contains(s) ? was : box.Items[0];
            }
        }
        finally { _qstFilling = false; }
    }

    // ---------------------------------------------------------------- filtering

    private static bool QstCell(string cell, string filter) => CellMatches(cell ?? "", filter ?? "");

    private static string PickedZone(ComboBox box) =>
        box.SelectedIndex <= 0 ? "" : box.SelectedItem as string ?? "";

    private IEnumerable<QuestInfo> FilteredQuests()
    {
        string fn = QstFilterName?.Text ?? "", fsn = QstFilterStartNpc?.Text ?? "";
        string fen = QstFilterEndNpc?.Text ?? "", fr = QstFilterReward?.Text ?? "";
        string sz = QstFilterStartZone is null ? "" : PickedZone(QstFilterStartZone);
        string ez = QstFilterEndZone is null ? "" : PickedZone(QstFilterEndZone);
        int band = Math.Clamp(QstFilterLevel?.SelectedIndex ?? 0, 0, LevelBands.Length - 1);
        (_, int lo, int hi) = LevelBands[band];
        bool autoOnly = QstAutoOnly?.IsChecked == true;
        int doneFilter = QstFilterDone?.SelectedIndex ?? 0;      // 0 any · 1 completed · 2 never
        int sort = QstSortDone?.SelectedIndex ?? 0;

        IEnumerable<QuestInfo> rows = QuestCatalog.Quests
            .Where(q => QstCell(q.Name, fn))
            .Where(q => sz.Length == 0 || string.Equals(q.StartZone, sz, StringComparison.OrdinalIgnoreCase))
            .Where(q => QstCell(q.StartNpc, fsn))
            .Where(q => QstCell(q.EndNpc, fen))
            .Where(q => ez.Length == 0 || string.Equals(q.EndZone, ez, StringComparison.OrdinalIgnoreCase))
            .Where(q => band == 0 || (q.LevelMin >= lo && q.LevelMin <= hi))
            .Where(q => QstCell(q.RewardText == "—" ? "" : q.RewardText, fr))
            .Where(q => !autoOnly || q.Automatable);

        if (doneFilter == 1) rows = rows.Where(q => QuestCompletions.Get(q.Name) is not null);
        else if (doneFilter == 2) rows = rows.Where(q => QuestCompletions.Get(q.Name) is null);

        // Sorted quests without a history sink to the bottom rather than jumbling in — when you
        // sort by "first done", the quests you HAVE done are the ones you're asking about.
        rows = sort switch
        {
            1 => rows.OrderBy(q => QuestCompletions.Get(q.Name)?.First ?? DateTime.MaxValue),
            2 => rows.OrderByDescending(q => QuestCompletions.Get(q.Name)?.First ?? DateTime.MinValue),
            3 => rows.OrderByDescending(q => QuestCompletions.Get(q.Name)?.Count ?? 0),
            4 => rows.OrderBy(q => QuestCompletions.Get(q.Name) is { } a ? a.Count : int.MaxValue),
            _ => rows,
        };
        return rows;
    }

    private bool QstFiltering() =>
        (QstFilterName?.Text ?? "").Trim().Length > 0
        || (QstFilterStartNpc?.Text ?? "").Trim().Length > 0
        || (QstFilterEndNpc?.Text ?? "").Trim().Length > 0
        || (QstFilterReward?.Text ?? "").Trim().Length > 0
        || (QstFilterStartZone?.SelectedIndex ?? 0) > 0
        || (QstFilterEndZone?.SelectedIndex ?? 0) > 0
        || (QstFilterLevel?.SelectedIndex ?? 0) > 0
        || (QstFilterDone?.SelectedIndex ?? 0) > 0
        || QstAutoOnly?.IsChecked == true;

    private void QstFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (QstListHost is null || _qstFilling) return;      // fires during InitializeComponent otherwise
        RenderQuests();
    }

    private void QstFilterSel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QstListHost is null || _qstFilling) return;
        RenderQuests();
    }

    private void QstFilter_Click(object sender, RoutedEventArgs e)
    {
        if (QstListHost is null) return;
        RenderQuests();
    }

    private void QstClear_Click(object sender, RoutedEventArgs e)
    {
        _qstFilling = true;
        QstFilterName.Text = QstFilterStartNpc.Text = QstFilterEndNpc.Text = QstFilterReward.Text = "";
        if (QstFilterStartZone.Items.Count > 0) QstFilterStartZone.SelectedIndex = 0;
        if (QstFilterEndZone.Items.Count > 0) QstFilterEndZone.SelectedIndex = 0;
        QstFilterLevel.SelectedIndex = 0;
        if (QstFilterDone.Items.Count > 0) QstFilterDone.SelectedIndex = 0;
        if (QstSortDone.Items.Count > 0) QstSortDone.SelectedIndex = 0;
        QstAutoOnly.IsChecked = false;
        _qstFilling = false;
        RenderQuests();
    }

    private async void QstRefresh_Click(object sender, RoutedEventArgs e) => await LoadQuestsAsync(force: true);

    // ---------------------------------------------------------------- rendering

    private void RenderQuests()
    {
        if (QstListHost is null) return;

        if (QuestCatalog.Generated is { } built)
        {
            QstStampBorder.Background = Hex("#10281A");
            QstStampBorder.BorderBrush = Hex("#2E7D4F");
            QstStampText.Foreground = Hex("#9FE0B8");
            DateTime local = built.ToLocalTime();
            QstStampText.Text = "built " + (local.Date == DateTime.Today ? $"today {local:HH:mm}" : local.ToString("MMM d, HH:mm"));
        }
        else
        {
            QstStampBorder.Background = Hex("#2A2410");
            QstStampBorder.BorderBrush = Hex("#7A6320");
            QstStampText.Foreground = Hex("#FFE1A6");
            QstStampText.Text = "not loaded yet";
        }

        QstListHost.Children.Clear();
        List<QuestInfo> shown = FilteredQuests().ToList();
        int total = QuestCatalog.Quests.Count;
        int automatable = QuestCatalog.Quests.Count(q => q.Automatable);
        int scripted = QuestScriptStore.Current.ReadyCount;
        bool capped = shown.Count > RowCap;

        QstCountText.Text = total == 0
            ? "no catalog loaded"
            : $"{total} quests from {QuestCatalog.Source}"
              + (QstFiltering() ? $" · {shown.Count} shown" : "")
              + (capped ? $" · only the first {RowCap} are drawn — narrow a filter to reach the rest" : "")
              + $" · {automatable} have an automatable hand-in"
              + (QuestCompletions.CompletedQuestCount > 0 ? $" · {QuestCompletions.CompletedQuestCount} completed by you" : "")
              + (scripted > 0 ? $" · {scripted} built" : "");

        if (shown.Count == 0)
        {
            QstListHost.Children.Add(new TextBlock
            {
                Text = total == 0
                    ? "No quest catalog yet. Press ⟳ Refresh from hub — it's one download, then it's cached and works offline."
                    : "nothing matches these filters",
                Foreground = Hex("#7E93A8"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 8),
            });
            return;
        }

        foreach (QuestInfo q in shown.Take(RowCap))
        {
            QstListHost.Children.Add(MakeQuestRow(q));
            if (_qstOpen is not null && QuestCatalog.Norm(_qstOpen) == QuestCatalog.Norm(q.Name))
                QstListHost.Children.Add(MakeQuestDetail(q));
        }
    }

    private static Grid QuestGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        return grid;
    }

    private FrameworkElement MakeQuestRow(QuestInfo q)
    {
        bool open = _qstOpen is not null && QuestCatalog.Norm(_qstOpen) == QuestCatalog.Norm(q.Name);
        QuestScript? existing = QuestScriptStore.Current.Find(q.Name);
        bool built = existing?.Ready == true;
        bool started = existing is not null;

        Grid grid = QuestGrid();
        grid.Margin = new Thickness(6, 0, 6, 0);

        TextBlock Cell(string text, string colour, int col, double size = 12, string? tip = null) => new()
        {
            Text = text.Length == 0 ? "—" : text,
            Foreground = text.Length == 0 ? Hex("#3C4C60") : Hex(colour),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = tip,
        };

        var name = Cell(q.Name, "#DDE7F0", 0, 12.5, q.Url);
        name.FontWeight = open ? FontWeights.Bold : FontWeights.Normal;
        Grid.SetColumn(name, 0); grid.Children.Add(name);

        var sz = Cell(q.StartZone, "#9FE0FF", 1, 11.5); Grid.SetColumn(sz, 1); grid.Children.Add(sz);
        var sn = Cell(q.StartNpc, "#C792EA", 2, 11.5); Grid.SetColumn(sn, 2); grid.Children.Add(sn);

        var endCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        endCell.Children.Add(Cell(q.EndNpc, "#FFCB6B", 3, 11.5));
        if (!string.Equals(q.EndZone, q.StartZone, StringComparison.OrdinalIgnoreCase) && q.EndZone.Length > 0)
            endCell.Children.Add(new TextBlock { Text = q.EndZone, Foreground = Hex("#7E93A8"), FontSize = 10 });
        Grid.SetColumn(endCell, 3); grid.Children.Add(endCell);

        var lvl = Cell(q.LevelText, "#7CE38B", 4, 11.5);
        lvl.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(lvl, 4); grid.Children.Add(lvl);

        var rew = Cell(q.RewardText == "—" ? "" : q.RewardText, "#FFB3D9", 5, 11.5, q.RewardText);
        Grid.SetColumn(rew, 5); grid.Children.Add(rew);

        // your own history: first completion + a ×count pill, from QuestCompletions
        FrameworkElement doneCell;
        if (QuestCompletions.Get(q.Name) is { } done)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = done.First.ToString("MMM d HH:mm"),
                Foreground = Hex("#9FE0B8"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"First completed {done.First:F}\nLast completed {done.Last:F}",
            });
            sp.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999),
                Background = Hex("#12301F"), BorderBrush = Hex("#2E7D4F"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "×" + done.Count, Foreground = Hex("#9FE0B8"), FontSize = 10 },
                ToolTip = $"Completed {done.Count} time(s) on this machine.",
            });
            doneCell = sp;
        }
        else
        {
            doneCell = new TextBlock
            {
                Text = "—", Foreground = Hex("#3C4C60"), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Never completed on this machine (as far as the Quest Runner has seen).",
            };
        }
        Grid.SetColumn(doneCell, 6); grid.Children.Add(doneCell);

        FrameworkElement badge = q.Automatable
            ? new Border
            {
                CornerRadius = new CornerRadius(999),
                Background = built ? Hex("#12301F") : Hex("#2A1C10"),
                BorderBrush = built ? Hex("#2E7D4F") : Hex("#7A4E20"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 1, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = built ? "built" : started ? "part" : "can",
                    Foreground = built ? Hex("#9FE0B8") : Hex("#FFC08A"),
                    FontSize = 10.5,
                },
                ToolTip = built
                    ? "Ready to run — open the row and press Run."
                    : started
                        ? "Started, but it still needs a pick before it can run — open the row to finish it."
                        : "This quest ends in a hand-in she can repeat. Open the row to build it.",
            }
            : new TextBlock
            {
                Text = "—",
                Foreground = Hex("#3C4C60"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The wiki doesn't record a hand-in for this quest, so there's no fixed gesture to repeat.",
            };
        Grid.SetColumn(badge, 7); grid.Children.Add(badge);

        var row = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 0, 0, 3),
            Background = open ? Hex("#14212F") : Brushes.Transparent,
            BorderBrush = open ? Hex("#2A4A57") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = "Click for the walkthrough details" + (q.Automatable ? " and the hand-in automation." : "."),
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            _qstOpen = open ? null : q.Name;
            RenderQuests();
        };
        return row;
    }

    // ---------------------------------------------------------------- the detail / automation card

    private FrameworkElement MakeQuestDetail(QuestInfo q)
    {
        var stack = new StackPanel();

        void Line(string label, string value, string colour = "#C6D2DE")
        {
            if (value.Trim().Length == 0) return;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, Foreground = Hex("#5E7C9A"), FontSize = 10.5, FontWeight = FontWeights.Bold };
            var v = new TextBlock { Text = value, Foreground = Hex(colour), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
            stack.Children.Add(g);
        }

        Line("STARTS", $"{q.StartNpc} in {q.StartZone}", "#9FE0FF");
        Line("ENDS", $"{q.EndNpc} in {q.EndZone}", "#FFCB6B");
        Line("LEVEL", q.LevelText, "#7CE38B");
        Line("CLASSES", q.ClassText);
        Line("HAND IN", q.TurnIns.Count == 0 ? "" : string.Join(", ", q.TurnIns.Select(t => $"{t.Qty}× {t.Item} → {t.Npc}")), "#FFC08A");
        Line("SAY", string.Join("  ·  ", q.SayPhrases), "#9FE0FF");
        Line("REWARD", q.RewardText == "—" ? "" : q.RewardText, "#FFB3D9");
        Line("EXPERIENCE", q.ExpText);
        Line("FACTION", string.Join(", ", q.Factions.Select(f => $"{f.Faction} {(f.Delta > 0 ? "+" : "")}{f.Delta}")));
        Line("ALSO SEE", string.Join(", ", q.RelatedNpcs));
        Line("LOCATIONS", string.Join("   ·   ", q.Locs.Select(l => $"{l.Who}: {l.LocText}")), "#9FE0FF");
        Line("ERA", q.Era);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var wiki = new Button { Content = "Open the wiki page ↗", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        wiki.ToolTip = q.Url;
        wiki.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(q.Url) { UseShellExecute = true }); }
            catch (Exception ex) { QstStatus.Text = "Couldn't open the browser: " + ex.Message; }
        };
        links.Children.Add(wiki);
        stack.Children.Add(links);

        if (q.Automatable) stack.Children.Add(MakeAutomationCard(q));

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Hex("#0E1622"),
            BorderBrush = Hex("#26405A"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(6, 0, 6, 10),
            Child = stack,
        };
    }

    /// <summary>
    /// The build-and-run card: an ordered list of hand-ins, the two picks they all share, a
    /// couple of switches and a start button.
    ///
    /// It is a LIST because that is what farming a quest chain looks like. On Kerra Isle the
    /// Desecrated Kejaar Totem finishes "Something is Wrrrong", which immediately assigns
    /// "This Means Warrr", whose Heretic Insurrection Orders go back to the same cat and re-open
    /// the first quest — so the unit worth repeating is both hand-ins, not either one.
    ///
    /// The picks exist because there is nothing to discover: the game has no addon API, the log
    /// says nothing about inventory or about what is drawn on screen, and an inventory slot's
    /// picture changes the moment the item leaves it — so a template match of the slot would work
    /// once and fail forever after. A position doesn't change, so a position is what gets stored,
    /// normalized to the game window so it survives the window moving.
    /// </summary>
    private FrameworkElement MakeAutomationCard(QuestInfo q)
    {
        // Look it up; only BUILD one in memory. Nothing is stored until the user changes something
        // — expanding a row to read it must not leave an empty automation behind, or the AUTO
        // column ends up claiming a dozen quests are "built" when none of them are.
        QuestScript script = QuestScriptStore.Current.Find(q.Name) ?? QuestScript.FromQuest(q);
        // Back-fill dialogue triggers into scripts built before the catalog carried them: without
        // "explorrre the island" said after the hail, the task never enters the journal and the
        // very first Totem offer goes unanswered. Persisted with the next edit or Run.
        if (script.SayPhrases.Count == 0 && q.SayPhrases.Count > 0 && _sayBackfilled.Add(q.Name))
            script.SayPhrases = new List<string>(q.SayPhrases);
        void Persist() => QuestScriptStore.Current.Adopt(script);
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "REPEAT THIS TURN-IN CYCLE",
            FontSize = 9.5, FontWeight = FontWeights.Bold,
            Foreground = Hex("#5E7C9A"),
            Margin = new Thickness(0, 14, 0, 6),
        });
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Hex("#9FB6CC"),
            Margin = new Thickness(0, 0, 0, 8),
            Text = $"One cycle: she targets {script.Npc}, hails, then hands over each item below in order — "
                 + "picking it out of your bag, dropping it on the NPC and pressing GIVE. She waits for the game's "
                 + "own log to confirm every hand-in and stops when two in a row go unanswered, which is what "
                 + "running out of an item looks like. Stand in front of the NPC with the items in your bags, show "
                 + "her the spots below once, then press Run.",
        });

        // ---- the shared picks, as scene tiles ----
        var picks = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        picks.Children.Add(MakePickTile("ui-pick-npc.jpg", "the NPC", 
            script.NpcAnchorLearned ? "found by nameplate" : "where the items land", "",
            script.Layout.Npc.Set,
            "Where she drops the held item. Target the NPC FIRST so the big name floats over his head — the pick "
            + "then learns a nameplate anchor, and she finds him by name wherever he stands. Click to pick.",
            () => { if (PickQuestPoint(script.Layout.Npc, "the NPC",
                        $"TARGET {script.Npc} so his name floats overhead, then click ON his body and press Enter.",
                        (frame, _) => LearnNpcAnchorAsync(script, new System.Drawing.Bitmap(frame),
                                                          script.Layout.Npc.X, script.Layout.Npc.Y)))
                    Persist(); RenderQuests(); }));
        picks.Children.Add(MakePickTile("ui-pick-give.jpg", "GIVE button", "commits the trade", "",
            script.Layout.GiveButton.Set,
            "The button that completes the hand-in. The give window opens in the same place every time, so one pick covers every item. Click to pick.",
            () => { if (PickQuestPoint(script.Layout.GiveButton, "the GIVE button",
                        "Open a give window with the NPC, then click ON its GIVE button and press Enter.")) Persist(); RenderQuests(); }));
        picks.Children.Add(MakePickTile("ui-pick-confirm.jpg", "confirm", "optional dialog", "",
            script.Layout.Confirm.Set,
            "Only needed if the server puts a second dialog up after GIVE. The cycle runs without it. Click to pick.",
            () => { if (PickQuestPoint(script.Layout.Confirm, "the confirm button",
                        "If a confirmation appears after GIVE, click ON its button and press Enter.")) Persist(); RenderQuests(); }));
        picks.Children.Add(MakePickTile("ui-pick-bag.jpg", "the bag area",
            $"{script.BagCols}×{script.BagRows} slots, scanned for icons", "",
            script.BagSet,
            "Drag ONE box around the whole block of bag slots the quest items live in. At run time she scans its "
            + "cells for each item's icon and clicks the copy that's actually there — the picked slots become "
            + "fallbacks. Click to pick, then set columns × rows below.",
            () => { if (PickQuestRect(r => { script.BagX = r.X; script.BagY = r.Y; script.BagW = r.W; script.BagH = r.H; },
                        "the bag area",
                        "Drag a box around the WHOLE block of bag slots holding the quest items — corner to corner — then press Enter."))
                    Persist(); RenderQuests(); }));
        stack.Children.Add(picks);

        // ---- what she says after the hail: the dialogue triggers ----
        var sayBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        sayBar.Children.Add(new TextBlock
        {
            Text = "say after hail", Foreground = Hex("#9FB6CC"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "The bracketed words the NPC asks you to say back — saying them does the same thing as "
                    + "clicking the bracketed link in chat, and it is what puts the task in your journal. "
                    + "Spoken in order after the hail, every cycle. Separate several with ;",
        });
        var sayBox = new TextBox
        {
            Text = string.Join(" ; ", script.SayPhrases), Width = 320, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Pre-filled from the wiki's own transcript when it records the phrase. Blank = nothing said.",
        };
        sayBox.LostFocus += (_, _) =>
        {
            script.SayPhrases = sayBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            sayBox.Text = string.Join(" ; ", script.SayPhrases);
            Persist();
        };
        sayBar.Children.Add(sayBox);
        if (script.SayPhrases.Count > 0)
            sayBar.Children.Add(new TextBlock
            {
                Text = "puts the task in the journal", Foreground = Hex("#5E7C9A"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            });
        stack.Children.Add(sayBar);

        // ---- the hand-ins, in order, as numbered tiles + the fire bar ----
        stack.Children.Add(new TextBlock
        {
            Text = "HAND-INS, IN ORDER",
            FontSize = 9.5, FontWeight = FontWeights.Bold,
            Foreground = Hex("#5E7C9A"),
            Margin = new Thickness(0, 6, 0, 5),
        });

        var steps = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        int total = script.Steps.Count;
        for (int i = 0; i < total; i++)
        {
            TurnInStep step = script.Steps[i];
            TurnInStep captured = step;
            string shortQuest = string.Equals(step.Quest, script.Quest, StringComparison.OrdinalIgnoreCase) || step.Quest.Length == 0
                ? (step.Qty > 1 ? $"{step.Qty}× per hand-in" : "one per hand-in")
                : "for " + step.Quest;

            var tileHost = new Grid();
            tileHost.Children.Add(MakePickTile("ui-pick-slot.jpg",
                step.Item.Length > 0 ? step.Item : "hand-in item",
                step.HasIcon ? "icon learned — found live" : shortQuest,
                $"{i + 1} of {total}",
                step.Slot.Set,
                "Drag a TIGHT box around one copy of the item in your bag. The pick learns the item's ICON, so at "
                + "run time she scans the bag area for wherever a copy actually is — the picked slot is only the "
                + "fallback. Click to pick.",
                () => { if (PickQuestPoint(captured.Slot, "where " + captured.Item + " sits in your bags",
                            $"Drag a TIGHT box around one {captured.Item} in your inventory, then press Enter.",
                            (frame, box) =>
                            {
                                captured.IconSig = QuestFind.SigFromRegion(frame, box.X, box.Y, box.W, box.H);
                                if (captured.IconSig is not null)
                                    QstStatus.Text = $"Saved — and learned {captured.Item}'s icon, so she'll find the "
                                                   + "next copy wherever it sits in the bag area.";
                            })) Persist(); RenderQuests(); }));

            if (total > 1)
            {
                var del = new Border
                {
                    CornerRadius = new CornerRadius(999), Width = 18, Height = 18,
                    Background = Hex("#C4140B12"), BorderBrush = Hex("#4A3040"), BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 12, 12),
                    ToolTip = "Remove this hand-in from the cycle.",
                    Child = new TextBlock
                    {
                        Text = "✕", Foreground = Hex("#C98B9E"), FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                del.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;                       // don't let the tile underneath open the picker
                    QuestScriptStore.Current.Edit(() => script.Steps.Remove(captured));
                    Persist();
                    RenderQuests();
                };
                tileHost.Children.Add(del);
            }
            steps.Children.Add(tileHost);
        }
        stack.Children.Add(steps);

        // The fire bar. While a run is live it burns down the CYCLE — hand-ins confirmed out of
        // the total this time round; idle, it shows how much of the setup is picked, so the same
        // bar answers "how far along is she?" and "how far along am I?" depending on who's working.
        bool runningNow = _questRun is { Running: true }
                       && QuestCatalog.Norm(_questRunFor) == QuestCatalog.Norm(script.Quest);
        double fireFraction; string fireCaption;
        if (runningNow && total > 0)
        {
            int inCycle = Math.Max(0, _questRun!.Stats.HandIns - _questRun.Stats.Cycles * total);
            fireFraction = Math.Min(1.0, (double)inCycle / total);
            fireCaption = $"{inCycle} of {total} hand-in(s) this cycle · {_questRun.Stats.Cycles} full cycle(s) done";
        }
        else
        {
            int need = 2 + total;                            // NPC + GIVE + one slot per item
            int have = (script.Layout.Npc.Set ? 1 : 0) + (script.Layout.GiveButton.Set ? 1 : 0)
                     + script.Steps.Count(st => st.Slot.Set);
            fireFraction = need == 0 ? 0 : (double)have / need;
            fireCaption = have >= need ? "everything picked — ready to run" : $"{have} of {need} picks made";
        }
        stack.Children.Add(MakeFireBar(fireFraction, fireCaption));

        // ---- add a hand-in from the quest this one leads into ----
        var addBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var addBox = new ComboBox
        {
            Width = 320, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Every other hand-in in the catalog that goes to this same NPC — which is exactly what a "
                    + "follow-on quest looks like.",
        };
        var candidates = new List<(QuestInfo Quest, QuestTurnIn TurnIn)>();
        string npcKey = QuestCatalog.Norm(script.Npc);
        // Only when we actually know who the NPC is: Norm("") == Norm("") would otherwise match
        // every blank-NPC hand-in in the catalog and offer all 884 quests as plausible follow-ons.
        if (npcKey.Length > 0)
            foreach (QuestInfo other in QuestCatalog.Quests)
                foreach (QuestTurnIn t in other.TurnIns)
                    if (QuestCatalog.Norm(t.Npc) == npcKey
                        && !script.Steps.Any(s => QuestCatalog.Norm(s.Item) == QuestCatalog.Norm(t.Item)))
                        candidates.Add((other, t));
        foreach ((QuestInfo other, QuestTurnIn t) in candidates)
            addBox.Items.Add($"{t.Item}  ·  {other.Name}");
        addBox.IsEnabled = candidates.Count > 0;
        if (candidates.Count == 0) addBox.ToolTip = "No other hand-in in the catalog goes to this NPC.";

        var addBtn = new Button
        {
            Content = "＋ add to the cycle", Padding = new Thickness(12, 3, 12, 3),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = candidates.Count > 0,
            ToolTip = "Append that hand-in after the ones above. Use this when finishing one quest immediately "
                    + "opens the next — the pair is what you actually repeat.",
        };
        addBtn.Click += (_, _) =>
        {
            int i = addBox.SelectedIndex;
            if (i < 0 || i >= candidates.Count) { QstStatus.Text = "Pick a hand-in from the list first."; return; }
            (QuestInfo other, QuestTurnIn t) = candidates[i];
            QuestScriptStore.Current.Edit(() =>
                script.Steps.Add(new TurnInStep { Item = t.Item, Qty = Math.Max(1, t.Qty), Quest = other.Name }));
            Persist();
            RenderQuests();
        };
        addBar.Children.Add(addBox);
        addBar.Children.Add(addBtn);
        stack.Children.Add(addBar);

        // ---- the switches ----
        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var hail = new CheckBox
        {
            Content = "hail first", IsChecked = script.HailFirst, Foreground = Hex("#9FB6CC"), FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Press the in-game hail key with the NPC targeted at the top of each cycle — one keystroke, "
                    + "not a typed sentence. On Kerra Isle the hail is also what re-assigns the task.",
        };
        hail.Click += (_, _) => { script.HailFirst = hail.IsChecked == true; Persist(); };

        var hailKey = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(script.HailKey) ? "h" : script.HailKey,
            Width = 34, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "Your hail key. EQL binds \"h\" by default — change it here if you've rebound it.",
        };
        hailKey.LostFocus += (_, _) =>
        {
            script.HailKey = hailKey.Text.Trim().Length > 0 ? hailKey.Text.Trim() : "h";
            hailKey.Text = script.HailKey;
            Persist();
        };

        var targ = new CheckBox
        {
            Content = "/target by name", IsChecked = script.TargetByName, Foreground = Hex("#9FB6CC"), FontSize = 11,
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Type /target <NPC> before hailing rather than trusting whatever is currently selected.",
        };
        targ.Click += (_, _) => { script.TargetByName = targ.IsChecked == true; Persist(); };

        var smart = new CheckBox
        {
            Content = "smart find", IsChecked = script.SmartFind, Foreground = Hex("#9FB6CC"), FontSize = 11,
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Find each item by its ICON in the bag area, and the NPC by his NAMEPLATE — instead of "
                    + "trusting the fixed picks. The fixed picks stay as fallbacks either way.",
        };
        smart.Click += (_, _) => { script.SmartFind = smart.IsChecked == true; Persist(); };

        opts.Children.Add(smart);
        opts.Children.Add(new TextBlock { Text = "bag", Foreground = Hex("#9FB6CC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var bagCols = new TextBox
        {
            Text = script.BagCols.ToString(), Width = 34, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Columns across the dragged bag area.",
        };
        var bagRows = new TextBox
        {
            Text = script.BagRows.ToString(), Width = 34, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 14, 0), ToolTip = "Rows down the dragged bag area.",
        };
        bagCols.LostFocus += (_, _) =>
        {
            script.BagCols = int.TryParse(bagCols.Text.Trim(), out int c) ? Math.Clamp(c, 1, 20) : script.BagCols;
            bagCols.Text = script.BagCols.ToString(); Persist();
        };
        bagRows.LostFocus += (_, _) =>
        {
            script.BagRows = int.TryParse(bagRows.Text.Trim(), out int r) ? Math.Clamp(r, 1, 20) : script.BagRows;
            bagRows.Text = script.BagRows.ToString(); Persist();
        };
        opts.Children.Add(bagCols);
        opts.Children.Add(new TextBlock { Text = "×", Foreground = Hex("#5E7C9A"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        opts.Children.Add(bagRows);
        opts.Children.Add(hail);
        opts.Children.Add(hailKey);
        opts.Children.Add(targ);
        opts.Children.Add(new TextBlock { Text = "repeat", Foreground = Hex("#9FB6CC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var repeat = new TextBox
        {
            Text = script.Repeat.ToString(), Width = 56, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "How many full cycles to run. 0 = keep going until the items run out.",
        };
        repeat.LostFocus += (_, _) =>
        {
            script.Repeat = int.TryParse(repeat.Text.Trim(), out int r) ? Math.Clamp(r, 0, 999) : 0;
            repeat.Text = script.Repeat.ToString();
            Persist();
        };
        opts.Children.Add(repeat);
        opts.Children.Add(new TextBlock
        {
            Text = "cycles · 0 = until they run out", Foreground = Hex("#5E7C9A"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
        stack.Children.Add(opts);

        // ---- run / stop ----
        bool running = _questRun is { Running: true }
                    && QuestCatalog.Norm(_questRunFor) == QuestCatalog.Norm(script.Quest);
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var run = new Button
        {
            Content = running ? "■  Stop" : "▶  Run the cycle",
            Padding = new Thickness(14, 5, 14, 5),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = running
                ? "Stop the run. F12 and tabbing away from the game also stop it."
                : "Start repeating this cycle. The game must be the focused window; tab away and she pauses.",
        };
        run.Click += (_, _) => { if (_questRun is { Running: true }) _questRun.Stop(); else { Persist(); StartQuestRun(script); } };
        bar.Children.Add(run);

        var hover = new Button
        {
            Content = "🖱  Test the clicks",
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Three-second countdown, then the cursor visits every picked point in order — bag slots, NPC, "
                    + "GIVE — WITHOUT clicking, pausing a second on each. Watch it over the game: every stop should "
                    + "sit exactly on its target. If one is off, that pick is the problem.",
        };
        hover.Click += async (_, _) => await HoverTestAsync(script);
        bar.Children.Add(hover);

        var stats = new TextBlock
        {
            Foreground = Hex("#9FE0B8"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Text = running
                ? $"{_questRun!.Stats.State} · {_questRun.Stats.Cycles} cycle(s), {_questRun.Stats.HandIns} hand-in(s)"
                : script.LifetimeCompleted > 0 ? $"{script.LifetimeCompleted} cycle(s) all time" : "",
        };
        bar.Children.Add(stats);
        stack.Children.Add(bar);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Hex("#121A28"),
            BorderBrush = Hex("#2A4A57"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 4, 12, 12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = stack,
        };
    }

    // ---------------------------------------------------------------- picking + running

    private bool _hoverTestBusy;

    /// <summary>
    /// Walk the cursor through every picked point without clicking anything.
    ///
    /// This exists because the first field test failed invisibly: keyboard actions landed (hail,
    /// say) but no click was ever seen, and a click that goes to the wrong place — or a pick that
    /// silently isn't what you thought — looks exactly like a bot doing nothing. A second of
    /// hovering on each target over the live game answers "are the coordinates right?" with your
    /// own eyes, before any item is put at risk.
    /// </summary>
    private async Task HoverTestAsync(QuestScript script)
    {
        if (_hoverTestBusy) return;
        if (_questRun is { Running: true }) { ShowToast("Stop the run first"); return; }
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero)
        {
            QstStatus.Text = "EverQuest window not found — launch the game, then try again.";
            QstStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        _hoverTestBusy = true;
        try
        {
            for (int i = 3; i >= 1; i--)
            {
                QstStatus.Text = $"Hover test in {i}… bring EverQuest on screen (no clicks will be sent).";
                QstStatus.Foreground = Hex("#9FE0FF");
                await Task.Delay(1000);
            }

            // The SAME lookups the run uses — a test that exercises different code proves nothing.
            var points = new List<(string Label, ScreenPoint P)>();
            foreach (TurnInStep st in script.Steps)
            {
                string label = $"bag slot: {(st.Item.Length > 0 ? st.Item : "item")}";
                ScreenPoint p = st.Slot;
                if (script.SmartFind && script.BagSet && st.HasIcon)
                {
                    QuestFind.IconHit? hit = QuestFind.FindIconCell(_grindTarget, script, st);
                    if (hit is not null && hit.Dist <= QuestFind.IconAcceptDistance)
                    {
                        p = new ScreenPoint { X = hit.X, Y = hit.Y };
                        label += $" — FOUND in cell {hit.Row + 1},{hit.Col + 1} (match {hit.Dist:0})";
                    }
                    else
                    {
                        label += hit is null
                            ? " — bag scan failed, showing the fixed pick"
                            : $" — NO icon match (closest {hit.Dist:0}), showing the fixed pick";
                    }
                }
                points.Add((label, p));
            }
            {
                string label = "the NPC";
                ScreenPoint p = script.Layout.Npc;
                if (script.SmartFind && script.NpcAnchorLearned)
                {
                    QuestFind.NpcHit? found = await QuestFind.FindNpcAsync(_grindTarget, script);
                    if (found is not null)
                    {
                        p = new ScreenPoint { X = found.X, Y = found.Y };
                        label += $" — FOUND by nameplate \"{found.Matched}\"";
                    }
                    else label += " — nameplate not readable, showing the fixed pick";
                }
                points.Add((label, p));
            }
            points.Add(("the GIVE button", script.Layout.GiveButton));
            if (script.Layout.Confirm.Set) points.Add(("the confirm button", script.Layout.Confirm));

            (int hx, int hy) = HumanizedMouse.CursorPos();
            foreach ((string label, ScreenPoint p) in points)
            {
                if (!p.Set)
                {
                    QstStatus.Text = $"{label}: not picked — skipped.";
                    await Task.Delay(800);
                    continue;
                }
                if (!GetWindowRect(_grindTarget, out KMRECT r))
                {
                    QstStatus.Text = "The game window has gone away.";
                    QstStatus.Foreground = Hex("#FFCB6B");
                    return;
                }
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                int x = r.Left + (int)(p.X * w), y = r.Top + (int)(p.Y * h);
                QstStatus.Text = $"hovering {label} → screen ({x}, {y})";
                GrindLogLine($"[quest] hover test: {label} at {p.X * 100:0.0}%, {p.Y * 100:0.0}% → screen ({x}, {y})");
                HumanizedMouse.MoveInstant(x, y);
                await Task.Delay(1000);
            }
            HumanizedMouse.MoveInstant(hx, hy);
            QstStatus.Text = "Hover test done. Every stop should have sat exactly on its target — if one was off, "
                           + "re-pick that point. If they were all right, run again and read the [quest] lines in the Grind log.";
            QstStatus.Foreground = Hex("#7CE38B");
        }
        finally { _hoverTestBusy = false; }
    }


    /// <summary>Show the shared region picker over a live frame of the game and store the CENTRE
    /// of whatever box was dragged, normalized to the window.</summary>
    /// <param name="learn">Given the pick frame and the dragged normalized box, BEFORE the frame
    /// is disposed — this is where a slot pick learns its icon signature and the NPC pick learns
    /// its nameplate anchor. Learning happens off the frame the user drew on, never a fresh grab
    /// (the 0.9.37 lesson: the modal covers the game).</param>
    private bool PickQuestPoint(ScreenPoint point, string what, string hint,
                                Action<System.Drawing.Bitmap, (double X, double Y, double W, double H)>? learn = null)
    {
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        // Disposed here, not by the picker: CaptureFrame allocates a full-window 32bpp bitmap
        // (~15 MB at 1440p) and the picker only reads its size. Four picks a quest adds up fast.
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        {
            QstStatus.Text = "No game window to capture — launch EQL and keep it on screen, then try again.";
            QstStatus.Foreground = Hex("#FFCB6B");
            return false;
        }
        var dlg = new CompassPickWindow(frame, "Pick " + what, hint + "  (drag a small box — she clicks its centre)")
        { Owner = this };
        if (dlg.ShowDialog() != true) return false;

        point.X = dlg.NX + dlg.NW / 2;
        point.Y = dlg.NY + dlg.NH / 2;
        QstStatus.Text = $"Saved {what} at {point.X * 100:0.#}% across, {point.Y * 100:0.#}% down the game window.";
        QstStatus.Foreground = Hex("#7CE38B");
        try { learn?.Invoke(frame, (dlg.NX, dlg.NY, dlg.NW, dlg.NH)); }
        catch { /* learning is a bonus; the pick itself already saved */ }
        return true;
    }

    /// <summary>Pick a normalized RECT (the bag area) rather than a point.</summary>
    private bool PickQuestRect(Action<(double X, double Y, double W, double H)> store, string what, string hint)
    {
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        {
            QstStatus.Text = "No game window to capture — launch EQL and keep it on screen, then try again.";
            QstStatus.Foreground = Hex("#FFCB6B");
            return false;
        }
        var dlg = new CompassPickWindow(frame, "Pick " + what, hint) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        store((dlg.NX, dlg.NY, dlg.NW, dlg.NH));
        QstStatus.Text = $"Saved {what}.";
        QstStatus.Foreground = Hex("#7CE38B");
        return true;
    }

    /// <summary>
    /// Learn the NPC's nameplate anchor from the pick frame: OCR the frame, find the name nearest
    /// ABOVE the picked body point, store the nameplate position and the nameplate→body vector.
    /// Async because Windows OCR is; the pick itself has already saved by the time this runs.
    /// </summary>
    private async void LearnNpcAnchorAsync(QuestScript script, System.Drawing.Bitmap frameClone,
                                           double bodyX, double bodyY)
    {
        try
        {
            string key = QuestFind.NameKey(script.Npc);
            if (key.Length == 0)
            {
                script.NpcAnchorLearned = false;
                QuestScriptStore.Current.Adopt(script);
                QstStatus.Text = "NPC name too short to anchor — using the fixed spot.";
                return;
            }
            double fw = frameClone.Width, fh = frameClone.Height;
            List<FoundText> found = await ScreenText.ReadBitmapAsync(frameClone);

            (double X, double Y)? best = null;
            double bestScore = double.MaxValue;
            foreach (FoundText f in found)
            {
                string t = new(f.Text.Where(char.IsLetter).ToArray());
                if (!t.ToLowerInvariant().Contains(key)) continue;
                double nx = f.X / fw, ny = f.Y / fh;
                if (ny > 0.72 || ny > bodyY) continue;         // the nameplate floats ABOVE the body
                double dx = nx - bodyX, dy = ny - bodyY;
                double score = dx * dx + dy * dy;
                if (score < bestScore) { bestScore = score; best = (nx, ny); }
            }
            if (best is null)
            {
                // Clear any PREVIOUS anchor: it was learned for a different body pick, and keeping
                // it live while telling the user "the fixed spot will be used" would be a lie.
                script.NpcAnchorLearned = false;
                QuestScriptStore.Current.Adopt(script);
                QstStatus.Text = $"Couldn't read \"{script.Npc}\"'s nameplate in the pick frame — she'll use the "
                               + "fixed spot. (Target the NPC so the big name is on screen, then re-pick.)";
                QstStatus.Foreground = Hex("#FFCB6B");
                return;
            }
            script.NpcNameX = best.Value.X;
            script.NpcNameY = best.Value.Y;
            script.NpcDx = bodyX - best.Value.X;
            script.NpcDy = bodyY - best.Value.Y;
            script.NpcAnchorLearned = true;
            QuestScriptStore.Current.Adopt(script);
            QstStatus.Text = $"Nameplate anchor learned — she'll find {script.Npc} by the name over his head and "
                           + "click the body below it, wherever he stands.";
            QstStatus.Foreground = Hex("#7CE38B");
        }
        catch { /* anchor is a bonus; the fixed point still works */ }
        finally { frameClone.Dispose(); }
    }

    private void StartQuestRun(QuestScript script)
    {
        if (_questRun is { Running: true }) { ShowToast("Already running — Stop first"); return; }
        if (_grind is { Running: true } || _hunt is { Running: true } || _mergeRun is { Running: true })
        { ShowToast("Something else is running — Stop (F12) first"); return; }

        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero)
        {
            QstStatus.Text = "EverQuest window not found — launch the game, then try again.";
            QstStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        if (!script.Ready)
        {
            QstStatus.Text = "Still need a pick for: " + script.Missing() + ".";
            QstStatus.Foreground = Hex("#FFCB6B");
            return;
        }

        _currentLog ??= EQAvatar.Spike.Log.EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        var sink = new ForegroundSendInputSink(() => _grindTarget);
        _questRun = new QuestRole(script, sink, _settings, () => _grindTarget, _currentLog);
        _questRunFor = script.Quest;
        _questRun.Log += m => Dispatcher.Invoke(() =>
        {
            QstStatus.Text = m;
            QstStatus.Foreground = m.StartsWith("✖") ? Hex("#FFCB6B") : Hex("#7CE38B");
            GrindLogLine("[quest] " + m);
            // The runner speaks exactly when the picture changes — a hand-in confirmed, a miss, a
            // cycle done — so this is what keeps the fire bar and the ×count column live mid-run.
            RenderQuests();
        });
        _questRun.Stopped += () => Dispatcher.Invoke(RenderQuests);
        _questRun.Start();
        RenderQuests();
    }

    // ---------------------------------------------------------------- the ⓘ guide

    private void QstInfo_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hex("#C6D2DE"),
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(18),
            Text = QuestingInfoText,
        };
        var win = new Window
        {
            Title = "How Questing works",
            Owner = this,
            Width = 660, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string QuestingInfoText =
        "WHERE THE DATA COMES FROM\n" +
        "Every row is a page on eqlwiki.com. The wiki is a MediaWiki, so its pages are read through its own API and " +
        "parsed into rows on the hub — once, for everyone — and this app downloads the result as a single file and " +
        "caches it. That means the page works offline, filtering is instant, and the START ZONE and END ZONE dropdowns " +
        "list the zones that are really in the data rather than a hand-typed list that drifts out of step with it.\n\n" +
        "The stamp beside the title is when that catalog was BUILT from the wiki, not when you last opened this page. " +
        "⟳ Refresh asks the hub whether there is a newer build and only downloads when there is.\n\n" +
        "THE COLUMNS\n" +
        "QUEST is the wiki's own page title. START ZONE / START NPC are who hands it out and where. END NPC is who you " +
        "finish with — usually the same person, and when the zone differs it's printed underneath. LEVEL is the minimum " +
        "the wiki lists. REWARD is what it pays. AUTO says whether the quest ends in a hand-in she can repeat, and " +
        "whether you have built that automation yet.\n\n" +
        "Every column filters on its own and they combine — 'quests in Kerra Island, level 1–9, that I can automate' is " +
        "three controls. Click any row for the walkthrough details: coordinates, faction, related NPCs and a link " +
        "straight to the page.\n\n" +
        "WHAT SHE CAN AND CANNOT AUTOMATE\n" +
        "A quest is mostly travel, dialogue and killing. Killing is what the Grind page already does and travel is what " +
        "the Maps waypoints do. What is left — and what is genuinely tedious when a quest is farmed — is the HAND-IN: " +
        "target, hail, pick the item up, drop it on the NPC, press GIVE, repeat. That is a fixed gesture, so that is what " +
        "this automates. Quests with no recorded hand-in show a dash in the AUTO column.\n\n" +
        "WHY YOU HAVE TO SHOW HER THREE SPOTS\n" +
        "There is nothing to discover. EQL has no addon API; the log says nothing about your inventory or about what is " +
        "drawn on the screen; and an inventory slot's picture changes the moment the item leaves it, so recognising the " +
        "slot by sight would work once and fail forever after. A POSITION doesn't change, so a position is what gets " +
        "stored — as a fraction of the game window, so moving or resizing the window doesn't break it.\n\n" +
        "HOW SHE KNOWS IT WORKED\n" +
        "She does not trust her own clicks. After each hand-in she waits for the server to say so in the log — " +
        "'You offered …', 'has been updated', 'You have been given:', a faction adjustment, an experience line. A loop " +
        "that clicks perfectly and confirms nothing is a FAILED loop. Two failures in a row stop the run, because the " +
        "overwhelmingly likely cause is that you are out of the item, the next likeliest is that one of the three picks " +
        "is wrong, and neither gets better by carrying on clicking.\n\n" +
        "SAFETY\n" +
        "Foreground only, like every other role: she only sends input while EQ Legends is the focused window, so tabbing " +
        "away pauses her mid-run and F12 stops her. Nothing here is written to the game until you press Run.";
}
