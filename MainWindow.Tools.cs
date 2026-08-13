using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ocr;
using EQAvatar.Spike.Roles;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The TOOLS pages (partial class). Hands for the jobs the game makes you do by hand.
///
/// AUTO MERGE is the first of them. The Talisman of Kejaar Kerrath has no drop — the only source
/// is repeating a quest — and the upgrade ladder doubles at every step, so a +10 is 1,024 base
/// items. Every one of them is folded in with the same three clicks: pick the copy out of the
/// bag, drop it in the Place Item box, press Merge Item. Three thousand clicks is not a thing to
/// ask a person for.
///
/// As everywhere else in this app, the clicks are shown to her once rather than discovered: the
/// game has no addon API, and a bag slot's picture changes the moment the item leaves it. What
/// makes the sweep safe is not the clicking, it's the READING — the item's own tier counter is
/// checked before and after every merge, and nothing is counted that the counter didn't confirm.
/// </summary>
public partial class MainWindow
{
    private bool _mrgInit;
    private MergeRole? _mergeRun;

    private void InitMergeUi()
    {
        if (!_mrgInit)
        {
            _mrgInit = true;
            ArtCache.Bind(ArtMergeBanner, "ui-merge-banner.jpg");
            MergePlan p = MergePlan.Current;
            MrgCols.Text = p.Columns.ToString();
            MrgRows.Text = p.Rows.ToString();
            MrgPlus.Text = "5";
            MrgTarget.Text = "10";
            MrgHotkeyText.Text = "Hotkey: Ctrl+Alt+M starts and stops the sweep without leaving the game. "
                               + "F12 stops it too, like every other role.";
        }
        RenderMergeUi();
    }

    // ---------------------------------------------------------------- the picks

    private void RenderMergeUi()
    {
        // MrgMathText is the LAST of this method's controls in document order, so a null check on
        // it is the one that proves the rest exist (MrgGridText and the maths block are declared
        // after MrgPickHost).
        if (MrgPickHost is null || MrgGridText is null || MrgMathText is null) return;
        MergePlan p = MergePlan.Current;
        MrgPickHost.Children.Clear();

        // The four picks as scene tiles, same visual grammar as the Grind mode tiles and the
        // Questing card: art, TITLE, subtitle, and a badge that answers "can I press Run?" at a
        // glance — orange ✕ Not Ready top-right until picked, glowing green ✓ Ready top-left after.
        var tiles = new WrapPanel();
        tiles.Children.Add(MakePickTile("ui-pick-place.jpg", "Place Item", "where each copy drops", "",
            p.PlaceBox.Set,
            "The empty Place Item square on the target item's window — where each copy gets dropped. Click to pick.",
            () => { if (PickMergePoint(p.PlaceBox, "the Place Item box",
                        "Click ON the empty Place Item square on the target item's window, then press Enter.")) p.Save(); RenderMergeUi(); }));
        tiles.Children.Add(MakePickTile("ui-pick-merge.jpg", "Merge Item", "commits & consumes", "",
            p.MergeButton.Set,
            "The Merge Item button that finalises the merge and consumes the copy. Click to pick.",
            () => { if (PickMergePoint(p.MergeButton, "the Merge Item button",
                        "Click ON the Merge Item button on the target item's window, then press Enter.")) p.Save(); RenderMergeUi(); }));
        tiles.Children.Add(MakePickTile("ui-pick-bag.jpg", "the bag", "drag the whole block", "",
            p.BagSet,
            "Drag one box around the WHOLE block of slots holding the copies — it is divided by the columns and rows "
            + "below, because picking 27 slots by hand would be worse than doing the merges by hand. Click to pick.",
            () => { if (PickMergeRect(r => { p.BagX = r.X; p.BagY = r.Y; p.BagW = r.W; p.BagH = r.H; },
                        "the bag area",
                        "Drag a box around the WHOLE block of slots holding the copies — corner to corner — then press Enter.")) p.Save(); RenderMergeUi(); }));
        tiles.Children.Add(MakePickTile("ui-pick-tier.jpg", "tier counter", "the \"4 / 32\" — her witness", "",
            p.TierSet,
            "A TIGHT box around just the n/m numbers on the target item's window. The game logs nothing about merging, "
            + "so this counter is the only proof a merge happened. Click to pick.",
            () => { if (PickMergeRect(r => { p.TierX = r.X; p.TierY = r.Y; p.TierW = r.W; p.TierH = r.H; },
                        "the tier counter",
                        "Drag a TIGHT box around just the \"n / m\" numbers on the target item's window, then press Enter.")) p.Save(); RenderMergeUi(); }));
        MrgPickHost.Children.Add(tiles);

        int mrgHave = (p.PlaceBox.Set ? 1 : 0) + (p.MergeButton.Set ? 1 : 0) + (p.BagSet ? 1 : 0) + (p.TierSet ? 1 : 0);
        MrgPickHost.Children.Add(MakeFireBar(mrgHave / 4.0,
            mrgHave >= 4 ? "everything picked — ready to sweep" : $"{mrgHave} of 4 picks made"));

        MrgGridText.Text = p.BagSet
            ? $"{p.Columns * p.Rows} slots will be visited, left to right then top to bottom"
            : "drag the bag box first";

        bool running = _mergeRun is { Running: true };
        MrgRunBtn.Content = running ? "■  Stop" : "▶  Merge the bag";
        RenderMergeMath();
    }

    private bool PickMergePoint(ScreenPoint point, string what, string hint)
    {
        if (PickMergeRect(r => { point.X = r.X + r.W / 2; point.Y = r.Y + r.H / 2; }, what,
                          hint + "  (drag a small box — she clicks its centre)"))
            return true;
        return false;
    }

    private bool PickMergeRect(Action<(double X, double Y, double W, double H)> store, string what, string hint)
    {
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        {
            MrgStatus.Text = "No game window to capture — launch EQL and keep it on screen, then try again.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return false;
        }
        var dlg = new CompassPickWindow(frame, "Pick " + what, hint) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        store((dlg.NX, dlg.NY, dlg.NW, dlg.NH));
        MrgStatus.Text = $"Saved {what}.";
        MrgStatus.Foreground = Hex("#7CE38B");
        return true;
    }

    private void MrgSaveGrid_Click(object sender, RoutedEventArgs e)
    {
        MergePlan p = MergePlan.Current;
        p.Columns = int.TryParse(MrgCols.Text.Trim(), out int c) ? Math.Clamp(c, 1, 20) : p.Columns;
        p.Rows = int.TryParse(MrgRows.Text.Trim(), out int r) ? Math.Clamp(r, 1, 20) : p.Rows;
        MrgCols.Text = p.Columns.ToString();
        MrgRows.Text = p.Rows.ToString();
        p.Save();
        RenderMergeUi();
        ShowToast("Grid saved");
    }

    // ---------------------------------------------------------------- the arithmetic

    /// <summary>
    /// Turn "I want a +10" into a number of quest runs.
    ///
    /// The ladder: a +0 is worth one base item and each level costs as much again as everything
    /// under it, so +1 is 2, +5 is 32, +10 is 1,024. One Talisman comes out of each full Kerra
    /// cycle (the Orders hand-in is what awards it), so the base-item count IS the quest-run count
    /// — and that is the number that actually says how long this is going to take.
    /// </summary>
    private void RenderMergeMath()
    {
        if (MrgMathText is null) return;
        int plus = int.TryParse(MrgPlus?.Text.Trim(), out int a) ? Math.Clamp(a, 0, 20) : 0;
        int target = int.TryParse(MrgTarget?.Text.Trim(), out int b) ? Math.Clamp(b, 0, 20) : 10;
        if (target <= plus) { MrgMathText.Text = $"A +{plus} is already at or past +{target}."; return; }

        long progress = 0;
        if (_mergeRun is { } run && run.Stats.Tier.Contains('/')
            && int.TryParse(run.Stats.Tier.Split('/')[0], out int have)) progress = have;

        long step = MergePlan.StepCost(plus);
        long remaining = MergePlan.Remaining(plus, progress, target);
        long total = MergePlan.BaseWorth(target);

        MrgMathText.Text =
            $"+{plus} → +{plus + 1} costs {step:N0} base item(s). "
          + $"+{plus} → +{target} costs {remaining:N0} more, and a +{target} is {total:N0} base items all in.\n"
          + $"The Kerra cycle yields one Talisman per run, so that is about {remaining:N0} run(s) of the quest — "
          + "which is why the Quest Runner and this page are the same job in two halves.";
    }

    private void MrgMath_Changed(object sender, TextChangedEventArgs e)
    {
        if (MrgMathText is null) return;                 // fires during InitializeComponent
        RenderMergeMath();
    }

    // ---------------------------------------------------------------- run / test

    private async void MrgTest_Click(object sender, RoutedEventArgs e)
    {
        MergePlan p = MergePlan.Current;
        if (!p.TierSet)
        {
            MrgStatus.Text = "Pick the tier counter first — it's the only thing that can confirm a merge.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        var probe = new MergeRole(p, new ForegroundSendInputSink(() => _grindTarget), () => _grindTarget);
        (int Have, int Need)? read = await probe.ReadTierAsync();
        if (read is null)
        {
            MrgStatus.Text = "Couldn't read the counter. Keep the item's window open, and re-pick a TIGHT box "
                           + "around just the numbers — a box with the word \"Tier\" in it reads worse, not better.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            SetMergeStamp("unreadable", false);
            return;
        }
        MrgStatus.Text = $"Read {read.Value.Have}/{read.Value.Need}. That's what a merge has to move.";
        MrgStatus.Foreground = Hex("#7CE38B");
        SetMergeStamp($"{read.Value.Have}/{read.Value.Need}", true);
    }

    private void SetMergeStamp(string text, bool good)
    {
        MrgStampBorder.Background = good ? Hex("#10281A") : Hex("#2A2410");
        MrgStampBorder.BorderBrush = good ? Hex("#2E7D4F") : Hex("#7A6320");
        MrgStampText.Foreground = good ? Hex("#9FE0B8") : Hex("#FFE1A6");
        MrgStampText.Text = text;
    }

    private void MrgRun_Click(object sender, RoutedEventArgs e) => ToggleMergeRun();

    /// <summary>Start or stop the sweep. Also the Ctrl+Alt+M hotkey target, so it has to be safe to
    /// call with the Auto Merge page never having been opened.</summary>
    private void ToggleMergeRun()
    {
        if (_mergeRun is { Running: true }) { _mergeRun.Stop(); return; }

        if (_grind is { Running: true } || _hunt is { Running: true }
            || _questRun is { Running: true } || _questStarting)
        { ShowToast("Something else is running — Stop (F12) first"); return; }

        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero) { ShowToast("EverQuest not found"); return; }

        MergePlan plan = MergePlan.Current;
        if (!plan.Ready || !plan.TierSet)
        {
            string gap = plan.Missing();
            if (!plan.TierSet) gap = (gap.Length > 0 ? gap + ", " : "") + "the tier counter";
            ShowToast("Auto Merge needs a pick first");
            if (MrgStatus is not null)
            {
                MrgStatus.Text = "Still need a pick for: " + gap + ".";
                MrgStatus.Foreground = Hex("#FFCB6B");
            }
            NavAutoMerge.IsChecked = true;
            return;
        }

        var sink = new ForegroundSendInputSink(() => _grindTarget);
        _mergeRun = new MergeRole(plan, sink, () => _grindTarget);
        _mergeRun.Log += m => Dispatcher.Invoke(() =>
        {
            if (MrgStatus is not null)
            {
                MrgStatus.Text = m;
                MrgStatus.Foreground = m.StartsWith("Stopped") || m.StartsWith("Can't") ? Hex("#FFCB6B") : Hex("#7CE38B");
            }
            if (_mergeRun is { } r && r.Stats.Tier.Length > 0) SetMergeStamp(r.Stats.Tier, true);
            ActivityLog.Record("Merge", m);        // its own source — not the Grind console's problem
        });
        _mergeRun.Stopped += () => Dispatcher.Invoke(RenderMergeUi);
        _mergeRun.Start();
        // Started from the app the game is behind us; started from Ctrl+Alt+M it is already in
        // front and this costs nothing. The sweep waits for focus and for a painted frame itself.
        FocusGameSoon();
        RenderMergeUi();
    }

    // ---------------------------------------------------------------- the ⓘ guide

    private void MrgInfo_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hex("#C6D2DE"),
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(18),
            Text = MergeInfoText,
        };
        var win = new Window
        {
            Title = "How Auto Merge works",
            Owner = this,
            Width = 660, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string MergeInfoText =
        "WHAT IT DOES\n" +
        "Walks every slot in a bag area you've drawn a box around, and folds each copy it finds into the one item "
        + "you're keeping: click the copy, drop it in the Place Item box, press Merge Item. Then the next one.\n\n" +
        "WHY THE CLICKS ARE PICKED AND NOT FOUND\n" +
        "EQ Legends has no addon API, and a bag slot's picture changes the instant the item leaves it — so "
        + "recognising a slot by sight would work once and fail forever after. A POSITION doesn't change, so "
        + "positions are what get stored, as fractions of the game window, which means moving or resizing the "
        + "window doesn't break them.\n\n" +
        "The bag is a grid rather than a list of picked slots for a plainer reason: a stack of twenty-seven "
        + "duplicates covers most of a rucksack, and picking twenty-seven points by hand is worse than doing the "
        + "merges by hand. One dragged box and two numbers say the same thing.\n\n" +
        "HOW SHE KNOWS A MERGE HAPPENED\n" +
        "The game writes nothing to the log about merging. The only witness is the item's own tier counter — the "
        + "\"4 / 32\" on its window — so that is read before and after every single merge, and nothing counts that "
        + "the number didn't confirm. This matters more than it sounds: without it there is no difference between "
        + "merging a hundred items and clicking a hundred empty squares, and a run that can't tell those apart "
        + "will happily do the second one all night.\n\n" +
        "An empty slot is EXPECTED — the sweep walks the whole grid — so one unmoved counter just means \"nothing "
        + "there\" and she moves on. Three reads in a row that fail to parse at all is different: that means the "
        + "item window has been closed or covered, and the run stops.\n\n" +
        "THE ARITHMETIC\n" +
        "A +0 is worth one base item, and every level costs as much again as everything beneath it: +1 is 2, +2 is "
        + "4, +5 is 32, +10 is 1,024. The panel does that sum for you against the level you're on and the one "
        + "you're heading for, and converts it into quest runs — because that, not the merging, is the part that "
        + "takes the time.\n\n" +
        "SAFETY\n" +
        "Foreground only, like every role: she sends input only while EQ Legends is the focused window, so tabbing "
        + "away pauses her mid-sweep. F12 stops her, and so does Ctrl+Alt+M — the hotkey exists so you never have "
        + "to leave the game to start or stop a run.";
}
