using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            p.HasIcon ? (!p.HasIconSize ? "⚠ re-pick me" : p.HasPixels ? "pixel-exact" : "⚠ colour only") : "she matches this", "",
            p.HasIcon,
            "Put the FIXED SQUARE over ONE copy in your bag — the same square, the same size, every time, which is "
            + "the size everything gets compared at. Click to place it, arrow keys nudge it a pixel, the wheel "
            + "resizes it to match your slot, and the magnified picture beside it is exactly what gets stored. "
            + "Free drag is still there for anything bigger.\n\n"
            + (p.HasIcon && !p.HasPixels
                ? "⚠ This pick predates pixel matching, so only the COLOURS of the icon are being compared — which "
                + "cannot separate two items drawn from the same palette, and twice hasn't. Re-pick it once and she "
                + "keeps the icon's actual pixels instead."
                : "The colours find the candidates fast; the icon's ACTUAL PIXELS decide which of them is really a "
                + "copy, nudged a few pixels either way to find the best fit. A real copy matches over 85% even "
                + "misaligned and dimmed; a different icon in the same colours scores around 45%."),
            () => { if (PickMergeRect(r => { }, "one copy of the item",
                        "Put the square over ONE copy in your bag, matched to the slot, then press Enter. "
                        + "These exact pixels become the reference, and this exact size becomes the size she "
                        + "compares with — so the square, not the magnified view, is what has to fit the slot.",
                        sh => Shot(p, "item", sh),
                        (frame, box) =>
                        {
                            // Keep the OLD signature if this box produced none (it can: a box against
                            // the window edge samples past the frame). Overwriting a good pick with
                            // null while the status line says "Saved" in green is the worst of both.
                            double[]? sig = QuestFind.SigFromRegion(frame, box.X, box.Y, box.W, box.H);
                            // The close-up is taken from the SAME box in the SAME frame — one drag,
                            // two resolutions, so the two screens can never describe different
                            // pixels. If the fine read fails while the coarse one worked, keep the
                            // coarse pick and say the confirm isn't armed; a silent half-pick would
                            // look exactly like a good one.
                            double[]? fine = QuestFind.SigFromRegion(frame, box.X, box.Y, box.W, box.H,
                                                                     QuestFind.SigGridFine);
                            if (sig is null)
                            {
                                MrgStatus.Text = "That box didn't produce a signature — drag inside the game window, "
                                               + "not against its edge, and keep the box on the icon.";
                                MrgStatus.Foreground = Hex("#FFCB6B");
                                return;
                            }
                            p.IconSig = sig;
                            p.IconSigFine = fine;
                            // The pixels themselves, from the same drag on the same frame. This is
                            // what actually decides now; the two signatures above are the fast
                            // screen in front of it and the fallback behind it.
                            p.IconPixels = QuestFind.PatchFromRegion(frame, box.X, box.Y, box.W, box.H);
                            p.IconW = box.W; p.IconH = box.H;
                            // Everything she learned to avoid was learned by comparing against the
                            // OLD picture. Against a new one those comparisons mean nothing, and a
                            // stale reject is the one failure mode with no symptom: she would
                            // quietly refuse to pick up a real copy and report an empty bag.
                            int forgotten = p.RejectCount;
                            p.ForgetAllRejects();
                            ActivityLog.Detail(MergeSource,
                                $"icon reference: {p.IconPixels?.W ?? 0}×{p.IconPixels?.H ?? 0} px, "
                                + $"search radius ±{QuestFind.SearchPadFor(p.IconPixels?.W ?? 0)}");
                            ActivityLog.Record(MergeSource, forgotten > 0
                                ? $"· re-picked the copy's icon — forgot {forgotten} learned non-copy icon(s), "
                                + "they were measured against the old picture."
                                : "· re-picked the copy's icon.");
                        },
                        SwatchSize, rememberSize: true)) { p.Save(); RenderMergeUi(); } },
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
        MrgPickHost.Children.Add(MakeRejectRow(p));
        MrgPickHost.Children.Add(MakeNameGateRow(p));

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

    /// <summary>
    /// What she has learned to leave alone, and a way to take it back.
    ///
    /// Learning is only safe when it is reversible and visible. A sweep that quietly decided some
    /// picture "isn't a copy" and was wrong about it would report an empty bag forever, with no
    /// symptom and nothing on screen to disagree with — so the count is shown even at zero, and
    /// forgetting is one click.
    /// </summary>
    private FrameworkElement MakeRejectRow(MergePlan p)
    {
        int n = p.RejectCount;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 6, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = !p.HasIcon ? ""
                 : !p.HasFineIcon
                     ? "⚠ Colour-only matching. Two items in the same palette can look identical to her — "
                     + "re-pick the copy's icon to arm the close-up confirm."
                 : n == 0
                     ? "Close-up confirm armed. Nothing learned as a look-alike yet."
                     : $"Close-up confirm armed. Avoiding {n} icon(s) she has learned aren't copies.",
            FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 620,
            Foreground = p.HasIcon && !p.HasFineIcon ? Hex("#FFCB6B") : Hex("#5E7C9A"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (n > 0)
        {
            var forget = new TextBlock
            {
                Text = "   forget them", FontSize = 10.5, Foreground = Hex("#4FC3F7"),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                ToolTip = "Clear the look-alikes she's learned to avoid. Do this if the bag has changed, or if "
                        + "you think she has written off something that really was a copy.",
            };
            forget.MouseLeftButtonUp += (_, _) =>
            {
                // Not while she is using them: MergePlan.Current is the same object the sweep reads
                // every pass, and emptying the list underneath it mid-look is a race for no benefit.
                if (_mergeRun is { Running: true }) { ShowToast("Stop the sweep first"); return; }
                p.ForgetAllRejects();
                p.Save();
                ActivityLog.Record(MergeSource, $"· forgot {n} learned non-copy icon(s) — she'll judge every "
                                              + "square on the picks alone again.");
                RenderMergeUi();
            };
            row.Children.Add(forget);
        }
        return row;
    }

    /// <summary>
    /// The name gate: on/off, what she'll be looking for, and a way to prove it works before three
    /// thousand clicks depend on it.
    ///
    /// The test matters more than the switch. Everything else on this page can be checked by eye —
    /// you can SEE whether the Place Item pick is on the box. Whether Windows' OCR can read a
    /// tooltip over a dark game frame is not something anyone can know by looking, so it gets a
    /// button that says exactly what was read.
    /// </summary>
    private FrameworkElement MakeNameGateRow(MergePlan p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 6, 0, 0) };
        string[] words = MergeRole.NameTokens(p.ItemName);

        var box = new CheckBox
        {
            Content = "Read the name before picking anything up",
            IsChecked = p.ConfirmByName, FontSize = 11, Foreground = Hex("#C6D2DE"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Hover each candidate square and check the item's own tooltip before touching it. "
                    + "This is the only check on this page that isn't a guess — an icon signature can't tell "
                    + "a Desecrated Kejaar Totem from a Talisman of Kejaar Kerrath, and twice now it hasn't.",
        };
        box.Click += (_, _) =>
        {
            if (_mergeRun is { Running: true }) { ShowToast("Stop the sweep first"); box.IsChecked = p.ConfirmByName; return; }
            p.ConfirmByName = box.IsChecked == true;
            p.Save();
            RenderMergeUi();
        };
        row.Children.Add(box);

        row.Children.Add(new TextBlock
        {
            Text = words.Length == 0
                ? "   ⚠ type the item's name above — without it there's nothing to match"
                : "   looking for: " + string.Join(" + ", words),
            FontSize = 10.5, Foreground = words.Length == 0 ? Hex("#FFCB6B") : Hex("#5E7C9A"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var test = new TextBlock
        {
            Text = "   test the name read", FontSize = 10.5, Foreground = Hex("#4FC3F7"),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = "Hover the best-matching square in the bag area and print exactly what she can read there. "
                    + "Do this once before a real sweep.",
        };
        test.MouseLeftButtonUp += async (_, _) => await TestNameReadAsync();
        row.Children.Add(test);
        return row;
    }

    private bool _mrgNameBusy;

    /// <summary>
    /// Hover the square the icon scan likes best and report what the tooltip says there — using the
    /// SAME read the sweep uses, so a passing test means the run will pass too.
    /// </summary>
    private async Task TestNameReadAsync()
    {
        if (_mrgNameBusy) return;
        if (_mergeRun is { Running: true }) { ShowToast("Stop the sweep first"); return; }
        MergePlan p = MergePlan.Current;
        // Commit what's in the box FIRST. The name is only saved on the TextBox's LostFocus, and the
        // test link is a TextBlock — not focusable — so clicking it never fires that. Type a
        // corrected spelling, click test, get a green pass, and the run would still use the old
        // name. A test that exercises different input than the run proves nothing.
        string want = (MrgItemName.Text ?? "").Trim();
        if (!string.Equals(p.ItemName, want, StringComparison.Ordinal)) { p.ItemName = want; p.Save(); }
        if (want.Length == 0)
        {
            MrgStatus.Text = "Type the item's name above first — that's what she matches against.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        if (!p.ScanReady)
        {
            MrgStatus.Text = "Pick the bag area and the copy's icon first — the test hovers the square they point at.";
            MrgStatus.Foreground = Hex("#FFCB6B");
            return;
        }
        if (_grindTarget == IntPtr.Zero) AutoTargetEq();
        if (_grindTarget == IntPtr.Zero) { ShowToast("EverQuest not found"); return; }

        _mrgNameBusy = true;
        try
        {
            MrgStatus.Text = "Bringing EverQuest forward and hovering the best match…";
            MrgStatus.Foreground = Hex("#9FE0FF");

            // Same rule as the copy count: a hover read taken while this window is over the game
            // photographs the app, reads nothing, and blames the user's picks.
            if (!GameFocus.IsFront(_grindTarget)
                && !await GameFocus.BringAndSettleAsync(_grindTarget, settleMs: 500))
            {
                MrgStatus.Text = "Bring EverQuest to the front first — from behind this window she'd be reading "
                               + "the app's own pixels.";
                MrgStatus.Foreground = Hex("#FFCB6B");
                return;
            }

            IntPtr h = _grindTarget;
            System.Drawing.Bitmap? frame = await Task.Run(() => QuestFind.Capture(h));
            QuestFind.IconHit? best = null;
            if (frame is not null)
                using (frame)
                    best = QuestFind.FindIconInRect(frame, p.BagX, p.BagY, p.BagW, p.BagH,
                                                    p.IconSig!, p.IconW, p.IconH);
            if (best is null)
            {
                MrgStatus.Text = "Couldn't read the bag area at all — is the game on screen with the bags open?";
                MrgStatus.Foreground = Hex("#FFCB6B");
                return;
            }

            var probe = new MergeRole(p, new ForegroundSendInputSink(() => _grindTarget), () => _grindTarget);
            MergeRole.NameLook look = await probe.ReadNameAtAsync(best.X, best.Y, want);

            if (!look.Read)
            {
                MrgStatus.Text = "Couldn't look at all — " + look.Why + ". Nothing was read, so this says nothing "
                               + "about the item.";
                MrgStatus.Foreground = Hex("#FFCB6B");
                ActivityLog.Record(MergeSource, "⚠ test name read couldn't look: " + look.Why);
                return;
            }
            string head = look.Matched
                ? $"✔ Read it. That square names itself \"{want}\" ({look.Hits} of {look.Tokens} words)."
                : $"✖ That square did NOT read back as \"{want}\" ({look.Hits} of {look.Tokens} words).";
            MrgStatus.Text = head + $"  Nearest text sat {look.Dist:0.00} away.\n\nWhat she can read there:\n"
                           + look.Nearby;
            MrgStatus.Foreground = look.Matched ? Hex("#7CE38B") : Hex("#FFCB6B");
            ActivityLog.Record(MergeSource, (look.Matched ? "✔" : "✖")
                + $" test name read at {best.X * 100:0.0}%, {best.Y * 100:0.0}% — {look.Hits}/{look.Tokens} words. "
                + "Read: " + look.Nearby);
        }
        // Without this the dispatcher marks the exception handled and the status line sits on
        // "Bringing EverQuest forward…" for ever, which reads as a hang rather than a failure.
        catch (Exception ex)
        {
            MrgStatus.Text = "The name test failed: " + ex.Message;
            MrgStatus.Foreground = Hex("#FFCB6B");
            ActivityLog.Record(MergeSource, "⚠ the name test threw: " + ex.Message);
        }
        finally { _mrgNameBusy = false; }
    }

    /// <summary>The remembered square size, defended against a settings file that predates the
    /// setting or was hand-edited to zero. Zero reads as "no square offered", so an old settings
    /// file would quietly turn the feature off on every pick with nothing said about it.</summary>
    private int SwatchSize => Math.Clamp(_settings.IconSwatchPx <= 0 ? 32 : _settings.IconSwatchPx,
                                         CompassPickWindow.MinSwatch, CompassPickWindow.MaxSwatch);

    /// <summary>
    /// A point pick, which is a region pick whose CENTRE is the only part kept.
    ///
    /// That makes the fixed square the better tool by some distance: the size is irrelevant to the
    /// answer, so placing a square with one click and nudging it with the arrow keys beats dragging
    /// a small box through a view where one mouse pixel is two frame pixels. Free drag is still one
    /// button away.
    /// </summary>
    private bool PickMergePoint(ScreenPoint point, string what, string hint, Action<PickShot?>? shot = null)
        => PickMergeRect(r => { point.X = r.X + r.W / 2; point.Y = r.Y + r.H / 2; }, what,
                         hint + "  (she clicks the centre of whatever you mark — the square's size "
                              + "doesn't matter here)", shot,
                         swatchPx: SwatchSize);

    /// <param name="swatchPx">Non-zero opens the picker on the FIXED SQUARE instead of free drag —
    /// the right tool for an inventory slot, where the box has to be the same size every time and
    /// the mouse cannot resolve a single frame pixel through the picker's scaled-down view.</param>
    /// <param name="rememberSize">Whether the size the user settles on is written back to settings
    /// as THE icon swatch size. True on the icon pick alone, because there the square's size is the
    /// answer: it becomes the reference's dimensions and the stride the sweep searches with.
    /// Everywhere else the square is only a placement aid — a point pick keeps its CENTRE and throws
    /// the size away — so letting those write the setting means wheeling the square up to cover the
    /// Merge Item button silently resizes the next icon pick to two slots wide, and the reference
    /// quietly gains a neighbour's pixels. The square is still offered on every pick; only the
    /// remembering is narrowed.</param>
    private bool PickMergeRect(Action<(double X, double Y, double W, double H)> store, string what, string hint,
                               Action<PickShot?>? shot = null,
                               Action<System.Drawing.Bitmap, (double X, double Y, double W, double H)>? learn = null,
                               int swatchPx = 0, bool rememberSize = false)
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
        // The square is OFFERED on every pick and OPENS on the ones it suits. A bag area or a tier
        // counter is a rectangle only the user knows the shape of, so those start on the drag — but
        // the button is there, because the last thing this should do is make someone hunt for a
        // feature they were told exists.
        int offered = swatchPx > 0 ? swatchPx : SwatchSize;
        var dlg = new CompassPickWindow(frame, "Pick " + what, hint, offered, startSwatch: swatchPx > 0)
        { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        if (rememberSize && dlg.UsedSwatch && dlg.SwatchPx != offered)
        {
            _settings.IconSwatchPx = dlg.SwatchPx;
            try { _settings.Save(); } catch { /* a remembered size is not worth an exception */ }
        }
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

    /// <summary>
    /// Auto Merge's own narration — the SAME console the Questing card carries, pointed at a
    /// different source.
    ///
    /// Built once and kept. It used to be torn down and rebuilt from the log every time she spoke,
    /// which meant the scrollbar snapped back to a fresh ScrollViewer's idea of "the bottom" on
    /// every line: you could not read back through a failure while the thing that failed was still
    /// running. Now the console owns its own scrollback and this method only tells it whether the
    /// sweep is alive.
    /// </summary>
    private EQAvatar.Spike.Ui.ModuleConsole? _mrgConsole;

    private void RenderMergeConsole(bool running)
    {
        if (MrgConsoleHost is null) return;
        if (_mrgConsole is null)
        {
            _mrgConsole = new EQAvatar.Spike.Ui.ModuleConsole(
                MergeSource, "", null, "LIVE ACTIVITY",
                "nothing yet — press Merge the bag and she'll narrate every step here.",
                () => NavActivity.IsChecked = true, ShowToast,
                MakeResizableConsole,
                () => _settings.ConsoleDetail,
                d => { _settings.ConsoleDetail = d; _settings.Save(); SyncConsoleChrome(); });
        }
        // The host is cleared by nothing else, but a re-parent is cheap insurance: adding an element
        // that still has a parent throws, and the throw would land inside a render pass.
        if (!MrgConsoleHost.Children.Contains(_mrgConsole))
        {
            _mrgConsole.Detach();
            MrgConsoleHost.Children.Add(_mrgConsole);
        }
        _mrgConsole.SetRunning(running);
    }

    internal const string MergeSource = MergeRole.MergeSource;

    // ---------------------------------------------------------------- the forecast

    /// <summary>Copies counted by the last scan; -1 = never scanned.</summary>
    private int _mrgCopies = -1;
    private ItemInfo? _mrgInfo;
    /// <summary>Shared with the Game Data catalog: one client, one cache, one icon atlas.</summary>
    private EQAvatar.Spike.Net.GameDataClient? _mrgGd;
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
            List<(double X, double Y, double Score)>? found =
                await Task.Run(() => MergeRole.ScanCopies(h, MergePlan.Current));
            // null means the screen couldn't be read. -1 is the sentinel every branch below and the
            // forecast already understand; reporting it as 0 would print "Found 0 copy(s)" in green
            // over a bag nobody could see.
            int copies = found?.Count ?? -1;
            // The SPREAD, not just the count. Whether the threshold is in the right place is a
            // question about the gap between the copies and everything else, and that gap is only
            // visible if the numbers are printed.
            if (MergePlan.Current.HasPixels && found is { Count: > 0 })
                ActivityLog.Record(MergeSource, $"· pixel match on the {found.Count} copy(s) found: "
                    + string.Join(", ", found.Take(12).Select(f => $"{f.Score * 100:0.0}%"))
                    + (found.Count > 12 ? " …" : ""));
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
            try { info = await ItemLookup.FetchAsync(name, _settings); }
            catch (System.Net.Http.HttpRequestException) { unreachable = "couldn't reach the hub or the wiki"; }
            catch (TaskCanceledException) { unreachable = "neither the hub nor the wiki answered in time"; }

            // "The network is down" and "you spelled it wrong" are different problems, and telling
            // someone to check their spelling while they are offline sends them round in circles.
            MrgStatus.Text = unreachable is not null
                ? $"{unreachable} — the forecast still works, it just can't show stats yet."
                : info is null
                    ? $"Couldn't find \"{name}\" — check the spelling against the item window in game."
                    : $"{info.Name}: {info.Stats.Count} stat(s) read"
                      + (info.Id > 0 ? " from our own item corpus." : " from the wiki.");
            MrgStatus.Foreground = info is null ? Hex("#FFCB6B") : Hex("#7CE38B");

            // The icon sheet has to be on disk before Icon() can cut anything out of it. Without
            // this the preview stays blank until the user happens to open the Game Data catalog,
            // which is a dependency no one could ever guess at.
            if (info is { Id: > 0, IconId: > 0 })
                try { await (_mrgGd ??= new EQAvatar.Spike.Net.GameDataClient(_settings)).EnsureAtlasAsync(); }
                catch { /* the forecast reads perfectly well without a picture */ }

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
    /// <summary>
    /// Draw the forecast, and never take the app down doing it.
    ///
    /// This panel is the one place on the page built out of data we did not write: stat names and
    /// numbers scraped off a wiki, a cached icon sheet, an OCR reading. It is also redrawn from
    /// every click handler on the page — so an exception in here does not produce a blank panel, it
    /// unwinds through the click and kills the process. It did exactly that: one null FontFamily,
    /// and setting the tier pick closed the app.
    ///
    /// The failure that matters is not the lost panel. It is that this can be redrawn WHILE A SWEEP
    /// IS RUNNING, and a process that dies mid-sweep dies holding an item on the cursor, with no
    /// put-back and nothing said. A forecast is worth exactly none of that.
    /// </summary>
    private void RenderMergeForecast()
    {
        try { RenderMergeForecastCore(); }
        catch (Exception ex)
        {
            Diag.BotLog.Log("merge", "forecast render failed: " + ex);
            ActivityLog.Record(MergeSource, "⚠ couldn't draw the forecast (" + ex.Message
                                          + ") — everything else on this page still works.");
            try
            {
                MrgForecastHost.Children.Clear();
                MrgForecastHost.Children.Add(new TextBlock
                {
                    Text = "⚠ Couldn't draw the forecast: " + ex.Message
                         + "\nThe picks, the sweep and the console are unaffected.",
                    FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 620,
                    Foreground = Hex("#FFCB6B"), Margin = new Thickness(0, 2, 0, 0),
                });
            }
            catch { /* if even the apology won't draw, the log has it */ }
        }
    }

    private void RenderMergeForecastCore()
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
            // The score, not just the tier: it is the number the game is really tracking, it moves
            // by one for every copy that goes in, and "1006 of 1024" answers "how close am I?" in a
            // way that "+9, 494/512" never quite does.
            // The GAME'S own words first. It never shows the internal score — what you see on the
            // item is "+9" and "56/512", the remainder toward the next upgrade — so leading with a
            // number no player ever sees would be the app inventing its own vocabulary. The score
            // follows in smaller type, because it is ours: it is what makes the progress bar and
            // the forecast honest, and it deserves to be visible, not hidden.
            Text = read is null ? "tier not read yet"
                 : (read is { } rd ? UpgradeScore.ScoreFrom(rd.Have, rd.Need) : null) is not { } sc
                     ? "counter unreadable — re-pick a tight box round just the numbers"
                     : $"+{tier}   ·   {progress} / {need} toward +{tier + 1}",
            FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Hex("#BFE3FF"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Every item carries a score out of 1024. Its tier is the highest power of two the score has "
                    + "passed, and the counter in game is the remainder over the next step. Merging ADDS scores, "
                    + "so nothing is lost and the order you merge in cannot matter.",
        });
        head.Children.Add(new TextBlock
        {
            Text = ((read is { } hs ? UpgradeScore.ScoreFrom(hs.Have, hs.Need) : null) is { } hsc
                        ? $"   ({hsc} of 1024 toward +10)" : "")
                 + (copies < 0 ? "   ·   press “Count what's in the bags” to forecast"
                               : $"   ·   {copies} copy(s) in the bags"),
            FontSize = 11.5, Foreground = Hex("#9FB6CC"), VerticalAlignment = VerticalAlignment.Center,
        });
        MrgForecastHost.Children.Add(head);

        long score = read is { } rr ? (UpgradeScore.ScoreFrom(rr.Have, rr.Need) ?? -1) : -1;
        if (read is null || score < 0)
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

        // ONE addition. The game keeps a single score out of 1024 and merging ADDS scores — a 510
        // folded into an 8 is a 518, the same +9 either would have reached the long way round — so
        // there is nothing to simulate and no order to get wrong. (Copies are counted as +0 drops
        // worth 1 each, which is what a quest turn-in gives; a stack of part-upgraded copies would
        // be worth MORE, never less, so this reads as the floor.)
        long projScore = UpgradeScore.Plus(score, Math.Max(0, copies));
        int projTier = UpgradeScore.TierFor(projScore);
        (int projHaveI, int projNeedI) = UpgradeScore.CounterFor(projScore);
        long projProgress = projHaveI, projNeed = projNeedI;

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
            // The bar is the SCORE out of 1024 — linear, so half full really is half way. A bar of
            // "progress into the current tier" looks nearly finished at +9 and is in fact 512 runs
            // from the end. At the top it must read as FINISHED: 1024 shows as 0/1024, which drew
            // an empty bar and promised "toward +11" at the exact moment the grind ended.
            bool finished = projTier >= UpgradeScore.MaxTier;
            MrgForecastHost.Children.Add(MakeFireBar(finished ? 1.0 : (double)projScore / UpgradeScore.Max,
                finished
                    ? $"{copies} copy(s) → +10, finished. Anything left over is spare."
                    : $"{copies} copy(s) → +{projTier}, {projProgress}/{projNeed} toward +{projTier + 1}"
                      + $"   ({projScore} of 1024)"));

            long toTen = UpgradeScore.ToReach(projScore, UpgradeScore.MaxTier);
            MrgForecastHost.Children.Add(new TextBlock
            {
                Text = finished
                    ? "That finishes it: what you already have takes this item to +10."
                    : $"After merging what you have: +{projTier}, {projProgress}/{projNeed}. A +10 is {toTen:N0} more base "
                    + "copies — one per Kerra cycle, so that is the number of quest runs still ahead. Merge order "
                    + "doesn't matter: scores simply add, and nothing is ever lost.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Foreground = Hex("#C6D2DE"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        // ---- what the item is actually worth at that tier
        //
        // The picture is the GAME'S OWN icon when the hub knows the item (it cuts the cell out of
        // the atlas for us at ?icon=), and the copy you pointed at otherwise. The first is what the
        // item IS; the second is what she matches against — both are worth being able to see, and
        // showing the real art next to your own snapshot is itself a check that they agree.
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
        // Id > 0 means the row CAME FROM the hub, so its icon number is the hub's. A wiki result
        // carries lucy_img_ID — a different numbering — and drawing that cell captioned "the game's
        // own icon" would be a confident wrong picture. The art itself comes from the shared
        // GameDataClient's cached sheet, the same one the Game Data catalog draws from.
        if (info is { IconId: > 0, Id: > 0 }
            && (_mrgGd ??= new EQAvatar.Spike.Net.GameDataClient(_settings)).Icon(info.IconId) is { } iconArt)
        {
            var art = new Image { Width = 80, Height = 80, Stretch = Stretch.Uniform, Source = iconArt };
            RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.NearestNeighbor);
            preview.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Hex("#0C0F13"),
                BorderBrush = Hex("#2E7D4F"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 0), Child = art,
                ToolTip = $"{info.Name} — the game's own icon, from our item corpus.",
            });
        }
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
                Text = $"{info.Name} — no numeric stats recorded for this item, so there is nothing to project "
                     + "from. The score and tier ladder above still apply.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Hex("#9FB6CC"),
            });
        else
        {
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            nameRow.Children.Add(new TextBlock
            {
                Text = $"{info.Name}{(info.Slot.Length > 0 ? "  ·  " + info.Slot : "")}",
                FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Hex("#BFE3FF"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (info.Url.Length > 0)
            {
                var link = new TextBlock
                {
                    Text = "  open on the hub ↗", FontSize = 10.5, Foreground = Hex("#4FC3F7"),
                    VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                    ToolTip = info.Url,
                };
                string url = info.Url;
                link.MouseLeftButtonUp += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
                };
                nameRow.Children.Add(link);
            }
            statStack.Children.Add(nameRow);
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
                        Foreground = c == 0 ? Hex("#9FB6CC")
                                   : c == 2 && projV > nowV ? Hex("#49F27E") : Hex("#C6D2DE"),
                        Margin = new Thickness(0, 0, 0, 1),
                    };
                    // The numbers are monospaced so the columns line up; the stat NAME is prose and
                    // wants the normal face. That was written as `FontFamily = c == 0 ? null : ...`,
                    // which throws — WPF converts a null FontFamily to the empty string and rejects
                    // it, and the throw lands inside a click handler and takes the app down. There
                    // is no "unset this" value to assign: the way to keep the inherited font is not
                    // to touch the property at all.
                    if (c > 0) tb.FontFamily = new FontFamily("Consolas");
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
        // The name test owns the cursor while it hovers and reads. Starting a sweep on top of it —
        // easy to do, since Ctrl+Alt+M works from inside the game — puts two things moving the mouse
        // at once, and the sweep's click can land wherever the probe just parked it.
        if (_mrgNameBusy) { ShowToast("The name test is still running"); return; }

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
        // Re-resolved at the START of every run, never cached across runs: EQ opens a NEW log file
        // for a new character or a re-login, and a role holding yesterday's handle reads a file the
        // server stopped writing to. That exact bug cost the Quest Runner a whole field test.
        _currentLog = EQAvatar.Spike.Log.EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim()) ?? _currentLog;
        _mergeRun = new MergeRole(plan, sink, () => _grindTarget, _currentLog);
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
        "EQ Legends has no addon API. The Place Item box and the Merge Item button don't move, so those are "
        + "POSITIONS, stored as fractions of the game window — moving or resizing the window doesn't break them. "
        + "The copies DO move, through the bags, as each one is consumed, so those are found by sight.\n\n" +
        "HOW SHE TELLS A COPY FROM SOMETHING THAT LOOKS LIKE ONE\n" +
        "Two screens over the same box. The first is the icon's COLOURS, coarse and fast, slid across the bag "
        + "area to propose every square that could be a copy. The second is a CLOSE-UP of the same box — four "
        + "times the detail — run only on the square she's about to click.\n\n" +
        "The second screen exists because the first one cannot do this job alone. A Talisman of Kejaar Kerrath "
        + "and a Desecrated Kejaar Totem are both brown, bone and gold in roughly the same places: averaged down "
        + "to thirty-six colours they are the same picture, which is how a sweep that had merged its last real "
        + "copy went looking for another and found a totem. A hundred and forty-four cells over the same pixels "
        + "tell them apart.\n\n" +
        "And when something does slip through — it looked right, she picked it up, the counter didn't move — she "
        + "remembers THAT PICTURE, not just that square, and never picks it up again. A square is only good "
        + "until the bags shuffle; the picture of a totem is good for the rest of the grind. The one thing she "
        + "will not learn is a picture that matches the copy's own, because that is what the item you're merging "
        + "INTO looks like, sitting in the same bag, unable to merge into itself. The page shows how many she's "
        + "learned and lets you make her forget them.\n\n" +
        "HOW SHE KNOWS A MERGE HAPPENED\n" +
        "The game writes nothing to the log about merging. The only witness is the item's own tier counter — the "
        + "\"4 / 32\" on its window — so that is read before and after every single merge, and nothing counts that "
        + "the number didn't confirm. This matters more than it sounds: without it there is no difference between "
        + "merging a hundred items and clicking a hundred empty squares, and a run that can't tell those apart "
        + "will happily do the second one all night.\n\n" +
        "Three reads in a row that fail to parse means the item window has been closed or covered, and the run "
        + "stops. Five squares in a row that looked like copies and merged nothing means the picks have moved or "
        + "the icon matches something else, and the run stops for that too.\n\n" +
        "THE CONSOLE\n" +
        "The same console the Questing card carries, showing only Auto Merge's lines. You can scroll back through "
        + "it while she's running without being yanked to the bottom every time she speaks, copy it, or save it to "
        + "a file. Turn DETAIL on and she narrates the numbers behind every decision instead of just the decision: "
        + "what each candidate scored on both screens, the bar it had to beat, where every click went, and the raw "
        + "text the OCR read before anything tried to parse it. It's verbose on purpose — turn it on when "
        + "something has gone wrong, and read it back afterwards.\n\n" +
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
