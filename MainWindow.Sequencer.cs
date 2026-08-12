using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EQAvatar.Spike.Sequencing;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The Action Sequencer page (partial class): a visual list of sequences that tries hard not
/// to look like a table — art column titles, glowing ID medallions you drag to reorder
/// (display IDs are ALWAYS position 1..N), pill chips added through filter-as-you-type popups,
/// chips you can DRAG within a cell / between sequences (Ctrl = duplicate), and multi-part
/// sequences with per-aspect REVERT chips. The engine that runs them ships with Key Mappings.
/// </summary>
public partial class MainWindow
{
    private bool _seqInit;
    private List<ActionSequence> _sequences = new();
    private SeqCatalog _seqCatalog = new();
    private readonly List<Border> _seqCards = new();
    private readonly List<Border> _seqLines = new();
    private int _seqDragFrom = -1, _seqDragTarget = -1;
    private Popup? _chipPopup;

    // chip drag state
    private sealed record SeqCell(Border Host, WrapPanel Wrap, ActionSequence Seq, int Part, string Col);
    private readonly List<SeqCell> _seqCells = new();
    private (Border pill, ActionSequence seq, int part, string col, SeqChip chip, Point start)? _chipDown;
    private bool _chipDragging;
    private SeqCell? _chipOver;
    private GhostAdorner? _chipGhost;

    private void InitSequencerUi()
    {
        if (!_seqInit)
        {
            _seqInit = true;
            ArtCache.Bind(ArtSeqBanner, "ui-sequencer-banner.jpg");
            ArtCache.Bind(ArtSeqColId, "ui-seq-col-id.jpg");
            ArtCache.Bind(ArtSeqColAction, "ui-seq-col-action.jpg");
            ArtCache.Bind(ArtSeqColStance, "ui-seq-col-stance.jpg");
            ArtCache.Bind(ArtSeqColSpell, "ui-seq-col-spell.jpg");
            ArtCache.Bind(ArtSeqColAbility, "ui-seq-col-ability.jpg");
            _seqCatalog = SeqCatalog.Load();
            _sequences = SequenceStore.Load();
        }
        RenderSequences();
    }

    // ---------------- rendering ----------------

    private void RenderSequences()
    {
        _chipPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        SeqListHost.Children.Clear();
        _seqCards.Clear();
        _seqLines.Clear();
        _seqCells.Clear();

        if (_sequences.Count == 0)
        {
            SeqListHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Hex("#111823"),
                BorderBrush = Hex("#22364A"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 16, 20, 16),
                Child = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Hex("#8FA3B8"),
                    FontSize = 12.5,
                    Text = "No sequences yet — press  ＋ New sequence.\n\nBuild chains like the classic buff routine: set your stance + invocation, " +
                           "swap to the buff spell set, fire Quick Buff — then chain a part 2 with ⛓ that REVERTS everything back the way it was. " +
                           "The engine that RUNS sequences arrives with the Key Mappings page — build them now, they'll be ready.",
                },
            });
            SeqCountText.Text = "0 sequences";
            return;
        }

        for (int i = 0; i < _sequences.Count; i++)
        {
            SeqListHost.Children.Add(MakeInsertLine());
            var card = MakeSeqCard(i);
            _seqCards.Add(card);
            SeqListHost.Children.Add(card);
        }
        SeqListHost.Children.Add(MakeInsertLine());
        SeqCountText.Text = $"{_sequences.Count} sequence{(_sequences.Count == 1 ? "" : "s")} — other pages reference them by number · drag pills to move them, hold Ctrl to copy";
    }

    private Border MakeInsertLine()
    {
        var line = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = Hex("#4FC3F7"),
            Margin = new Thickness(8, 0, 8, 4),
            Visibility = Visibility.Hidden,
        };
        _seqLines.Add(line);
        return line;
    }

    private Border MakeSeqCard(int idx)
    {
        var seq = _sequences[idx];
        if (seq.Parts.Count == 0) seq.Parts.Add(new SeqSegment());

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        // rows: 0 = name, then per part (header row for parts >= 2, cells row per part)
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // name
        string[] cols = { "action", "stance", "spell", "ability" };
        int row = 1;
        for (int p = 0; p < seq.Parts.Count; p++)
        {
            if (p > 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var hdr = MakePartHeader(seq, p);
                Grid.SetColumn(hdr, 1); Grid.SetColumnSpan(hdr, 4); Grid.SetRow(hdr, row);
                grid.Children.Add(hdr);
                row++;
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols.Length; c++)
            {
                var cell = MakeChipCell(seq, p, cols[c]);
                Grid.SetColumn(cell, c + 1); Grid.SetRow(cell, row);
                grid.Children.Add(cell);
            }
            row++;
        }
        int lastRow = row - 1;

        // --- the ID medallion: the number IS the position; drag it to reorder ---
        var num = new TextBlock
        {
            Text = (idx + 1).ToString(),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#9FE0FF"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var med = new Border
        {
            Width = 46, Height = 46,
            CornerRadius = new CornerRadius(999),
            Background = Hex("#0F2740"),
            BorderBrush = Hex("#2E6E96"),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            Child = num,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x4F, 0xC3, 0xF7), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 },
            ToolTip = $"Sequence {idx + 1} — the ID is simply its position in this list. Drag the medallion to reorder; every sequence renumbers instantly (drag #3 to the 8th spot and it becomes #8).",
        };
        Grid.SetColumn(med, 0); Grid.SetRow(med, 0); Grid.SetRowSpan(med, row);
        int myIndex = idx;
        med.MouseLeftButtonDown += (_, e) => { _seqDragFrom = myIndex; _seqDragTarget = -1; med.CaptureMouse(); e.Handled = true; };
        med.MouseMove += (_, e) => { if (med.IsMouseCaptured) UpdateDragTarget(e.GetPosition(SeqListHost).Y); };
        med.MouseLeftButtonUp += (_, _) => { if (med.IsMouseCaptured) med.ReleaseMouseCapture(); FinishSeqDrag(); };
        med.LostMouseCapture += (_, _) => { foreach (var l in _seqLines) l.Visibility = Visibility.Hidden; };
        grid.Children.Add(med);

        // parts badge under the medallion when chained
        if (seq.Parts.Count > 1)
        {
            var badge = new TextBlock
            {
                Text = $"⛓ {seq.Parts.Count} parts",
                FontSize = 9.5,
                Foreground = Hex("#6E8CA6"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 58, 0, 0),
            };
            Grid.SetColumn(badge, 0); Grid.SetRow(badge, 0); Grid.SetRowSpan(badge, row);
            grid.Children.Add(badge);
        }

        // --- name row (optional label; the NUMBER is the real reference) ---
        var nameBox = new TextBox
        {
            Text = seq.Name,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Hex("#BFD2E4"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(2, 0, 2, 2),
            ToolTip = "Optional name. Other pages reference the sequence by its NUMBER — the name is just for you.",
        };
        var nameHint = new TextBlock
        {
            Text = "unnamed — click to name this sequence",
            Foreground = Hex("#54657A"),
            FontSize = 11.5,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(4, 1, 0, 0),
            IsHitTestVisible = false,
            Visibility = string.IsNullOrWhiteSpace(seq.Name) ? Visibility.Visible : Visibility.Collapsed,
        };
        nameBox.TextChanged += (_, _) => nameHint.Visibility = string.IsNullOrWhiteSpace(nameBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        nameBox.LostFocus += (_, _) => { if (seq.Name != nameBox.Text.Trim()) { seq.Name = nameBox.Text.Trim(); SequenceStore.Save(_sequences); } };
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        var nameGrid = new Grid { Margin = new Thickness(4, 2, 0, 4) };
        nameGrid.Children.Add(nameBox);
        nameGrid.Children.Add(nameHint);
        Grid.SetColumn(nameGrid, 1); Grid.SetColumnSpan(nameGrid, 4); Grid.SetRow(nameGrid, 0);
        grid.Children.Add(nameGrid);

        // --- delete sequence ---
        var del = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            Foreground = Hex("#4E6076"),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Remove this sequence (the ones below renumber up)",
        };
        del.MouseLeftButtonUp += (_, _) =>
        {
            _sequences.Remove(seq);
            SeqSaveRender();
            ShowToast("Sequence removed — IDs renumbered");
        };
        Grid.SetColumn(del, 5); Grid.SetRow(del, 0);
        grid.Children.Add(del);

        // --- ⛓ chain a part ---
        var addPart = new TextBlock
        {
            Text = "⛓＋",
            FontSize = 13,
            Foreground = Hex("#5E7C9A"),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Chain another part onto this sequence. Parts run in order, and the bot keeps short-term memory of what earlier parts changed — so later parts can add ↺ REVERT pills (from the stance and spell popups) that restore stances, invocation and spells to their pre-sequence values.",
        };
        addPart.MouseLeftButtonUp += (_, _) =>
        {
            seq.Parts.Add(new SeqSegment());
            SeqSaveRender();
            ShowToast($"Part {seq.Parts.Count} chained — add ↺ revert pills from its popups");
        };
        Grid.SetColumn(addPart, 5); Grid.SetRow(addPart, lastRow);
        grid.Children.Add(addPart);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Hex("#131A24"),
            BorderBrush = Hex("#22364A"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 6, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    private FrameworkElement MakePartHeader(ActionSequence seq, int part)
    {
        var grid = new Grid { Margin = new Thickness(6, 4, 6, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = $"⛓  PART {part + 1}",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#7FB2D9"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var line = new Border { Height = 1, Background = Hex("#22364A"), Margin = new Thickness(8, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);

        var remove = new TextBlock
        {
            Text = "unchain ✕",
            FontSize = 9.5,
            Foreground = Hex("#4E6076"),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove this part (its pills go with it)",
        };
        remove.MouseLeftButtonUp += (_, _) =>
        {
            if (part < seq.Parts.Count) seq.Parts.RemoveAt(part);
            SeqSaveRender();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return grid;
    }

    private FrameworkElement MakeChipCell(ActionSequence seq, int part, string col)
    {
        var wrap = new WrapPanel { Margin = new Thickness(2, 0, 2, 0) };
        foreach (var chip in seq.Parts[part].Cell(col))
            wrap.Children.Add(MakeChip(seq, part, col, chip));

        string hue = ColumnHue(col, col == "stance" ? "stance" : col);
        var plus = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(11, 1, 11, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Background = Brushes.Transparent,
            BorderBrush = Tint(hue, 0x50),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "＋", FontSize = 12, Foreground = Tint(hue, 0xC0) },
            ToolTip = "Add — a filter popup opens: type to narrow instantly, tick several, Enter adds exactly what you typed (even if it isn't listed yet).",
        };
        plus.MouseLeftButtonUp += (_, _) => OpenChipPopup(seq, part, col, plus);
        wrap.Children.Add(plus);

        var host = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 2, 0),
            Margin = new Thickness(2, 0, 2, 0),
            Child = wrap,
        };
        _seqCells.Add(new SeqCell(host, wrap, seq, part, col));
        return host;
    }

    private FrameworkElement MakeChip(ActionSequence seq, int part, string col, SeqChip chip)
    {
        string hue = ColumnHue(col, chip.Kind);
        var lbl = new TextBlock { Text = chip.Label, Foreground = Hex("#E6EDF3"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        if (chip.Kind == "revert") lbl.FontStyle = FontStyles.Italic;
        var x = new TextBlock
        {
            Text = "✕", FontSize = 8.5, Foreground = Hex("#617792"),
            Margin = new Thickness(7, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(lbl);
        sp.Children.Add(x);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Background = Tint(hue, chip.Kind == "revert" ? (byte)0x1C : (byte)0x2E),
            BorderBrush = Tint(hue, 0x78),
            BorderThickness = new Thickness(1),
            Child = sp,
            Cursor = Cursors.Hand,
            ToolTip = ChipTip(chip),
        };
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; seq.Parts[part].Cell(col).Remove(chip); SeqSaveRender(); };

        // drag: move within the cell, into the same column of any sequence/part; Ctrl = copy
        pill.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, x)) return;
            _chipDown = (pill, seq, part, col, chip, e.GetPosition(SeqListHost));
            _chipDragging = false;
        };
        pill.MouseMove += (_, e) => ChipDragMove(pill, e);
        pill.MouseLeftButtonUp += (_, _) => ChipDragEnd(pill);
        pill.LostMouseCapture += (_, _) => ChipDragCleanup();

        var cm = new ContextMenu();
        var dup = new MenuItem { Header = "Duplicate" };
        dup.Click += (_, _) =>
        {
            var cell = seq.Parts[part].Cell(col);
            cell.Insert(Math.Min(cell.IndexOf(chip) + 1, cell.Count), chip.Clone());
            SeqSaveRender();
        };
        var rem = new MenuItem { Header = "Remove" };
        rem.Click += (_, _) => { seq.Parts[part].Cell(col).Remove(chip); SeqSaveRender(); };
        cm.Items.Add(dup);
        cm.Items.Add(rem);
        pill.ContextMenu = cm;
        return pill;
    }

    // ---------------- chip drag mechanics ----------------

    private void ChipDragMove(Border pill, MouseEventArgs e)
    {
        if (_chipDown is null || _chipDown.Value.pill != pill) return;
        var pos = e.GetPosition(SeqListHost);
        if (!_chipDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { _chipDown = null; return; }
            var d = pos - _chipDown.Value.start;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
            _chipDragging = true;
            pill.CaptureMouse();
            pill.Opacity = 0.35;
            var layer = AdornerLayer.GetAdornerLayer(SeqListHost);
            if (layer is not null)
            {
                var ghost = new Border
                {
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(10, 3, 10, 4),
                    Background = Tint(ColumnHue(_chipDown.Value.col, _chipDown.Value.chip.Kind), 0x66),
                    BorderBrush = Tint(ColumnHue(_chipDown.Value.col, _chipDown.Value.chip.Kind), 0xE0),
                    BorderThickness = new Thickness(1),
                    Opacity = 0.9,
                    Child = new TextBlock { Text = _chipDown.Value.chip.Label, Foreground = Hex("#F2F7FC"), FontSize = 11.5 },
                };
                _chipGhost = new GhostAdorner(SeqListHost, ghost);
                layer.Add(_chipGhost);
            }
        }
        _chipGhost?.SetPos(new Point(pos.X + 10, pos.Y + 6));

        var over = FindCellAt(pos, _chipDown.Value.col);
        if (!ReferenceEquals(over, _chipOver))
        {
            SetCellHighlight(_chipOver, false);
            _chipOver = over;
            SetCellHighlight(_chipOver, true);
        }
    }

    private void ChipDragEnd(Border pill)
    {
        if (_chipDown is null || _chipDown.Value.pill != pill) { ChipDragCleanup(); return; }
        var info = _chipDown.Value;
        bool wasDragging = _chipDragging;
        var over = _chipOver;
        if (pill.IsMouseCaptured) pill.ReleaseMouseCapture();
        var dropPos = Mouse.GetPosition(SeqListHost);
        ChipDragCleanup();
        if (!wasDragging || over is null) return;

        bool copy = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var srcCell = info.seq.Parts[info.part].Cell(info.col);
        var dstCell = over.Seq.Parts[over.Part].Cell(over.Col);
        bool sameCell = ReferenceEquals(srcCell, dstCell);

        int target = DropIndex(over, dropPos);
        int srcIdx = srcCell.IndexOf(info.chip);
        if (srcIdx < 0) return;

        var moving = copy ? info.chip.Clone() : info.chip;
        if (!copy)
        {
            srcCell.RemoveAt(srcIdx);
            if (sameCell && srcIdx < target) target--;
        }
        // one stance + one invocation per part — entering a NEW cell replaces the same kind
        if (!sameCell && moving.Kind is "stance" or "invocation")
            dstCell.RemoveAll(c => c.Kind == moving.Kind);
        target = Math.Clamp(target, 0, dstCell.Count);
        dstCell.Insert(target, moving);
        SeqSaveRender();
        if (!sameCell)
            ShowToast(copy ? "Pill copied" : "Pill moved");
    }

    private void ChipDragCleanup()
    {
        if (_chipDown is { } d) d.pill.Opacity = 1.0;
        SetCellHighlight(_chipOver, false);
        _chipOver = null;
        if (_chipGhost is not null)
            AdornerLayer.GetAdornerLayer(SeqListHost)?.Remove(_chipGhost);
        _chipGhost = null;
        _chipDown = null;
        _chipDragging = false;
    }

    private SeqCell? FindCellAt(Point pos, string col)
    {
        foreach (var cell in _seqCells)
        {
            if (cell.Col != col) continue;                    // pills only fit their own column
            var top = cell.Host.TranslatePoint(new Point(0, 0), SeqListHost);
            if (pos.X >= top.X && pos.X <= top.X + cell.Host.ActualWidth &&
                pos.Y >= top.Y && pos.Y <= top.Y + cell.Host.ActualHeight)
                return cell;
        }
        return null;
    }

    private int DropIndex(SeqCell cell, Point posInHost)
    {
        // children = chips… then the ＋ pill last
        int count = Math.Max(0, cell.Wrap.Children.Count - 1);
        for (int i = 0; i < count; i++)
        {
            if (cell.Wrap.Children[i] is not FrameworkElement fe) continue;
            var tl = fe.TranslatePoint(new Point(0, 0), SeqListHost);
            if (posInHost.Y <= tl.Y + fe.ActualHeight &&
                (posInHost.Y < tl.Y || posInHost.X < tl.X + fe.ActualWidth / 2))
                return i;
        }
        return count;
    }

    private static void SetCellHighlight(SeqCell? cell, bool on)
    {
        if (cell is null) return;
        string hue = ColumnHue(cell.Col, cell.Col == "stance" ? "stance" : cell.Col);
        cell.Host.Background = on ? Tint(hue, 0x16) : Brushes.Transparent;
        cell.Host.BorderBrush = on ? Tint(hue, 0x70) : Brushes.Transparent;
    }

    /// <summary>A floating clone of the dragged pill, riding the adorner layer.</summary>
    private sealed class GhostAdorner : Adorner
    {
        private readonly UIElement _child;
        private Point _pos;
        public GhostAdorner(UIElement adorned, UIElement child) : base(adorned)
        {
            _child = child;
            AddVisualChild(child);
            IsHitTestVisible = false;
        }
        public void SetPos(Point p) { _pos = p; InvalidateArrange(); }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;
        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(0, 0);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_pos, _child.DesiredSize));
            return finalSize;
        }
    }

    // ---------------- the filter-as-you-type popup ----------------

    private void OpenChipPopup(ActionSequence seq, int part, string col, UIElement anchor)
    {
        _chipPopup?.SetCurrentValue(Popup.IsOpenProperty, false);

        string spellKind = "spell";                    // cast | memspell | spellset (spell column only)
        var selected = new List<SeqChip>();
        var panel = new StackPanel { Width = 288 };

        panel.Children.Add(new TextBlock
        {
            Text = (col switch { "action" => "ADD ACTIONS", "stance" => "ADD STANCE / INVOCATION", "spell" => "ADD SPELLS", _ => "ADD ABILITIES" })
                   + (part > 0 ? $"  ·  PART {part + 1}" : ""),
            FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Hex("#7FB2D9"), Margin = new Thickness(1, 0, 0, 6),
        });

        var kindPills = new List<Border>();
        var filter = new TextBox { FontSize = 12.5 };
        var listHost = new StackPanel();
        Action refresh = () => { };

        if (col == "spell")
        {
            var kinds = new (string key, string label, string tip)[]
            {
                ("spell", "cast", "Cast the spell (it must be memorized — the engine will be slot-aware)."),
                ("memspell", "mem", "Memorize this one spell before casting anything."),
                ("spellset", "spell set", "Swap the whole set with /memspellset 'name'."),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (key, label, tip) in kinds)
            {
                var kp = new Border
                {
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(10, 2, 10, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = key,
                    ToolTip = tip,
                    Child = new TextBlock { Text = label, FontSize = 11, Foreground = Hex("#CFE0F0") },
                };
                kp.MouseLeftButtonUp += (_, _) => { spellKind = key; StyleKindPills(kindPills, spellKind); refresh(); };
                kindPills.Add(kp);
                row.Children.Add(kp);
            }
            StyleKindPills(kindPills, spellKind);
            panel.Children.Add(row);
        }

        panel.Children.Add(filter);
        panel.Children.Add(new TextBlock
        {
            Text = "type to filter — updates instantly · Enter adds what you typed",
            FontSize = 10, Foreground = Hex("#5E7488"), Margin = new Thickness(1, 3, 0, 5),
        });
        panel.Children.Add(new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listHost });

        var addBtn = new Button { Content = "Add selected", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        panel.Children.Add(addBtn);

        refresh = () =>
        {
            listHost.Children.Clear();
            string f = filter.Text.Trim();

            // parts ≥ 2: offer the ↺ REVERT pills first (they restore pre-sequence values)
            foreach (var (kind, value) in RevertOptions(col, part))
            {
                if (f.Length > 0 && value.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    "revert".IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var rcb = new CheckBox
                {
                    Content = "↺ revert " + value + "   · back to pre-sequence",
                    Foreground = Hex("#BFD9CE"),
                    FontSize = 12,
                    Margin = new Thickness(2, 2, 0, 2),
                    IsChecked = selected.Any(s => s.Kind == kind && s.Value == value),
                };
                string k2 = kind, v2 = value;
                rcb.Checked += (_, _) => { if (!selected.Any(s => s.Kind == k2 && s.Value == v2)) selected.Add(new SeqChip(k2, v2)); };
                rcb.Unchecked += (_, _) => selected.RemoveAll(s => s.Kind == k2 && s.Value == v2);
                listHost.Children.Add(rcb);
            }

            foreach (var (kind, value) in PopupOptions(col, spellKind))
            {
                if (f.Length > 0 && value.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var cb = new CheckBox
                {
                    Content = kind == "invocation" ? value + "   · invocation" : value,
                    Foreground = Hex("#DDE7F0"),
                    FontSize = 12,
                    Margin = new Thickness(2, 2, 0, 2),
                    IsChecked = selected.Any(s => s.Kind == kind && s.Value == value),
                };
                string k = kind, v = value;
                cb.Checked += (_, _) => { if (!selected.Any(s => s.Kind == k && s.Value == v)) selected.Add(new SeqChip(k, v)); };
                cb.Unchecked += (_, _) => selected.RemoveAll(s => s.Kind == k && s.Value == v);
                listHost.Children.Add(cb);
            }
            if (f.Length > 0 && listHost.Children.Count == 0)
                listHost.Children.Add(new TextBlock
                {
                    Text = $"nothing matches — press Enter to add '{f}'",
                    FontSize = 11, FontStyle = FontStyles.Italic, Foreground = Hex("#6E8296"), Margin = new Thickness(2, 4, 0, 2),
                });
        };
        filter.TextChanged += (_, _) => refresh();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            string f = filter.Text.Trim();
            if (f.Length == 0) { CommitChips(seq, part, col, selected); return; }
            string kind = col == "spell" ? spellKind : col == "stance" ? "stance" : col;
            var known = PopupOptions(col, spellKind).FirstOrDefault(o => string.Equals(o.value, f, StringComparison.OrdinalIgnoreCase));
            if (known.value is not null)
            {
                if (!selected.Any(s => s.Kind == known.kind && s.Value == known.value)) selected.Add(new SeqChip(known.kind, known.value));
            }
            else
            {
                _seqCatalog.Remember(CatalogListFor(col, spellKind), f);
                selected.Add(new SeqChip(kind, f));
            }
            filter.Clear();
            refresh();
        };
        addBtn.Click += (_, _) => CommitChips(seq, part, col, selected);
        refresh();

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Hex("#0E1520"),
                BorderBrush = Hex("#2E6E96"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 9, 11, 10),
                Margin = new Thickness(0, 4, 12, 12),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.55 },
                Child = panel,
            },
        };
        popup.Opened += (_, _) => filter.Focus();
        _chipPopup = popup;
        popup.IsOpen = true;
    }

    /// <summary>The ↺ options a part ≥ 2 can restore, per column.</summary>
    private static IEnumerable<(string kind, string value)> RevertOptions(string col, int part)
    {
        if (part <= 0) yield break;
        switch (col)
        {
            case "stance":
                yield return ("revert", "stance");
                yield return ("revert", "invocation");
                break;
            case "spell":
                yield return ("revert", "spells");
                break;
        }
    }

    private static void StyleKindPills(List<Border> pills, string active)
    {
        foreach (var p in pills)
        {
            bool on = (string)p.Tag == active;
            p.Background = on ? Tint("#82AAFF", 0x3A) : Brushes.Transparent;
            p.BorderBrush = on ? Tint("#82AAFF", 0xB0) : Tint("#82AAFF", 0x45);
        }
    }

    private IEnumerable<(string kind, string value)> PopupOptions(string col, string spellKind)
    {
        switch (col)
        {
            case "action":
                foreach (string a in _seqCatalog.Actions) yield return ("action", a);
                break;
            case "stance":
                foreach (string s in _seqCatalog.Stances) yield return ("stance", s);
                foreach (string i in _seqCatalog.Invocations) yield return ("invocation", i);
                break;
            case "spell":
                if (spellKind == "spellset") { foreach (string s in _seqCatalog.SpellSets) yield return ("spellset", s); }
                else { foreach (string s in _seqCatalog.Spells) yield return (spellKind, s); }
                break;
            default:
                foreach (string a in _seqCatalog.Abilities) yield return ("ability", a);
                break;
        }
    }

    private List<string> CatalogListFor(string col, string spellKind) => col switch
    {
        "action" => _seqCatalog.Actions,
        "stance" => _seqCatalog.Stances,
        "spell" => spellKind == "spellset" ? _seqCatalog.SpellSets : _seqCatalog.Spells,
        _ => _seqCatalog.Abilities,
    };

    private void CommitChips(ActionSequence seq, int part, string col, List<SeqChip> picked)
    {
        _chipPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        if (picked.Count == 0) return;
        var cell = seq.Parts[part].Cell(col);
        foreach (var chip in picked)
        {
            // one physical stance + one invocation per part — a new one replaces the old;
            // same for each distinct ↺ revert pill
            if (chip.Kind is "stance" or "invocation") cell.RemoveAll(c => c.Kind == chip.Kind);
            if (chip.Kind == "revert") cell.RemoveAll(c => c.Kind == "revert" && c.Value == chip.Value);
            cell.Add(chip.Clone());
        }
        SeqSaveRender();
    }

    // ---------------- drag to reorder sequences (IDs are positional) ----------------

    private void UpdateDragTarget(double y)
    {
        int target = 0;
        for (int i = 0; i < _seqCards.Count; i++)
        {
            var top = _seqCards[i].TranslatePoint(new Point(0, 0), SeqListHost).Y;
            if (y > top + _seqCards[i].ActualHeight / 2) target = i + 1;
        }
        _seqDragTarget = target;
        for (int i = 0; i < _seqLines.Count; i++)
            _seqLines[i].Visibility = i == target ? Visibility.Visible : Visibility.Hidden;
    }

    private void FinishSeqDrag()
    {
        foreach (var l in _seqLines) l.Visibility = Visibility.Hidden;
        int from = _seqDragFrom, t = _seqDragTarget;
        _seqDragFrom = -1; _seqDragTarget = -1;
        if (from < 0 || t < 0 || from >= _sequences.Count) return;
        int to = t > from ? t - 1 : t;
        if (to == from || to < 0 || to >= _sequences.Count) return;
        var s = _sequences[from];
        _sequences.RemoveAt(from);
        _sequences.Insert(to, s);
        SeqSaveRender();
        ShowToast($"Moved — it's sequence #{to + 1} now");
    }

    private void SeqSaveRender()
    {
        SequenceStore.Save(_sequences);
        RenderSequences();
    }

    // ---------------- page chrome ----------------

    private void SeqNew_Click(object sender, RoutedEventArgs e)
    {
        _sequences.Add(new ActionSequence());
        SeqSaveRender();
    }

    private static string ColumnHue(string col, string kind) => kind switch
    {
        "invocation" => "#7FDBCA",
        "revert" => "#9FB6AA",
        _ => col switch { "action" => "#4FC3F7", "stance" => "#C792EA", "spell" => "#82AAFF", _ => "#FFCB6B" },
    };

    private static SolidColorBrush Tint(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    private static string ChipTip(SeqChip chip) => chip.Kind switch
    {
        "action" => "General action — fires the key your game has bound to it (the Key Mappings page will read those bindings straight from the game).",
        "stance" => "Physical stance for this part of the sequence — one per part; adding another replaces it.",
        "invocation" => "Invocation for this part — one per part; adding another replaces it.",
        "spell" => "Cast this spell. The engine will be aware of your spell slots and what's memorized.",
        "memspell" => "Memorize this spell before anything casts.",
        "spellset" => $"Swap the whole spell set:  /memspellset '{chip.Value}'",
        "revert" => "↺ Restores this aspect to its PRE-SEQUENCE value — the bot keeps short-term memory of what earlier parts changed.",
        _ => "Activated ability — abilities run AFTER spells, so Quick Buff finds the right spells already memorized.",
    } + "\nDrag to move (same column, any sequence) · Ctrl+drag to copy · right-click to duplicate or remove.";

    private void SeqInfo_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hex("#C6D2DE"),
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(18),
            Text = SequencerInfoText,
        };
        var win = new Window
        {
            Title = "How the Action Sequencer works",
            Owner = this,
            Width = 660, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string SequencerInfoText =
@"THE SHORT VERSION
A sequence is a reusable chain of things your avatar does in order: general actions, a stance + invocation, spell work, then abilities. Build them here as visual pills; other pages will reference a sequence by its NUMBER.

SEQUENCE NUMBERS
The number on the medallion is simply the sequence's position in the list — always 1..N. Drag a medallion to reorder: drag #3 down to the 8th spot and it BECOMES #8; everything else renumbers instantly. There are no gaps and no fixed IDs to memorize.

THE FOUR COLUMNS
• ACTIONS — general things anyone can do: jump, sit, target the nearest NPC, open the inventory. Each maps to whatever key your game has bound (the Key Mappings page in the Information section will read your Controls → Key binds screen and keep a last-refreshed stamp).
• STANCES — both physical stances and invocations live here. Each part of a sequence carries ONE stance and ONE invocation; adding another simply replaces it.
• SPELLS — three flavors from the popup: CAST a spell, MEM a single spell, or swap the entire set with /memspellset 'name'. The engine will know how many spell slots you have and what's in them.
• ABILITIES — activated abilities, deliberately executed AFTER spells so something like Quick Buff finds the right spells already memorized.

PILLS: ADD, DRAG, COPY
Press ＋ in any cell: a popup filters as you type (instantly), you can tick several options, and Enter adds exactly what you typed even if it isn't in the list yet — your additions are remembered for next time.
DRAG a pill to move it: reorder within its cell, or drop it into the SAME column of any other sequence or part — the cell lights up when you're over a valid home. Hold CTRL while dropping to COPY instead of move. Pills only fit their own column (a spell can't live in Actions). Remove with ✕, duplicate in place with right-click.

MULTI-PART SEQUENCES + ↺ REVERT
Press ⛓＋ at a sequence's edge to chain another part. Parts run in order, and while a sequence runs the bot keeps SHORT-TERM MEMORY of what it changed — so the popups in part 2+ offer ↺ REVERT pills: put the stance back, the invocation back, the spells back exactly as they were before the sequence started. The classic use: part 1 = stance + invocation + buff spell set + Quick Buff, part 2 = revert all three. 'unchain ✕' removes a part.

WHEN DO THEY RUN?
This page is the builder. The runtime engine ships together with the Key Mappings page — it needs your real key binds to fire actions. Everything you build now is saved (%AppData%\EQAvatar\sequences.json) and will run as-is once the engine lands.";
}
