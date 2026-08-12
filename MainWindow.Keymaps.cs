using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ocr;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The Key Mappings page (partial class): the bot's mirror of the game's Controls → Key binds
/// screen. One button OCRs the visible rows out of the game window; scroll in-game, capture
/// again, and the passes merge. The stamp shows when it was last refreshed, and the ACTIONS
/// column of the Action Sequencer resolves through these mappings.
/// </summary>
public partial class MainWindow
{
    private bool _kmInit;
    private bool _kmBusy;

    private void InitKeymapsUi()
    {
        if (!_kmInit)
        {
            _kmInit = true;
            ArtCache.Bind(ArtKeymapsBanner, "ui-keymaps-banner.jpg");
        }
        RenderKeymaps();
    }

    // ---------------- capture ----------------

    private async void KmCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_kmBusy) return;
        IntPtr hwnd = _grindTarget;
        if (hwnd == IntPtr.Zero && Input.WindowFinder.GuessEverQuest() is { } w) hwnd = w.Handle;
        if (hwnd == IntPtr.Zero)
        {
            KmStatus.Text = "game not found — launch EQL first (any page can target it)";
            ShowToast("Launch EQ Legends first");
            return;
        }
        _kmBusy = true;
        try
        {
            KmStatus.Text = "reading the game window…  (keep the Key binds list visible, not covered)";
            var found = await KeybindReader.ReadAsync(hwnd);
            if (found.Count == 0)
            {
                KmStatus.Text = "no key binds recognized — is Controls → Key binds open and fully visible in-game?";
                Diag.BotLog.Log("keymap", "capture: 0 rows recognized");
                return;
            }
            var (added, updated) = KeyMapStore.Current.Merge(found);
            KmStatus.Text = $"captured {found.Count} row(s) — {added} new, {updated} updated. Scroll the in-game list and capture again until everything's in.";
            Diag.BotLog.Log("keymap", $"capture: {found.Count} rows, {added} new, {updated} updated (total {KeyMapStore.Current.Binds.Count})");
            RenderKeymaps();
        }
        catch (Exception ex)
        {
            KmStatus.Text = "capture failed: " + ex.Message;
            Diag.BotLog.Log("keymap", "capture error: " + ex);
        }
        finally { _kmBusy = false; }
    }

    private void KmAdd_Click(object sender, RoutedEventArgs e)
    {
        string action = KmAddAction.Text.Trim();
        if (action.Length == 0) { ShowToast("Type the action name first"); return; }
        KeyMapStore.Current.Merge(new[]
        {
            new KeyBind { Action = action, Primary = KmAddPrimary.Text.Trim(), Alternate = KmAddAlt.Text.Trim() },
        }, stamp: false);
        KmAddAction.Clear(); KmAddPrimary.Clear(); KmAddAlt.Clear();
        RenderKeymaps();
    }

    private void KmClear_Click(object sender, RoutedEventArgs e)
    {
        KeyMapStore.Current.Binds.Clear();
        KeyMapStore.Current.LastRefreshed = null;
        KeyMapStore.Current.Save();
        RenderKeymaps();
        ShowToast("All key mappings cleared");
    }

    private void KmFilter_Changed(object sender, TextChangedEventArgs e) => RenderKeymaps();

    // ---------------- rendering ----------------

    private void RenderKeymaps()
    {
        if (KmListHost is null) return;
        var store = KeyMapStore.Current;

        // stamp chip
        if (store.LastRefreshed is { } ts)
        {
            KmStampBorder.Background = Hex("#10281A");
            KmStampBorder.BorderBrush = Hex("#2E7D4F");
            KmStampText.Foreground = Hex("#9FE0B8");
            KmStampText.Text = "refreshed " + (ts.Date == DateTime.Today ? $"today {ts:HH:mm}" : ts.ToString("MMM d, HH:mm"));
        }
        else
        {
            KmStampBorder.Background = Hex("#2A2410");
            KmStampBorder.BorderBrush = Hex("#7A6320");
            KmStampText.Foreground = Hex("#FFE1A6");
            KmStampText.Text = "never captured";
        }

        KmListHost.Children.Clear();
        string f = KmFilter?.Text.Trim() ?? "";
        var binds = store.Binds
            .Where(b => f.Length == 0
                        || b.Action.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                        || b.Primary.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                        || b.Alternate.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                        || b.Category.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        KmCountText.Text = store.Binds.Count == 0
            ? "no mappings yet"
            : $"{store.Binds.Count} mapping{(store.Binds.Count == 1 ? "" : "s")}" + (f.Length > 0 ? $" · {binds.Count} shown" : "") + " — these power the ACTIONS pills in the Sequencer";

        if (binds.Count == 0)
        {
            KmListHost.Children.Add(new TextBlock
            {
                Text = store.Binds.Count == 0
                    ? "Nothing here yet. In EQL open Options → Controls → Key binds, keep that window visible, and press ◉ Capture. Scroll the in-game list and capture again — every pass merges."
                    : "nothing matches the filter",
                Foreground = Hex("#7E93A8"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 8),
            });
            return;
        }

        string lastCat = "";
        foreach (var b in binds)
        {
            if (b.Category != lastCat && b.Category.Length > 0)
            {
                KmListHost.Children.Add(new TextBlock
                {
                    Text = b.Category.ToUpperInvariant(),
                    FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = Hex("#5E7C9A"),
                    Margin = new Thickness(4, 10, 0, 3),
                });
            }
            lastCat = b.Category;
            KmListHost.Children.Add(MakeKeymapRow(b));
        }
    }

    private FrameworkElement MakeKeymapRow(KeyBind b)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        var name = new TextBlock
        {
            Text = b.Action,
            Foreground = Hex("#DDE7F0"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 8, 0),
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        FrameworkElement KeyPill(string key, bool primary)
        {
            if (key.Length == 0)
                return new TextBlock { Text = "—", Foreground = Hex("#3C4C60"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = primary ? Hex("#14293D") : Hex("#101B29"),
                BorderBrush = primary ? Hex("#2E6E96") : Hex("#22364A"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 2, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, Foreground = primary ? Hex("#9FE0FF") : Hex("#8FA9C4"), FontSize = 11.5, FontFamily = new FontFamily("Consolas") },
                ToolTip = primary ? "primary key" : "alternate key",
            };
        }
        var p = KeyPill(b.Primary, true); Grid.SetColumn(p, 1); grid.Children.Add(p);
        var a = KeyPill(b.Alternate, false); Grid.SetColumn(a, 2); grid.Children.Add(a);

        var del = new TextBlock
        {
            Text = "✕", FontSize = 10.5, Foreground = Hex("#4E6076"), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove this mapping (a bad OCR read, for example)",
        };
        del.MouseLeftButtonUp += (_, _) =>
        {
            KeyMapStore.Current.Binds.Remove(b);
            KeyMapStore.Current.Save();
            RenderKeymaps();
        };
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Hex("#121926"),
            BorderBrush = Hex("#1C2C3E"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 4, 4, 4),
            Child = grid,
        };
    }

    private void KmInfo_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hex("#C6D2DE"),
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(18),
            Text = KeymapsInfoText,
        };
        var win = new Window
        {
            Title = "How Key Mappings work",
            Owner = this,
            Width = 620, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string KeymapsInfoText =
@"WHAT THIS IS
The bot's own copy of your game's Controls → Key binds screen. When a sequence says 'Jump' or 'Open inventory', the engine looks the action up HERE to learn which key to press — so the bot always presses what YOUR game actually has bound.

CAPTURING
1. In EQ Legends open Options → Controls → Key binds.
2. Keep that window fully visible on screen (the bot reads pixels — nothing can cover it).
3. Press ◉ Capture from game. The visible rows are read with OCR and appear below.
4. Scroll the in-game list, capture again, repeat until everything's in — every pass MERGES: new actions are added, changed keys are updated, nothing is duplicated.

The stamp at the top shows when the mappings were last refreshed — recapture after you rebind keys in-game.

FIXING READS
OCR isn't perfect. Remove a bad row with ✕ and add the correct one by hand in the add row (action · primary · alternate). Hand-added rows merge by action name just like captured ones.

WHERE IT'S USED
The ACTIONS column of the Action Sequencer lists every mapped action automatically, and each action pill's tooltip shows the key it resolves to. Actions without a mapping still work as pills — they just can't fire until a capture (or a manual row) gives them a key.";
}
