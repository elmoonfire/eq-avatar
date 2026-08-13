using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Login;
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
    private DropShadowEffect? _mrgGlow;
    private bool _mrgLookupBusy, _mrgScanBusy;

    private void InitMergeUi()
    {
        if (!_mrgInit)
        {
            _mrgInit = true;
            ArtCache.Bind(ArtMergeBanner, "ui-merge-banner.jpg");
            MergePlan p = MergePlan.Current;
            MrgItemName.Text = p.ItemName.Length > 0 ? p.ItemName : "Talisman of Kejaar Kerrath";
            MrgItemName.LostFocus += (_, _) =>
            {
                MergePlan.Current.ItemName = MrgItemName.Text.Trim();
                MergePlan.Current.Save();
                RenderMergeForecast();
            };
            MrgHotkeyText.Text = "Hotkey: Ctrl+Alt+M starts and stops the sweep without leaving the game. "
                               + "F12 stops it too, like every other role.";
        }
        RenderMergeUi();
    }

    // ---------------------------------------------------------------- the picks

    private void RenderMergeUi()
    {
        // MrgConsoleHost is the LAST of this method's controls in document order, so a null check
        // on it is the one that proves the rest exist.
        if (MrgPickHost is null || MrgForecastHost is null || MrgConsoleHost is null) return;
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
                        "Click ON the empty Place Item square on the target item's window, then press Enter.",
                        sh => Shot(p, "place", sh))) p.Save(); RenderMergeUi(); },
            p.PlaceBox.Set ? () => ShowMergeShot(p, "place", "The Place Item box",
                "Each copy gets dropped in the centre of this box.") : null));
        tiles.Children.Add(MakePickTile("ui-pick-merge.jpg", "Merge Item", "commits & consumes", "",
            p.MergeButton.Set,
            "The Merge Item button that finalises the merge and consumes the copy. Click to pick.",
            () => { if (PickMergePoint(p.MergeButton, "the Merge Item button",
                        "Click ON the Merge Item button on the target item's window, then press Enter.",
                        sh => Shot(p, "merge", sh))) p.Save(); RenderMergeUi(); },
            p.MergeButton.Set ? () => ShowMergeShot(p, "merge", "The Merge Item button",
                "Pressed once per copy. If the item window has moved since, re-pick it.") : null));
        tiles.Children.Add(MakePickTile("ui-pick-slot.jpg", "the copy's icon",
            p.HasIcon ? (p.HasIconSize ? "found by sight" : "⚠ re-pick me") : "she matches this", "",
            p.HasIcon,
            "Drag a TIGHT box around ONE of the copies in your bag. She learns its icon and then finds every other "
            + "copy by sight — so only squares that actually hold one get clicked. Click the READY badge to see "
            + "exactly what she compares against.",
            () => { if (PickMergeRect(r => { }, "one copy of the item",
                        "Drag a TIGHT box around ONE copy in your bag — right up to the icon's edges — then press Enter.",
                        sh => Shot(p, "item", sh),
                        (frame, box) =>
                        {
                            // Keep the OLD signature if this box produced none (it can: a box against
                            // the window edge samples past the frame). Overwriting a good pick with
                            // null while the status line says "Saved" in green is the worst of both.
                            double[]? sig = QuestFind.SigFromRegion(frame, box.X, box.Y, box.W, box.H);
                            if (sig is null)
                            {
                                MrgStatus.Text = "That box didn't produce a signature — drag inside the game window, "
                                               + "not against its edge, and keep the box on the icon.";
                                MrgStatus.Foreground = Hex("#FFCB6B");
                                return;
                            }
                            p.IconSig = sig;
                            p.IconW = box.W; p.IconH = box.H;
                        })) { p.Save(); RenderMergeUi(); } },
            p.HasIcon ? () => ShowMergeShot(p, "item", "The copy's icon",
                p.HasIconSize
                    ? "She slides a window of exactly this size across the bag area and clicks the closest match."
                    : "⚠ No size stored — re-pick this once so the precise scan can take over.") : null));
        tiles.Children.Add(MakePickTile("ui-pick-bag.jpg", "the bag area", "every slot scanned", "",
            p.BagSet,
            "Drag ONE box around ALL the bags holding copies. Every slot inside it gets looked at — no columns, "
            + "no rows, no counting. Click to pick.",
            () => { if (PickMergeRect(r => { p.BagX = r.X; p.BagY = r.Y; p.BagW = r.W; p.BagH = r.H; },
                        "the bag area",
                        "Drag a box around ALL the bag slots holding the copies — corner to corner — then press Enter.",
                        sh => Shot(p, "bag", sh))) p.Save(); RenderMergeUi(); },
            p.BagSet ? () => ShowMergeShot(p, "bag", "The bag area",
                "Everything inside the orange box is scanned for the copy's icon.") : null));
        tiles.Children.Add(MakePickTile("ui-pick-tier.jpg", "tier counter", "the \"4 / 32\" — her witness", "",
            p.TierSet,
            "A TIGHT box around just the n/m numbers on the target item's window. The game logs nothing about merging, "
            + "so this counter is the only proof a merge happened. Click to pick.",
            () => { if (PickMergeRect(r => { p.TierX = r.X; p.TierY = r.Y; p.TierW = r.W; p.TierH = r.H; },
                        "the tier counter",
                        "Drag a TIGHT box around just the \"n / m\" numbers on the target item's window, then press Enter.",
                        sh => Shot(p, "tier", sh))) p.Save(); RenderMergeUi(); },
            p.TierSet ? () => ShowMergeShot(p, "tier", "The tier counter",
                "This is OCR'd before and after every merge. If the box holds anything but the numbers, tighten it.") : null));
        MrgPickHost.Children.Add(tiles);

        int mrgHave = (p.PlaceBox.Set ? 1 : 0) + (p.MergeButton.Set ? 1 : 0) + (p.BagSet ? 1 : 0)
                    + (p.TierSet ? 1 : 0) + (p.HasIcon ? 1 : 0);
        MrgPickHost.Children.Add(MakeFireBar(mrgHave / 5.0,
            mrgHave >= 5 ? "everything picked — ready to sweep" : $"{mrgHave} of 5 picks made"));

        bool running = _mergeRun is { Running: true };
        MrgRunBtn.Content = running ? "■  Stop" : "▶  Merge the bag";

        // The card reports the run the same way the Questing card does: green, glowing, breathing.
        if (MrgCard is not null)
        {
            MrgCard.Background = running ? Hex("#0E2318") : Hex("#121A28");
            MrgCard.BorderBrush = running ? Hex("#49F27E") : Hex("#26405A");
            MrgCard.BorderThickness = new Thickness(running ? 2 : 1);
            // ONE effect for the window's life. Building a fresh one per render restarted the
            // breath from 0.30 every time she spoke, so the card twitched instead of breathing —
            // and orphaned a Forever animation on each discarded effect.
            if (running)
            {
                if (_mrgGlow is null)
                {
                    _mrgGlow = new DropShadowEffect
                    { Color = Color.FromRgb(0x49, 0xF2, 0x7E), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 };
                    _mrgGlow.BeginAnimation(DropShadowEffect.OpacityProperty,
                        new DoubleAnimation(0.30, 0.75, TimeSpan.FromSeconds(1.6))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                }
                MrgCard.Effect = _mrgGlow;
            }
            else MrgCard.Effect = null;
        }

        RenderMergeConsole(running);
        RenderMergeForecast();
    }

    private bool PickMergePoint(ScreenPoint point, string what, string hint, Action<PickShot?>? shot = null)
        => PickMergeRect(r => { point.X = r.X + r.W / 2; point.Y = r.Y + r.H / 2; }, what,
                         hint + "  (drag a small box — she clicks its centre)", shot);

    private bool PickMergeRect(Action<(double X, double Y, double W, double H)> store, string what, string hint,
                               Action<PickShot?>? shot = null,
                               Action<System.Drawing.Bitmap, (double X, double Y, double W, double H)>? learn = null)
    {
        // MergePlan.Current is the SAME object the running sweep reads every pass. Re-picking the
        // bag area under it would move the rectangle it is scanning mid-run — and a modal opened
        // while a copy is on the cursor leaves it there.
        if (_mergeRun is { Running: true })
        {
            MrgStatus.Text = "Stop the sweep before changing a pick — she's using these right now.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return false;
        }
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
        // Learning and the snapshot both read the PICKER'S frame, never a fresh grab — the modal
        // was covering the game, and a new capture would photograph this app (the 0.9.37 lesson).
        try { learn?.Invoke(frame, (dlg.NX, dlg.NY, dlg.NW, dlg.NH)); } catch { }
        try { shot?.Invoke(PickShot.From(frame, dlg.NX, dlg.NY, dlg.NW, dlg.NH, what.Contains("bag") ? 0.06 : 1.6)); }
        catch { }
        MrgStatus.Text = $"Saved {what}.";
        MrgStatus.Foreground = Hex("#7CE38B");
        return true;
    }

    /// <summary>Store a pick's snapshot — or CLEAR a stale one when the capture failed, so the
    /// badge can never show a picture of somewhere she no longer clicks.</summary>
    private static void Shot(MergePlan p, string key, PickShot? sh)
    {
        if (sh is null) p.Shots.Remove(key);
        else p.Shots[key] = sh;
    }

    private void ShowMergeShot(MergePlan p, string key, string title, string note)
    {
        p.Shots.TryGetValue(key, out PickShot? sh);
        new PickShotWindow(title, p.ItemName.Length > 0 ? p.ItemName : "Auto Merge", sh,
            sh is null
                ? "No snapshot for this pick — either it predates snapshots, or the capture failed at pick time. "
                + "Re-pick it once and this window will show you exactly what she uses."
                : (note.Length > 0 ? note : null))
        { Owner = this }.ShowDialog();
    }

    // ---------------------------------------------------------------- the console

    /// <summary>Auto Merge's own narration, same shape as the Questing card: one oversized line for
    /// what she is doing now, the steps behind it, and nothing from any other module.</summary>
    private void RenderMergeConsole(bool running)
    {
        MrgConsoleHost.Children.Clear();
        List<ActivityEntry> lines = ActivityLog.Snapshot(e => e.Source == MergeSource, 150);
        ActivityEntry? now = lines.Count > 0 ? lines[^1] : null;

        var nowBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = running ? Hex("#10301F") : Hex("#0C1420"),
            BorderBrush = running ? Hex("#3FCB74") : Hex("#26303F"),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 8),
        };
        var nowStack = new StackPanel();
        nowStack.Children.Add(new TextBlock
        {
            Text = running ? "NOW" : "LAST", FontSize = 8.5, FontWeight = FontWeights.Bold,
            Foreground = running ? Hex("#49F27E") : Hex("#5E7C9A"),
        });
        var nowText = new TextBlock
        {
            Text = now?.Text ?? "nothing yet — press Merge the bag and she'll narrate every step here.",
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = now is null ? Hex("#5E7C9A")
                       : now.IsBad ? Hex("#FFCB6B") : now.IsGood ? Hex("#49F27E") : Hex("#DDE7F0"),
        };
        if (running && now is not null && !now.IsBad)
            nowText.Effect = new DropShadowEffect
            { Color = Color.FromRgb(0x49, 0xF2, 0x7E), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 };
        nowStack.Children.Add(nowText);
        nowBorder.Child = nowStack;
        MrgConsoleHost.Children.Add(nowBorder);

        var lineStack = new StackPanel();
        foreach (ActivityEntry e in lines.Count > 1 ? lines.GetRange(0, lines.Count - 1) : new List<ActivityEntry>())
            lineStack.Children.Add(new TextBlock
            {
                Text = $"{e.When:HH:mm:ss}  {e.Text}",
                FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = e.IsBad ? Hex("#FFCB6B") : e.IsGood ? Hex("#7CE38B")
                           : e.IsStep ? Hex("#8AA0B6") : Hex("#C6D2DE"),
                Margin = new Thickness(0, 0, 0, 1),
            });
        var scroll = new ScrollViewer
        {
            Height = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0), Content = lineStack,
            Background = Hex("#0C0F13"), Padding = new Thickness(8, 5, 8, 5),
        };
        scroll.Loaded += (_, _) => scroll.ScrollToEnd();
        MrgConsoleHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = Hex("#26303F"), BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = scroll,
        });
    }

    internal const string MergeSource = "Merge";

    // ---------------------------------------------------------------- the forecast

    /// <summary>Copies counted by the last scan; -1 = never scanned.</summary>
    private int _mrgCopies = -1;
    private ItemInfo? _mrgInfo;
    private string _mrgInfoFor = "\u0000";
    private (int Have, int Need)? _mrgTier;

    private async void MrgScan_Click(object sender, RoutedEventArgs e)
    {
        if (_mrgScanBusy) return;
        MergePlan p = MergePlan.Current;
        if (!p.ScanReady)
        {
            MrgStatus.Text = "Pick the bag area and the copy's icon first — that pair is what lets her count.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero) { ShowToast("EverQuest not found"); return; }
        if (_mergeRun is { Running: true }) { ShowToast("Stop the sweep first"); return; }

        _mrgScanBusy = true;
        MrgScanBtn.IsEnabled = false;
        try
        {
            MrgStatus.Text = "Looking at the bags…";
            MrgStatus.Foreground = Hex("#9FE0FF");

            // The count is a PHOTOGRAPH of the screen at the game's coordinates. Taken while this
            // window is over the game it scans our own pixels, finds nothing, and reports "0
            // copies" in green — indistinguishable from an empty bag. So the game must genuinely
            // be in front, and if it can't be, say so instead of counting anyway.
            if (!GameFocus.IsFront(_grindTarget))
            {
                bool up = _settings.FocusGameOnStart
                       && await GameFocus.BringAndSettleAsync(_grindTarget, settleMs: 500);
                if (!up)
                {
                    MrgStatus.Text = "Bring EverQuest to the front first — from behind this window she'd be "
                                   + "counting the app's own pixels.";
                    MrgStatus.Foreground = Hex("#FFCB6B");
                    return;
                }
            }

            IntPtr h = _grindTarget;
            int copies = await Task.Run(() => MergeRole.CountCopies(h, MergePlan.Current));
            if (p.TierSet)
            {
                string txt = await ScreenText.ReadRectAsync(h, p.TierX, p.TierY, p.TierW, p.TierH);
                _mrgTier = MergeRole.ParseTier(txt);
            }
            _mrgCopies = copies;
            ActivityLog.Record(MergeSource, copies < 0
                ? "⚠ couldn't read the bag area to count copies."
                : $"· counted {copies} copy(s) in the bag area"
                  + (_mrgTier is { } t ? $", target reads {t.Have}/{t.Need}" : ""));
            MrgStatus.Text = copies < 0
                ? "Couldn't read the screen — is the game on screen?"
                : $"Found {copies} copy(s) in the bag area."
                  + (p.TierSet && _mrgTier is null ? " (The tier counter didn't read — re-pick it.)" : "");
            MrgStatus.Foreground = copies < 0 ? Hex("#FFCB6B") : Hex("#7CE38B");
            RenderMergeUi();
        }
        finally { _mrgScanBusy = false; MrgScanBtn.IsEnabled = true; }
    }

    private async void MrgLookup_Click(object sender, RoutedEventArgs e)
    {
        if (_mrgLookupBusy) return;                  // four impatient clicks used to race on one file
        string name = MrgItemName.Text.Trim();
        MergePlan.Current.ItemName = name;
        MergePlan.Current.Save();
        _mrgLookupBusy = true;
        MrgLookupBtn.IsEnabled = false;
        try
        {
            MrgStatus.Text = $"Looking up {name}…";
            MrgStatus.Foreground = Hex("#9FE0FF");
            ItemInfo? info = null;
            string? unreachable = null;
            try { info = await ItemLookup.FetchAsync(name); }
            catch (System.Net.Http.HttpRequestException) { unreachable = "couldn't reach eqlwiki.com"; }
            catch (TaskCanceledException) { unreachable = "eqlwiki.com didn't answer in time"; }

            // "The network is down" and "you spelled it wrong" are different problems, and telling
            // someone to check their spelling while they are offline sends them round in circles.
            MrgStatus.Text = unreachable is not null
                ? $"{unreachable} — the forecast still works, it just can't show stats yet."
                : info is null
                    ? $"Couldn't find \"{name}\" on the wiki — check the spelling against the item window in game."
                    : $"{info.Name}: {info.Stats.Count} stat(s) read from the wiki.";
            MrgStatus.Foreground = info is null ? Hex("#FFCB6B") : Hex("#7CE38B");
            _mrgInfoFor = "\u0000";                 // force the cache to re-read what we just wrote
            RenderMergeUi();
        }
        finally { _mrgLookupBusy = false; MrgLookupBtn.IsEnabled = true; }
    }

    /// <summary>
    /// What the bag you already have is worth, as a ladder.
    ///
    /// The arithmetic is the game's own documented rule: a +0 is one base item and every level
    /// costs as much again as everything beneath it, so the copies you own convert into tiers with
    /// diminishing returns — 32 copies is a +5 from scratch but barely a third of the way from +5
    /// to +6. That is a hard thing to feel from a number and an easy thing to see from a bar, which
    /// is the entire reason this is drawn rather than printed.
    /// </summary>
    private void RenderMergeForecast()
    {
        if (MrgForecastHost is null) return;
        MergePlan p = MergePlan.Current;
        MrgForecastHost.Children.Clear();

        int tier = 0; long progress = 0; long need = 1;
        // The LIVE counter wins. A scan taken before the run is a snapshot of a bag that the run is
        // currently emptying — keeping it in front kept the panel promising a level the item had
        // already passed, for the whole hour anyone was watching it.
        (int Have, int Need)? read = null;
        if (_mergeRun is { } run && run.Stats.Tier.Contains('/'))
        {
            string[] parts = run.Stats.Tier.Split('/');
            if (int.TryParse(parts[0], out int h2) && int.TryParse(parts[1], out int n2)) read = (h2, n2);
        }
        read ??= _mrgTier;
        if (read is { } t2)
        {
            progress = t2.Have;
            need = Math.Max(1, t2.Need);
            // The denominator IS the ladder step: 1,2,4,…512 for +0…+9, so it names the tier.
            tier = (int)Math.Round(Math.Log2(Math.Max(1, need)));
        }

        int copies = _mrgCopies;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock
        {
            Text = read is null ? "tier not read yet" : $"now +{tier}  ({progress}/{need})",
            FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Hex("#BFE3FF"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = copies < 0 ? "   ·   press “Count what's in the bags” to forecast"
                              : $"   ·   {copies} copy(s) in the bags",
            FontSize = 11.5, Foreground = Hex("#9FB6CC"), VerticalAlignment = VerticalAlignment.Center,
        });
        MrgForecastHost.Children.Add(head);

        if (read is null)
        {
            // No counter, no projection. Drawing pips and a "you'll reach +5" from an ASSUMED +0
            // tells someone with a +8 that merging will demote them and that they need 992 runs
            // they don't. A blank with a reason is worth more than a confident fiction.
            MrgForecastHost.Children.Add(new TextBlock
            {
                Text = "Pick the tier counter and press “Test read” (or “Count what's in the bags”) — without it "
                     + "there is nothing to project from, and guessing +0 would be worse than saying nothing.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                Foreground = Hex("#FFCB6B"), Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        // Spend the copies up the ladder.
        int projTier = tier; long projProgress = progress, projNeed = need, left = Math.Max(0, copies);
        if (copies > 0)
            while (left > 0)
            {
                long toNext = projNeed - projProgress;
                if (left >= toNext) { left -= toNext; projTier++; projProgress = 0; projNeed = MergePlan.StepCost(projTier); }
                else { projProgress += left; left = 0; }
                if (projTier > 20) break;
            }

        // The ladder itself: one pip per level, filled to where you are, lit to where you land.
        var ladder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        for (int lvl = 0; lvl <= 10; lvl++)
        {
            bool have = lvl <= tier;
            bool gain = lvl > tier && lvl <= projTier;
            var pip = new Border
            {
                Width = 40, Height = 26, CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 4, 0),
                Background = have ? Hex("#1B4D30") : gain ? Hex("#14202E") : Hex("#0E141C"),
                BorderBrush = have ? Hex("#49F27E") : gain ? Hex("#FF9E3D") : Hex("#26303F"),
                BorderThickness = new Thickness(have || gain ? 2 : 1),
                ToolTip = $"+{lvl} costs {MergePlan.BaseWorth(lvl):N0} base item(s) all in.",
                Child = new TextBlock
                {
                    Text = "+" + lvl, FontSize = 11,
                    FontWeight = have || gain ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = have ? Hex("#9FE0B8") : gain ? Hex("#FFCB6B") : Hex("#5E7C9A"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (gain)
                pip.Effect = new DropShadowEffect
                { Color = Color.FromRgb(0xFF, 0x9E, 0x3D), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 };
            ladder.Children.Add(pip);
        }
        MrgForecastHost.Children.Add(ladder);

        if (copies >= 0)
        {
            double frac = projNeed > 0 ? (double)projProgress / projNeed : 0;
            MrgForecastHost.Children.Add(MakeFireBar(frac,
                projTier > tier
                    ? $"{copies} copy(s) → +{projTier} ({projProgress}/{projNeed} toward +{projTier + 1})"
                    : $"{copies} copy(s) → still +{tier} ({projProgress}/{projNeed}) — not enough for the next level"));

            long toTen = MergePlan.Remaining(projTier, projProgress, 10);
            MrgForecastHost.Children.Add(new TextBlock
            {
                Text = $"After merging what you have: +{projTier}. A +10 needs {toTen:N0} more base item(s) — "
                     + "and the Kerra cycle yields one per run, so that is the number of quest runs still ahead.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Hex("#C6D2DE"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        // ---- what the item is actually worth at that tier
        // Read from disk ONCE per name, not once per log line: this method is re-run every time the
        // sweep speaks, and a file read per narration line is a stutter you can feel.
        string wantName = p.ItemName.Length > 0 ? p.ItemName : MrgItemName?.Text?.Trim() ?? "";
        if (!string.Equals(wantName, _mrgInfoFor, StringComparison.OrdinalIgnoreCase))
        {
            _mrgInfoFor = wantName;
            _mrgInfo = wantName.Length > 0 ? ItemLookup.Cached(wantName) : null;
        }
        ItemInfo? info = _mrgInfo;
        var preview = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (p.Shots.TryGetValue("item", out PickShot? itemShot) && itemShot.Bytes() is { } bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();
                var img = new Image { Source = bmp, Width = 88, Height = 88, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                preview.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8), Background = Hex("#0C0F13"),
                    BorderBrush = Hex("#2A4A57"), BorderThickness = new Thickness(1),
                    Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0), Child = img,
                    ToolTip = "The copy you pointed at — this is the picture she matches against.",
                });
            }
            catch { }
        }

        var statStack = new StackPanel();
        if (info is null)
            statStack.Children.Add(new TextBlock
            {
                Text = "No item stats yet — type the item's name above and press “Look it up” to see what each "
                     + "tier is actually worth.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Hex("#9FB6CC"),
            });
        else if (info.Stats.Count == 0)
            statStack.Children.Add(new TextBlock
            {
                Text = $"{info.Name} — the wiki lists no numeric stats for this item, so there is nothing to "
                     + "project. The tier ladder above still applies.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Hex("#9FB6CC"),
            });
        else
        {
            statStack.Children.Add(new TextBlock
            {
                Text = $"{info.Name}{(info.Slot.Length > 0 ? "  ·  " + info.Slot : "")}",
                FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Hex("#BFE3FF"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            string[] heads = { "", $"now +{tier}", $"→ +{projTier}", "at +10" };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < heads.Length; c++)
            {
                var th = new TextBlock
                {
                    Text = heads[c], FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = c == 2 ? Hex("#FFCB6B") : Hex("#5E7C9A"), Margin = new Thickness(0, 0, 0, 3),
                };
                Grid.SetColumn(th, c); Grid.SetRow(th, 0);
                grid.Children.Add(th);
            }
            int row = 1;
            foreach (ItemStat st in info.Stats)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                double nowV = ItemLookup.AtTier(st, tier);
                double projV = ItemLookup.AtTier(st, projTier);
                double tenV = ItemLookup.AtTier(st, 10);
                string[] cells = { st.Name, Num(nowV), Num(projV), Num(tenV) };
                for (int c = 0; c < cells.Length; c++)
                {
                    var tb = new TextBlock
                    {
                        Text = cells[c], FontSize = 11.5,
                        FontFamily = c == 0 ? null : new FontFamily("Consolas"),
                        Foreground = c == 0 ? Hex("#9FB6CC")
                                   : c == 2 && projV > nowV ? Hex("#49F27E") : Hex("#C6D2DE"),
                        Margin = new Thickness(0, 0, 0, 1),
                    };
                    Grid.SetColumn(tb, c); Grid.SetRow(tb, row);
                    grid.Children.Add(tb);
                }
                row++;
            }
            statStack.Children.Add(grid);
            statStack.Children.Add(new TextBlock
            {
                Text = "+10% a tier, cumulative and rounded down, with a guaranteed +1 per tier — the game's own "
                     + "documented rule. Weapon damage rises 5% a tier and weapon delay never falls.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = Hex("#5E7C9A"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        preview.Children.Add(statStack);
        MrgForecastHost.Children.Add(preview);
    }

    private static string Num(double v) => Math.Abs(v - Math.Round(v)) < 0.05
        ? Math.Round(v).ToString("N0") : v.ToString("0.#");

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
            _mrgTier = null;
            RenderMergeUi();
            return;
        }
        MrgStatus.Text = $"Read {read.Value.Have}/{read.Value.Need}. That's what a merge has to move.";
        MrgStatus.Foreground = Hex("#7CE38B");
        SetMergeStamp($"{read.Value.Have}/{read.Value.Need}", true);
        // The forecast reads this too. Without it the page held two different answers to "what
        // tier is this?" — a green stamp saying 4/32 and a panel underneath saying "not read yet".
        _mrgTier = read;
        ActivityLog.Record(MergeSource, $"· test read: {read.Value.Have}/{read.Value.Need}");
        RenderMergeUi();
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
            ActivityLog.Record(MergeSource, m);    // its own source — not the Grind console's problem
            // ONLY the console. A full RenderMergeUi here rebuilt five art tiles, two animated fire
            // bars, eleven glowing pips and a PNG decode on the UI thread — synchronously, with the
            // sweep's own thread blocked on Dispatcher.Invoke waiting for it — once per merge.
            RenderMergeConsole(_mergeRun is { Running: true });
        });
        _mergeRun.Stopped += () => Dispatcher.Invoke(RenderMergeUi);
        // The bag is about to change under it: a count from before the sweep is not a forecast any
        // more, so drop it rather than let it go stale on screen.
        _mrgCopies = -1;
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
