using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
        _questPassKeys.Clear();
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
                                                          script.Layout.Npc.X, script.Layout.Npc.Y),
                        sh => { if (sh is null) script.Shots.Remove("npc"); else script.Shots["npc"] = sh; }))
                    Persist(); RenderQuests(); },
            script.Layout.Npc.Set ? () => ShowShot(script, "npc", "The NPC pick",
                script.NpcAnchorLearned
                    ? "The body point you clicked. At run time she reads the nameplate and clicks this far below it."
                    : "The body point you clicked. No nameplate anchor was learned — target him and re-pick to get one.") : null));
        picks.Children.Add(MakePickTile("ui-pick-give.jpg", "GIVE button", "commits the trade", "",
            script.Layout.GiveButton.Set,
            "The button that completes the hand-in. The give window opens in the same place every time, so one pick covers every item. Click to pick.",
            () => { if (PickQuestPoint(script.Layout.GiveButton, "the GIVE button",
                        "Open a give window with the NPC, then click ON its GIVE button and press Enter.",
                        null, sh => { if (sh is null) script.Shots.Remove("give"); else script.Shots["give"] = sh; })) Persist(); RenderQuests(); },
            script.Layout.GiveButton.Set ? () => ShowShot(script, "give", "The GIVE button pick",
                "She clicks the centre of this box every hand-in. If the give window has moved since, re-pick it.") : null));
        picks.Children.Add(MakePickTile("ui-pick-confirm.jpg", "confirm", "optional dialog", "",
            script.Layout.Confirm.Set,
            "Only needed if the server puts a second dialog up after GIVE. The cycle runs without it. Click to pick.",
            () => { if (PickQuestPoint(script.Layout.Confirm, "the confirm button",
                        "If a confirmation appears after GIVE, click ON its button and press Enter.",
                        null, sh => { if (sh is null) script.Shots.Remove("confirm"); else script.Shots["confirm"] = sh; })) Persist(); RenderQuests(); },
            script.Layout.Confirm.Set ? () => ShowShot(script, "confirm", "The confirm pick", "") : null));
        picks.Children.Add(MakePickTile("ui-pick-bag.jpg", "the bag area",
            "every slot scanned at your icon's size", "",
            script.BagSet,
            "Drag ONE box around ALL your open bags — every slot inside it gets scanned. At run time she slides a "
            + "window the size of each item's learned icon across this area and clicks the copy that's actually "
            + "there; the picked slots become fallbacks. Click to pick.",
            () => { if (PickQuestRect(r => { script.BagX = r.X; script.BagY = r.Y; script.BagW = r.W; script.BagH = r.H; },
                        "the bag area",
                        "Drag a box around the WHOLE block of bag slots holding the quest items — corner to corner — then press Enter.",
                        sh => { if (sh is null) script.Shots.Remove("bag"); else script.Shots["bag"] = sh; }))
                    Persist(); RenderQuests(); },
            script.BagSet ? () => ShowShot(script, "bag", "The bag area",
                "Everything inside the orange box gets scanned for your items. If your bags have moved or you have "
                + "opened different ones since, re-pick it.") : null));
        stack.Children.Add(picks);

        // ---- what she says after the hail: the dialogue triggers ----
        var sayBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        sayBar.Children.Add(new TextBlock
        {
            Text = "say after hail", Foreground = Hex("#9FB6CC"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "The bracketed words the NPC asks you to say back. THIS is what puts the task in your "
                    + "journal — not the hail. You can walk up with no prior interaction, say these words, and "
                    + "hand the items straight over; the hail only exists to make him tell you the words in the "
                    + "first place. Separate several with ;",
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
                Text = script.HailFirst ? "puts the task in the journal"
                                        : "this alone assigns the task — no hail, no target needed",
                Foreground = Hex("#7CE38B"), FontSize = 10,
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

            // The subtitle is where a step tells the truth about HOW it will be found. "icon learned"
            // was true of a pre-0.10.9 pick too, and that hid the one fact that mattered: it was
            // still using the loose grid scan that matched a totem to gauntlets at 24.
            string howFound = !step.HasIcon ? shortQuest
                            : step.HasIconSize ? "icon learned — found live"
                            : "⚠ old scan — re-pick me";
            var tileHost = new Grid();
            tileHost.Children.Add(MakePickTile("ui-pick-slot.jpg",
                step.Item.Length > 0 ? step.Item : "hand-in item",
                howFound,
                $"{i + 1} of {total}",
                step.Slot.Set,
                "Drag a TIGHT box around one copy of the item in your bag. The pick learns the item's ICON, so at "
                + "run time she scans the bag area for wherever a copy actually is — the picked slot is only the "
                + "fallback. Click to pick. Click the READY badge to see what she's comparing against.",
                () => { if (PickQuestPoint(captured.Slot, "where " + captured.Item + " sits in your bags",
                            $"Drag a TIGHT box around one {captured.Item} in your inventory, then press Enter.",
                            (frame, box) =>
                            {
                                captured.IconSig = QuestFind.SigFromRegion(frame, box.X, box.Y, box.W, box.H);
                                captured.IconW = box.W;              // the box's own size drives the sliding
                                captured.IconH = box.H;              // search — no columns, no rows, no questions
                                if (captured.IconSig is not null)
                                    QstStatus.Text = $"Saved — and learned {captured.Item}'s icon, so she'll find the "
                                                   + "next copy wherever it sits in the bag area.";
                            },
                            sh => captured.Shot = sh)) Persist(); RenderQuests(); },
                captured.Slot.Set ? () => ShowStepShot(captured) : null));

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
        // EDITABLE on purpose. The catalog is scraped from a wiki written by people, and when it
        // has a gap the list is empty and the cycle simply cannot be built — which is exactly what
        // happened to the Kerra pair: 'This Means Warrr' kept its hand-in under a sub-heading the
        // scraper stopped short of, so the one item that completes the loop was un-addable. A
        // suggestion list must never be the only way in; type the item's name and it goes in.
        var addBox = new ComboBox
        {
            Width = 320, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), IsEditable = true,
            ToolTip = "Other hand-ins the catalog knows about for this NPC — or just type the item's name "
                    + "exactly as it appears in game and press ＋.",
        };
        var candidates = new List<(string Item, int Qty, string Quest)>();
        string npcKey = QuestCatalog.Norm(script.Npc);
        // Only when we actually know who the NPC is: Norm("") == Norm("") would otherwise match
        // every blank-NPC hand-in in the catalog and offer all 884 quests as plausible follow-ons.
        if (npcKey.Length > 0)
            foreach (QuestInfo other in QuestCatalog.Quests)
            {
                bool sameNpc = QuestCatalog.Norm(other.StartNpc) == npcKey || QuestCatalog.Norm(other.EndNpc) == npcKey;
                foreach (QuestTurnIn t in other.TurnIns)
                    if (QuestCatalog.Norm(t.Npc) == npcKey)
                        candidates.Add((t.Item, Math.Max(1, t.Qty), other.Name));
                // Quests whose turn-in line the wiki never spelled out still list what they need.
                // Offering those keeps a thin catalog entry from blocking the build.
                if (sameNpc)
                    foreach (string need in other.ItemsNeeded)
                        candidates.Add((need, 1, other.Name));
            }
        // Drop anything already in the cycle, and any duplicate suggestion.
        var seenCand = new HashSet<string>();
        candidates = candidates
            .Where(c => c.Item.Trim().Length > 1
                     && !script.Steps.Any(s => QuestCatalog.Norm(s.Item) == QuestCatalog.Norm(c.Item))
                     && seenCand.Add(QuestCatalog.Norm(c.Item) + "|" + QuestCatalog.Norm(c.Quest)))
            .ToList();
        foreach ((string item, int _q, string quest) in candidates)
            addBox.Items.Add($"{item}  ·  {quest}");

        var addBtn = new Button
        {
            Content = "＋ add to the cycle", Padding = new Thickness(12, 3, 12, 3),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Append a hand-in after the ones above. Use this when finishing one quest immediately "
                    + "opens the next — the pair is what you actually repeat. Picked from the list or typed "
                    + "by hand, both work.",
        };
        addBtn.Click += (_, _) =>
        {
            // The typed text wins over a stale selection: an editable ComboBox can keep an old
            // SelectedIndex while the box shows something the user has since typed over.
            int i = addBox.SelectedIndex;
            string shown = (addBox.Text ?? "").Trim();
            bool fromList = i >= 0 && i < candidates.Count
                         && string.Equals(shown, addBox.Items[i] as string, StringComparison.OrdinalIgnoreCase);
            string item; int qty; string quest; bool known = true;
            if (fromList)
                (item, qty, quest) = candidates[i];
            else
            {
                // Typed by hand. Strip the "item · quest" shape in case they edited a suggestion,
                // and credit it to the quest the wiki lists that item under when it knows one, so
                // the COMPLETED column still counts the right row.
                int dot = shown.IndexOf('·');
                item = (dot > 0 ? shown[..dot] : shown).Trim();
                if (item.Length < 2)
                {
                    QstStatus.Text = "Pick a hand-in from the list, or type the item's name.";
                    QstStatus.Foreground = Hex("#FFCB6B");
                    return;
                }
                qty = 1;
                string? match = QuestCatalog.Quests.FirstOrDefault(q =>
                            q.TurnIns.Any(t => QuestCatalog.Norm(t.Item) == QuestCatalog.Norm(item))
                         || q.ItemsNeeded.Any(n => QuestCatalog.Norm(n) == QuestCatalog.Norm(item)))?.Name;
                known = match is not null;
                quest = match ?? script.Quest;
            }
            if (script.Steps.Any(s => QuestCatalog.Norm(s.Item) == QuestCatalog.Norm(item)))
            {
                QstStatus.Text = $"{item} is already in the cycle.";
                QstStatus.Foreground = Hex("#FFCB6B");
                return;
            }

            QuestScriptStore.Current.Edit(() =>
                script.Steps.Add(new TurnInStep { Item = item, Qty = Math.Max(1, qty), Quest = quest }));
            Persist();
            RenderQuests();
            // The name is not how she FINDS the item (that's the icon) — it is how she RECOGNISES
            // the server's "You offered 1 <item> to …" line. A name the catalog has never seen is
            // usually a typo or a wiki page title, and the cost is silent: items get handed over
            // and none of them count, which reads in the log as "you've run out".
            QstStatus.Text = known
                ? $"Added {item} — now pick its slot: drag a TIGHT box around one in your bag."
                : $"Added {item} — heads up, the catalog doesn't know that name. It has to match what the game "
                  + "prints in \"You offered 1 … \" EXACTLY, or the hand-ins won't be counted. Now pick its slot.";
            QstStatus.Foreground = Hex(known ? "#9FE0B8" : "#FFCB6B");
        };
        addBar.Children.Add(addBox);
        addBar.Children.Add(addBtn);
        stack.Children.Add(addBar);

        // ---- the switches ----
        // A WrapPanel, not a StackPanel. This row lives inside a ScrollViewer with horizontal
        // scrolling disabled, so a horizontal StackPanel that outgrows the window CLIPS its tail
        // with no way to reach it — and the tail is where "repeat" lives, the only control that
        // bounds a run. Every child gets a bottom margin so wrapped lines don't collide.
        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        // A label and the box it annotates must wrap as ONE unit, or "confirm wait" ends a line
        // and its field starts the next. Pairs go in their own little StackPanel.
        void OptPair(params UIElement[] parts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (UIElement part in parts) row.Children.Add(part);
            Opt(row);
        }
        void Opt(UIElement e)
        {
            if (e is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right,
                                          Math.Max(fe.Margin.Bottom, 6));
            opts.Children.Add(e);
        }
        var hail = new CheckBox
        {
            Content = "hail first", IsChecked = script.HailFirst, Foreground = Hex("#9FB6CC"), FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Press the in-game hail key at the top of each cycle. OFF by default now: the SAY-PHRASE is "
                    + "what assigns the task, not the hail — the hail is only how you learn the words. Leave it off "
                    + "unless an NPC genuinely needs waking up; it costs about two seconds a cycle.",
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
            ToolTip = "Type /target <NPC> first. OFF by default now: a say-phrase is spoken aloud to everyone in "
                    + "range and needs no target. The trade-off is that his nameplate is only drawn when he is "
                    + "TARGETED, so with this off the fixed NPC pick does all the work — turn it on if you move "
                    + "around between runs.",
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

        // (No bag columns × rows here anymore. The sliding search takes its window size from the
        // TIGHT box dragged around the item itself, so every slot in the bag area gets scanned
        // without the user ever counting slots. BagCols/BagRows live on only as a fallback grid
        // for steps picked before icon sizes were stored.)
        Opt(smart);
        OptPair(hail, hailKey);
        Opt(targ);

        var bagsLbl = new TextBlock
        {
            Text = "open bags", Foreground = Hex("#9FB6CC"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "The key you've bound in game to OPEN ALL BAGS (chords welcome: alt+b). She presses it at the "
                    + "start of the run, at the top of every cycle, and again if an item scan comes up empty — a "
                    + "shut bag and an empty bag look the same to her otherwise. Bind the OPEN command, not the "
                    + "show/hide toggle. Leave blank to never press anything.",
        };
        var bagsKey = new TextBox
        {
            Text = script.OpenBagsKey ?? "", Width = 62, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "e.g. alt+b — blank means never pressed.",
        };
        bagsKey.LostFocus += (_, _) =>
        {
            string want = bagsKey.Text.Trim();
            if (want.Length > 0 && InputKey.ParseChord(want).Key.IsNone)
            {
                QstStatus.Text = $"\"{want}\" isn't a key I can press — try something like alt+b, ctrl+i or b.";
                QstStatus.Foreground = Hex("#FFCB6B");
                bagsKey.Text = script.OpenBagsKey ?? "";
                return;
            }
            script.OpenBagsKey = want;
            Persist();
            QstStatus.Text = want.Length > 0
                ? $"Open-bags key set to {want} — she'll press it at the start of a run, at the top of every cycle, and whenever a scan comes up empty."
                : "Open-bags key cleared — she won't press anything.";
            QstStatus.Foreground = Hex("#9FE0B8");
        };
        OptPair(bagsLbl, bagsKey);

        var focusBox = new CheckBox
        {
            Content = "focus the game on start", IsChecked = _settings.FocusGameOnStart,
            Foreground = Hex("#9FB6CC"), FontSize = 11, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Bring EverQuest to the front when you press Run, instead of you alt-tabbing to it while "
                    + "she waits. Applies to every runner, not just this one. Only ever at the START — tabbing "
                    + "away still pauses her, and she will never grab focus back off you mid-run.",
        };
        focusBox.Click += (_, _) => { _settings.FocusGameOnStart = focusBox.IsChecked == true; _settings.Save(); };
        Opt(focusBox);
        var confirmBox = new CheckBox
        {
            Content = "wait for the server to confirm", IsChecked = script.WaitForConfirm,
            Foreground = Hex("#9FB6CC"), FontSize = 11, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "ON: every hand-in waits for the log to acknowledge it, so the counts are real and the run can "
                    + "tell you when the items run out. OFF: hand over, wait a beat, move on — much faster, and the "
                    + "honest cost is that a run which stops working can't notice — so an assumed hand-in moves the "
                    + "run along but is kept OUT of the permanent completion history, and this needs a cycle count "
                    + "rather than \"until they run out\". A fast acknowledgement is still used either way.",
        };
        confirmBox.Click += (_, _) => { script.WaitForConfirm = confirmBox.IsChecked == true; Persist(); };
        Opt(confirmBox);

        var confirmLbl = new TextBlock { Text = "confirm wait", Foreground = Hex("#9FB6CC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var confirmSecs = new TextBox
        {
            Text = script.ConfirmSeconds.ToString(), Width = 44, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Seconds to wait for the server to acknowledge a hand-in before calling it a miss. The "
                    + "acknowledgement normally lands within a second, so this is a ceiling, not a schedule — but it "
                    + "is what a FAILED hand-in costs you, once per item.",
        };
        confirmSecs.LostFocus += (_, _) =>
        {
            script.ConfirmSeconds = int.TryParse(confirmSecs.Text.Trim(), out int cs) ? Math.Clamp(cs, 2, 60) : 6;
            confirmSecs.Text = script.ConfirmSeconds.ToString();
            Persist();
        };
        OptPair(confirmLbl, confirmSecs, new TextBlock
        {
            Text = "s", Foreground = Hex("#5E7C9A"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0),
        });

        var giveLbl = new TextBlock { Text = "give wait", Foreground = Hex("#9FB6CC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var giveWait = new TextBox
        {
            Text = script.GiveSettleMs.ToString(), Width = 56, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Milliseconds to wait after dropping the item on the NPC, before pressing GIVE — the gap the "
                    + "trade window has to appear in. Every failed hand-in so far has been a first offer that missed "
                    + "and a retry that worked, which is what a GIVE landing before the window is up looks like. "
                    + "Raise it if hand-ins still miss; lower it if your machine is quick and you want the seconds back.",
        };
        giveWait.LostFocus += (_, _) =>
        {
            script.GiveSettleMs = int.TryParse(giveWait.Text.Trim(), out int g) ? Math.Clamp(g, 200, 4000) : 1100;
            giveWait.Text = script.GiveSettleMs.ToString();
            Persist();
        };
        OptPair(giveLbl, giveWait, new TextBlock
        {
            Text = "ms before GIVE", Foreground = Hex("#5E7C9A"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 14, 0),
        });
        var repeatLbl = new TextBlock { Text = "repeat", Foreground = Hex("#9FB6CC"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
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
        OptPair(repeatLbl, repeat, new TextBlock
        {
            Text = "cycles · 0 = until they run out", Foreground = Hex("#5E7C9A"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
        stack.Children.Add(opts);

        // The quest's own "that worked" line. No scrape can know it — it is different for every
        // turn-in — but it is the fastest and least ambiguous confirmation there is, so there is a
        // box for it rather than a timeout standing in for one.
        var okRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        okRow.Children.Add(new TextBlock
        {
            Text = "also count as success", Foreground = Hex("#9FB6CC"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 6, 0),
        });
        var okLines = new TextBox
        {
            Text = string.Join(Environment.NewLine, script.SuccessLines ?? new List<string>()),
            Width = 470, MinHeight = 40, FontSize = 11, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "One phrase per line. Any log line containing one of these ends the wait and counts the "
                    + "hand-in. Paste the line your quest prints right after a successful turn-in — for the Kerra "
                    + "cycle that's \"You validated the Kerran Sha`rr's concerns\" and \"You've dealt a blow to the "
                    + "Heretics\". Part of the line is enough; six characters minimum.",
        };
        okLines.LostFocus += (_, _) =>
        {
            script.SuccessLines = okLines.Text
                .Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            okLines.Text = string.Join(Environment.NewLine, script.SuccessLines);
            Persist();
        };
        okRow.Children.Add(okLines);
        stack.Children.Add(okRow);
        stack.Children.Add(new TextBlock
        {
            Text = "one phrase per line — the log line your quest prints right after a successful turn-in",
            Foreground = Hex("#5E7C9A"), FontSize = 10, Margin = new Thickness(112, 2, 0, 0),
        });

        // ---- run / stop ----
        // "Starting" counts as running for the button: while the game is being brought to the
        // front there is no role object yet, and a button still offering "Run" is an invitation to
        // start a second one.
        bool running = (_questStarting || _questRun is { Running: true })
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
        run.Click += async (_, _) =>
        {
            if (_questRun is { Running: true }) { _questRun.Stop(); return; }
            if (_questStarting) { _questStartCancelled = true; return; }   // stop a start that's mid-flight
            Persist();
            await StartQuestRunAsync(script);
        };
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
            // NOT gated on `running` — that now includes the start window, where the role object
            // does not exist yet and reading its stats would be a null dereference on the very
            // first press of Run in a session.
            Text = _questRun is { Running: true } live
                        && QuestCatalog.Norm(_questRunFor) == QuestCatalog.Norm(script.Quest)
                ? $"{live.Stats.State} · {live.Stats.Cycles} cycle(s), {live.Stats.HandIns} hand-in(s)"
                : running ? "starting…"
                : script.LifetimeCompleted > 0 ? $"{script.LifetimeCompleted} cycle(s) all time" : "",
        };
        bar.Children.Add(stats);
        stack.Children.Add(bar);

        // ---- the live console: what she is doing NOW, over the last few things she did ----
        stack.Children.Add(MakeQuestConsole(running, script.Quest));

        // The card itself reports the run. Green, glowing and faintly lit from within while she
        // works — from across the room you can tell whether the bot is alive without reading a
        // word, which is the whole point of watching an automation you can't see the inside of.
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = running ? Hex("#0E2318") : Hex("#121A28"),
            BorderBrush = running ? Hex("#49F27E") : Hex("#2A4A57"),
            BorderThickness = new Thickness(running ? 2 : 1),
            Padding = new Thickness(12, 4, 12, 12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = stack,
        };
        if (running)
        {
            var glow = new DropShadowEffect
            { Color = Color.FromRgb(0x49, 0xF2, 0x7E), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 };
            // A slow breath rather than a fixed halo: a still glow reads as decoration, a moving
            // one reads as a heartbeat — and a heartbeat that stops is information.
            var breathe = new DoubleAnimation(0.30, 0.75, TimeSpan.FromSeconds(1.6))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, breathe);
            card.Effect = glow;
        }
        return card;
    }

    /// <summary>
    /// The Questing card's own console: one big line for what she is doing THIS SECOND, and the
    /// steps behind it, scrollable back through the whole run.
    ///
    /// Separate from the Grind console on purpose. Everything used to pour into that one box, so a
    /// quest run's narration was buried in someone else's log — and the Grind log was ruined by
    /// quest lines it had no use for. Both consoles now read one source each; the Activity Console
    /// under TOOLS is where you go to see them interleaved.
    /// </summary>
    /// <summary>
    /// One console per quest, kept for the window's life.
    ///
    /// The card around it is rebuilt from scratch every time anything changes, which is fine for a
    /// fire bar and fatal for a console: a console you have to re-find your place in after every
    /// line is a console that only works when nothing is happening. So the console outlives its
    /// card and gets re-parented into each new one.
    ///
    /// Keyed by the NORMALIZED quest name, the same key its lines are tagged with — two spellings
    /// of one quest would otherwise get two consoles, each holding half the story.
    /// </summary>
    private readonly Dictionary<string, EQAvatar.Spike.Ui.ModuleConsole> _questConsoles = new();
    /// <summary>Console keys already placed in the render pass currently running.</summary>
    private readonly HashSet<string> _questPassKeys = new();

    private FrameworkElement MakeQuestConsole(bool running, string questName)
    {
        string key = QuestCatalog.Norm(questName);
        // If the cached console is still sitting in a card built EARLIER IN THIS SAME PASS, two
        // catalog entries normalize to one name. Handing the same control to the second card would
        // pull it out of the first, leaving that card silently console-less. Give the second one its
        // own instead — they show the same lines, which is honest, since the tags normalize too.
        // The suffix counts occurrences WITHIN THIS PASS, so the same card gets the same console
        // every pass. Numbering off the dictionary's size instead made a fresh console on every
        // render — one every 1.2 seconds during a run, each kept forever, each fed every log line.
        string baseKey = key;
        for (int n = 1; _questPassKeys.Contains(key); n++) key = baseKey + "#" + n;
        _questPassKeys.Add(key);

        if (!_questConsoles.TryGetValue(key, out EQAvatar.Spike.Ui.ModuleConsole? console))
        {
            console = new EQAvatar.Spike.Ui.ModuleConsole(
                QuestSource, questName, (a, b) => QuestCatalog.Norm(a) == QuestCatalog.Norm(b),
                "LIVE ACTIVITY", "nothing yet — press Run and she'll narrate every step here.",
                () => NavActivity.IsChecked = true, ShowToast,
                MakeResizableConsole,
                () => _settings.ConsoleDetail,
                d => { _settings.ConsoleDetail = d; _settings.Save(); SyncConsoleChrome(); });
            _questConsoles[key] = console;
        }
        console.Detach();
        console.SetRunning(running);
        return console;
    }

    /// <summary>
    /// A full page redraw, at most one every <see cref="QuestRenderGapMs"/> and never more than one
    /// queued.
    ///
    /// The LAST word matters here: a trailing timer, not a leading one. Dropping the render that
    /// follows the final line of a run would leave the fire bar and the ×count column frozen one
    /// step short of the truth for as long as nobody clicked anything — and the last line of a run
    /// is the one people read.
    /// </summary>
    private const int QuestRenderGapMs = 1200;
    private System.Windows.Threading.DispatcherTimer? _questRenderTimer;

    private void QueueQuestRender()
    {
        if (_questRenderTimer is null)
        {
            _questRenderTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(QuestRenderGapMs) };
            _questRenderTimer.Tick += (_, _) =>
            {
                // A redraw re-parents the console, and WPF drops keyboard focus on removal without
                // giving it back. Redrawing while someone is typing in the find box or dragging out
                // a selection takes the thing they are using away mid-gesture — so wait. The timer
                // keeps running, so the moment they click off, the card catches up.
                if (_questConsoles.Values.Any(c => c.IsUserBusy)) return;
                _questRenderTimer!.Stop();
                RenderQuests();
            };
        }
        if (!_questRenderTimer.IsEnabled) _questRenderTimer.Start();
    }

    /// <summary>Every console shows the same two switches, so flipping one repaints them all.
    /// A page that disagreed with the next about whether detail was on would be lying about what
    /// the log is going to contain.</summary>
    private void SyncConsoleChrome()
    {
        _mrgConsole?.RefreshChrome();
        foreach (EQAvatar.Spike.Ui.ModuleConsole c in _questConsoles.Values) c.RefreshChrome();
    }

    // ---------------------------------------------------------------- picking + running

    /// <summary>Everything the card draws from the runner, as one string. When it hasn't moved,
    /// the card doesn't need redrawing — and the console keeps itself up to date either way.</summary>
    private string _qcStatSig = "\u0000";

    /// <summary>The name this page records its activity under. One string, so the console that
    /// reads it and the runner that writes it can never drift apart.</summary>
    internal const string QuestSource = "Quest";

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
        if (_questStarting || _questRun is { Running: true }) { ShowToast("Stop the run first"); return; }
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
            // Same courtesy as a run: put the game on screen rather than asking the user to race a
            // countdown to it. The countdown stays (shortened) even when focus succeeds — the whole
            // point of this test is that you WATCH it, so you get a moment to look at the game.
            bool front = _settings.FocusGameOnStart && await GameFocus.BringAndSettleAsync(_grindTarget, settleMs: 400);
            for (int i = front ? 2 : 3; i >= 1; i--)
            {
                QstStatus.Text = front
                    ? $"Hover test in {i}… watch the cursor (no clicks will be sent)."
                    : $"Hover test in {i}… bring EverQuest on screen (no clicks will be sent).";
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
                    bool sliding = st.HasIconSize;
                    QuestFind.IconHit? hit = sliding
                        ? QuestFind.FindIconSliding(_grindTarget, script, st)
                        : QuestFind.FindIconCell(_grindTarget, script, st);
                    double accept = sliding ? QuestFind.SlidingAcceptDistance : QuestFind.IconAcceptDistance;
                    if (hit is not null && hit.Dist <= accept)
                    {
                        p = new ScreenPoint { X = hit.X, Y = hit.Y };
                        label += sliding
                            ? $" — FOUND (match {hit.Dist:0})"
                            : $" — FOUND in cell {hit.Row + 1},{hit.Col + 1} (match {hit.Dist:0})";
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
                ActivityLog.Record(QuestSource,
                    $"· hover test: {label} at {p.X * 100:0.0}%, {p.Y * 100:0.0}% → screen ({x}, {y})", script.Quest);
                HumanizedMouse.MoveInstant(x, y);
                await Task.Delay(1000);
            }
            HumanizedMouse.MoveInstant(hx, hy);
            QstStatus.Text = "Hover test done. Every stop should have sat exactly on its target — if one was off, "
                           + "re-pick that point. Every stop is written to this card's LIVE ACTIVITY box below.";
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
                                Action<System.Drawing.Bitmap, (double X, double Y, double W, double H)>? learn = null,
                                Action<PickShot?>? shot = null)
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
        try { shot?.Invoke(PickShot.From(frame, dlg.NX, dlg.NY, dlg.NW, dlg.NH)); }
        catch { /* a missing picture must never cost the pick */ }
        try { learn?.Invoke(frame, (dlg.NX, dlg.NY, dlg.NW, dlg.NH)); }
        catch { /* learning is a bonus; the pick itself already saved */ }
        return true;
    }

    // ---------------------------------------------------------------- "show me what you learned"

    /// <summary>The picture behind one of the shared picks (NPC / GIVE / confirm / bag).</summary>
    private void ShowShot(QuestScript script, string key, string title, string note)
    {
        script.Shots.TryGetValue(key, out PickShot? shot);
        new PickShotWindow(title, $"{script.Quest} · {script.Npc}", shot,
            shot is null
                ? "No snapshot for this pick — either it predates snapshots, or the capture failed at pick time. "
                + "Re-pick it once and this window will show you exactly what she uses."
                : (note.Length > 0 ? note : null))
        { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// The picture behind an ITEM pick — the one that actually decides whether she grabs the right
    /// thing. It also states WHICH search that step will use, because a signature learned before
    /// 0.10.9 has no box size and silently falls back to the loose grid scan: the run then reports
    /// "found ... in bag cell 2,1 (match 27)" and clicks an empty square with total confidence.
    /// </summary>
    private void ShowStepShot(TurnInStep step)
    {
        string note = !step.HasIcon
            ? "No icon signature on this step — she will click the fixed slot and hope. Re-pick it."
            : step.HasIconSize
                ? "She slides a window of exactly this size across the bag area and clicks the closest match "
                + "(needs a score of " + QuestFind.SlidingAcceptDistance.ToString("0") + " or better)."
                : "⚠ This pick predates the precise search: with no box size stored she falls back to the OLD grid "
                + "scan, which divides your bag area into cells and compares the middle of each one. That is how a "
                + "totem got matched to gauntlets — and how an empty slot can score 27. Re-pick this item once.";
        new PickShotWindow(
            step.Item.Length > 0 ? step.Item : "hand-in item",
            step.HasIconSize ? "precise sliding search" : "old grid scan",
            step.Shot,
            step.Shot is null
                ? "No snapshot for this pick — either it predates snapshots, or the capture failed at pick time. "
                + "Re-pick it once and this window will show you the exact pixels she compares against.\n\n" + note
                : note)
        { Owner = this }.ShowDialog();
    }

    /// <summary>Pick a normalized RECT (the bag area) rather than a point.</summary>
    private bool PickQuestRect(Action<(double X, double Y, double W, double H)> store, string what, string hint,
                               Action<PickShot?>? shot = null)
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
        // A bag area is already large; padding it further would just photograph the screen.
        try { shot?.Invoke(PickShot.From(frame, dlg.NX, dlg.NY, dlg.NW, dlg.NH, pad: 0.06)); }
        catch { /* a missing picture must never cost the pick */ }
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

    /// <summary>True from the moment Run is pressed until the runner is actually alive.
    ///
    /// Focusing the game is asynchronous, so there is now a window — up to a couple of seconds if
    /// the game is minimized — in which the run has been ORDERED but <c>_questRun</c> is still
    /// null. Every "is something running?" test in the app reads that field, so without this flag
    /// the window is a hole: a second Run click starts a SECOND runner that nothing holds a
    /// reference to (unstoppable, clicking against the first), F12 finds nothing to stop, and
    /// Ctrl+Alt+M can start a merge sweep that ends up sharing the cursor with the quest run.
    /// "Running" has to mean "ordered and not yet finished", not "the object exists".</summary>
    private bool _questStarting;
    /// <summary>Set when a stop arrives DURING that window — F12 must not be a no-op just because
    /// the thing it stops hasn't been constructed yet.</summary>
    private bool _questStartCancelled;

    /// <summary>A start that refuses has to say so where the user is looking — which, now the card
    /// has its own console, is the big NOW line as much as the status text. A refusal that only
    /// wrote to QstStatus left the console reading "nothing yet — press Run" after they had.</summary>
    private void QuestFail(QuestScript script, string why)
    {
        QstStatus.Text = why;
        QstStatus.Foreground = Hex("#FFCB6B");
        ActivityLog.Record(QuestSource, why, script.Quest);
        RenderQuests();
    }

    private async Task StartQuestRunAsync(QuestScript script)
    {
        if (_questStarting || _questRun is { Running: true }) { ShowToast("Already running — Stop first"); return; }
        if (_hoverTestBusy) { ShowToast("The hover test is running — let it finish"); return; }
        if (_grind is { Running: true } || _hunt is { Running: true } || _mergeRun is { Running: true })
        { ShowToast("Something else is running — Stop (F12) first"); return; }

        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero)
        {
            QuestFail(script, "✖ EverQuest window not found — launch the game, then try again.");
            return;
        }
        if (!script.Ready)
        {
            QuestFail(script, "✖ Still need a pick for: " + script.Missing() + ".");
            return;
        }

        _questStarting = true;
        _questStartCancelled = false;
        _questRunFor = script.Quest;        // set BEFORE the await so this card owns the start
        try
        {
            // Inside the try: a render that throws must not strand _questStarting true, which
            // would leave every role in the app refusing to start until a restart.
            RenderQuests();                 // the button reads Stop from here on — one click, one run

            // Hand control to the game. Pressing Run IS the intent to do that, and the runner
            // refuses to act while anything else is focused — so without this the first thing the
            // user sees is "Paused — EverQuest isn't the focused window" and a race to alt-tab.
            ActivityLog.Record(QuestSource, "· starting the cycle", script.Quest);
            if (_settings.FocusGameOnStart)
            {
                QstStatus.Text = "Bringing EverQuest to the front…";
                QstStatus.Foreground = Hex("#9FE0FF");
                ActivityLog.Record(QuestSource, "· bringing EverQuest to the front", script.Quest);
                if (!await GameFocus.BringAndSettleAsync(_grindTarget))
                {
                    QstStatus.Text = "Couldn't bring EverQuest to the front — click the game yourself and she'll pick up from there.";
                    QstStatus.Foreground = Hex("#FFCB6B");
                    ActivityLog.Record(QuestSource, "⚠ couldn't bring EverQuest to the front — click the game yourself.", script.Quest);
                }
            }

            // Stopped while we were bringing the game up (F12, or the Stop button). Honour it: the
            // whole value of a panic key is that it works at the moment you reach for it.
            if (_questStartCancelled)
            {
                QstStatus.Text = "Stopped before she started.";
                QstStatus.Foreground = Hex("#FFCB6B");
                ActivityLog.Record(QuestSource, "Stopped before she started.", script.Quest);
                return;
            }

            // RE-RESOLVE, never reuse. `??=` meant the first log found in a session was tailed
            // forever — but EQ writes a NEW file for a new character or a re-login, so after one
            // relog every hand-in clicked perfectly and confirmed nothing, and the run blamed an
            // empty bag for a file it was no longer reading.
            string? newest = EQAvatar.Spike.Log.EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
            if (newest is not null && !string.Equals(newest, _currentLog, StringComparison.OrdinalIgnoreCase))
            {
                _currentLog = newest;
                ActivityLog.Record(QuestSource, "· reading " + System.IO.Path.GetFileName(newest), script.Quest);
            }
            _currentLog ??= newest;
            var sink = new ForegroundSendInputSink(() => _grindTarget);
            // Held as a local as well as a field: the handlers below outlive this method, and a
            // later run replacing the field must not make an old line report the NEW run's numbers.
            var runner = new QuestRole(script, sink, _settings, () => _grindTarget, _currentLog);
            _questRun = runner;
            runner.Log += m => Dispatcher.Invoke(() =>
            {
                QstStatus.Text = m;
                QstStatus.Foreground = m.StartsWith("✖") || m.StartsWith("⚠") ? Hex("#FFCB6B") : Hex("#7CE38B");
                // Its OWN source, not the Grind console. Mixing them made both logs unreadable.
                // The console picks this up from ActivityLog.Added and appends ONE line; it is not
                // rebuilt from here, which is what lets it keep the reading position.
                ActivityLog.Record(QuestSource, m, script.Quest);
                // The runner speaks exactly when the picture changes — a hand-in confirmed, a miss,
                // a cycle done — so the fire bar and the ×count column stay live mid-run. But most
                // lines are just steps, and rebuilding the card for one of those is a whole page of
                // art redrawn on the UI thread while the runner's own thread waits inside
                // Dispatcher.Invoke. So: redraw only when the numbers the card shows have actually
                // moved, and coalesce even that. The console is not rebuilt from here at all — it
                // gets the line from ActivityLog.Added and appends it, which is what lets it keep
                // both the reading position and any selection the user is making in it.
                QuestStats st = runner.Stats;
                string sig = $"{st.State}|{st.Cycles}|{st.HandIns}|{st.Attempts}|{st.Misses}|{runner.Running}";
                if (sig != _qcStatSig) { _qcStatSig = sig; QueueQuestRender(); }
            });
            runner.Stopped += () => Dispatcher.Invoke(RenderQuests);
            runner.Start();
        }
        finally
        {
            _questStarting = false;
            RenderQuests();
        }
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
