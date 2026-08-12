using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// screen. AUTO-CAPTURE drives the whole thing hands-free — it brings the game forward, reads
/// the visible binds, scrolls the list itself and repeats until nothing new appears. Every
/// column filters as you type, any bind can be LOCKED against imports, and the whole set can
/// be published to the member hub for friends to view and copy.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [StructLayout(LayoutKind.Sequential)] private struct KMRECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out KMRECT r);
    private const int SW_RESTORE = 9;

    private static readonly HttpClient KmHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private bool _kmInit;
    private bool _kmBusy;
    private CancellationTokenSource? _kmAuto;

    private void InitKeymapsUi()
    {
        if (!_kmInit)
        {
            _kmInit = true;
            ArtCache.Bind(ArtKeymapsBanner, "ui-keymaps-banner.jpg");
            ArtCache.Bind(ArtKmColAction, "ui-km-col-action.jpg");
            ArtCache.Bind(ArtKmColPrimary, "ui-km-col-primary.jpg");
            ArtCache.Bind(ArtKmColSecondary, "ui-km-col-secondary.jpg");
            ArtCache.Bind(ArtKmColLock, "ui-km-col-lock.jpg");
        }
        RenderKeymaps();
    }

    // ---------------- finding the game ----------------

    private IntPtr ResolveGameWindow()
    {
        if (_grindTarget != IntPtr.Zero) return _grindTarget;
        return WindowFinder.GuessEverQuest() is { } w ? w.Handle : IntPtr.Zero;
    }

    private static void BringToFront(IntPtr hwnd)
    {
        try { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); } catch { }
    }

    // ---------------- capture: one visible page ----------------

    private async void KmCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_kmBusy || _kmAuto != null) return;
        IntPtr hwnd = ResolveGameWindow();
        if (hwnd == IntPtr.Zero) { KmStatus.Text = "game not found — launch EQL first."; ShowToast("Launch EQ Legends first"); return; }
        _kmBusy = true;
        try
        {
            BringToFront(hwnd);
            await Task.Delay(500);
            KmStatus.Text = "reading the game window…";
            var page = await KeybindReader.ReadPageAsync(hwnd);
            if (page.Binds.Count == 0)
            {
                KmStatus.Text = "no key binds recognized — is Controls → Key binds open and fully visible in-game?";
                Diag.BotLog.Log("keymap", "capture: 0 rows recognized");
                return;
            }
            var (added, updated) = KeyMapStore.Current.Merge(page.Binds);
            KmStatus.Text = $"captured {page.Binds.Count} row(s) — {added} new, {updated} updated.";
            Diag.BotLog.Log("keymap", $"capture: {page.Binds.Count} rows, {added} new, {updated} updated");
            RenderKeymaps();
        }
        catch (Exception ex)
        {
            KmStatus.Text = "capture failed: " + ex.Message;
            Diag.BotLog.Log("keymap", "capture error: " + ex);
        }
        finally { _kmBusy = false; }
    }

    // ---------------- capture: the whole list, scrolling itself ----------------

    private async void KmAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_kmAuto is not null) { _kmAuto.Cancel(); return; }      // the button doubles as Stop
        if (_kmBusy) return;

        IntPtr hwnd = ResolveGameWindow();
        if (hwnd == IntPtr.Zero) { KmStatus.Text = "game not found — launch EQL first."; ShowToast("Launch EQ Legends first"); return; }

        _kmAuto = new CancellationTokenSource();
        var ct = _kmAuto.Token;
        KmAutoBtn.Content = "■  Stop";
        var (cx, cy) = HumanizedMouse.CursorPos();          // put the pointer back where it was afterwards
        int totalNew = 0, totalUpd = 0, pass = 0;

        try
        {
            BringToFront(hwnd);
            await Task.Delay(600, ct);

            // Park over the list and rewind to the very top so we always start from row one.
            var (wx, wy) = WindowCentre(hwnd);
            HumanizedMouse.MoveInstant(wx, wy);
            KmStatus.Text = "scrolling to the top of the list…";
            HumanizedMouse.Scroll(30);
            await Task.Delay(600, ct);

            int dry = 0;
            while (!ct.IsCancellationRequested && pass < 90 && dry < 3)
            {
                pass++;
                var page = await KeybindReader.ReadPageAsync(hwnd);
                if (page.Binds.Count == 0 && pass == 1)
                {
                    KmStatus.Text = "nothing readable — open Options → Controls → Key binds (category ALL) and keep it visible, then try again.";
                    Diag.BotLog.Log("keymap", "auto: first pass saw 0 rows — aborting");
                    return;
                }
                var (added, updated) = KeyMapStore.Current.Merge(page.Binds);
                totalNew += added; totalUpd += updated;
                dry = added == 0 ? dry + 1 : 0;

                KmStatus.Text = $"pass {pass} — {KeyMapStore.Current.Binds.Count} binds so far ({totalNew} new)…  press Stop any time";
                RenderKeymaps();

                // scroll down over the list itself (falls back to the window centre)
                var (px, py) = page.HasRegion ? page.Center : WindowCentre(hwnd);
                HumanizedMouse.MoveInstant(px, py);
                HumanizedMouse.Scroll(-3);
                await Task.Delay(430, ct);
            }

            KmStatus.Text = ct.IsCancellationRequested
                ? $"stopped after {pass} pass(es) — {KeyMapStore.Current.Binds.Count} binds captured ({totalNew} new)."
                : $"done — swept {pass} pass(es), {KeyMapStore.Current.Binds.Count} binds total ({totalNew} new, {totalUpd} updated). Check a few rows, then Share ↗ them.";
            Diag.BotLog.Log("keymap", $"auto-capture: {pass} passes, {totalNew} new, {totalUpd} updated, total {KeyMapStore.Current.Binds.Count}");
        }
        catch (OperationCanceledException)
        {
            KmStatus.Text = $"stopped — {KeyMapStore.Current.Binds.Count} binds captured.";
        }
        catch (Exception ex)
        {
            KmStatus.Text = "auto-capture failed: " + ex.Message;
            Diag.BotLog.Log("keymap", "auto-capture error: " + ex);
        }
        finally
        {
            HumanizedMouse.MoveInstant(cx, cy);
            _kmAuto?.Dispose();
            _kmAuto = null;
            KmAutoBtn.Content = "⟳  Auto-capture everything";
            RenderKeymaps();
        }
    }

    private static (int X, int Y) WindowCentre(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out KMRECT r)) return (600, 400);
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    // ---------------- publish to the member hub ----------------

    private async void KmShare_Click(object sender, RoutedEventArgs e)
    {
        string user = (_settings.HubUsername ?? "").Trim();
        if (user.Length == 0) { ShowToast("Set your check-in name on the Licensing page first"); return; }
        if (KeyMapStore.Current.Binds.Count == 0) { ShowToast("Capture your key binds first"); return; }

        KmStatus.Text = "publishing to your member page…";
        try
        {
            string url = _settings.HubUrl;
            int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
            string apiBase = (i >= 0 ? url[..i] : url.TrimEnd('/') + "/") + "api/keymaps.php";

            using var req = new HttpRequestMessage(HttpMethod.Post, apiBase)
            {
                Content = new StringContent(KeyMapStore.Current.ToShareJson(user), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-API-KEY", _settings.HubApiKey);
            using var resp = await KmHttp.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && body.Contains("\"ok\":true"))
            {
                KmStatus.Text = $"published {KeyMapStore.Current.Binds.Count} binds — they're on your Key Mappings page on the website now (share it from your account page).";
                ShowToast("Key mappings published");
            }
            else
            {
                KmStatus.Text = $"publish failed ({(int)resp.StatusCode}): {body.Trim()}";
            }
            Diag.BotLog.Log("keymap", $"publish → {(int)resp.StatusCode} {body.Trim()}");
        }
        catch (Exception ex)
        {
            KmStatus.Text = "publish failed: " + ex.Message;
            Diag.BotLog.Log("keymap", "publish error: " + ex);
        }
    }

    // ---------------- manual add / clear / filters ----------------

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
    private void KmFilter_Click(object sender, RoutedEventArgs e) => RenderKeymaps();

    // ---------------- rendering ----------------

    /// <summary>Matches a filter box against a cell. An em dash / hyphen means "unbound".</summary>
    private static bool CellMatches(string cell, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        if (filter is "-" or "—") return cell.Trim().Length == 0;
        return cell.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RenderKeymaps()
    {
        if (KmListHost is null) return;
        var store = KeyMapStore.Current;

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
        string fa = KmFilterAction?.Text ?? "", fp = KmFilterPrimary?.Text ?? "", fs = KmFilterAlt?.Text ?? "";
        bool lockedOnly = KmLockedOnly?.IsChecked == true;
        bool filtering = fa.Trim().Length + fp.Trim().Length + fs.Trim().Length > 0 || lockedOnly;

        var binds = store.Binds
            .Where(b => CellMatches(b.Action, fa) || (fa.Trim().Length > 0 && CellMatches(b.Category, fa)))
            .Where(b => CellMatches(b.Primary, fp))
            .Where(b => CellMatches(b.Alternate, fs))
            .Where(b => !lockedOnly || b.Locked)
            .ToList();

        int locks = store.Binds.Count(b => b.Locked);
        KmCountText.Text = store.Binds.Count == 0
            ? "no mappings yet"
            : $"{store.Binds.Count} mapping{(store.Binds.Count == 1 ? "" : "s")}"
              + (filtering ? $" · {binds.Count} shown" : "")
              + (locks > 0 ? $" · {locks} locked" : "")
              + " — these power the ACTIONS pills in the Sequencer";

        if (binds.Count == 0)
        {
            KmListHost.Children.Add(new TextBlock
            {
                Text = store.Binds.Count == 0
                    ? "Nothing here yet. In EQL open Options → Controls → Key binds, set the category to ALL, then press ⟳ Auto-capture everything — the bot scrolls and reads the whole list itself."
                    : "nothing matches these filters",
                Foreground = Hex("#7E93A8"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 8),
            });
            return;
        }

        string lastCat = " ";
        foreach (var b in binds)
        {
            if (!string.Equals(b.Category, lastCat, StringComparison.Ordinal) && b.Category.Length > 0)
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        var name = new TextBlock
        {
            Text = b.Action,
            Foreground = Hex("#DDE7F0"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 8, 0),
            ToolTip = b.Category.Length > 0 ? $"{b.Category} · {b.Action}" : b.Action,
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
                Background = primary ? Hex("#14293D") : Hex("#1B1730"),
                BorderBrush = primary ? Hex("#2E6E96") : Hex("#4C3E77"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 2, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, Foreground = primary ? Hex("#9FE0FF") : Hex("#C792EA"), FontSize = 11.5, FontFamily = new FontFamily("Consolas") },
                ToolTip = primary ? "primary key" : "secondary key",
            };
        }
        var p = KeyPill(b.Primary, true); Grid.SetColumn(p, 1); grid.Children.Add(p);
        var a = KeyPill(b.Alternate, false); Grid.SetColumn(a, 2); grid.Children.Add(a);

        // ---- lock toggle: protects this bind when importing someone else's mappings ----
        var lockGlyph = new TextBlock
        {
            Text = b.Locked ? "🔒" : "🔓",
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = b.Locked ? 1.0 : 0.32,
        };
        var lockBtn = new Border
        {
            CornerRadius = new CornerRadius(999),
            Width = 30, Height = 24,
            Background = b.Locked ? Hex("#2A2410") : Brushes.Transparent,
            BorderBrush = b.Locked ? Hex("#7A6320") : Hex("#22364A"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = lockGlyph,
            ToolTip = b.Locked
                ? "LOCKED — importing a friend's mappings will leave this bind alone. Click to unlock."
                : "Click to lock this bind: imports from other members will never overwrite it.",
        };
        lockBtn.MouseLeftButtonUp += (_, _) =>
        {
            b.Locked = !b.Locked;
            KeyMapStore.Current.Save();
            RenderKeymaps();
        };
        Grid.SetColumn(lockBtn, 3);
        grid.Children.Add(lockBtn);

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
        Grid.SetColumn(del, 4);
        grid.Children.Add(del);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = b.Locked ? Hex("#171A1E") : Hex("#121926"),
            BorderBrush = b.Locked ? Hex("#4A3E22") : Hex("#1C2C3E"),
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
            Width = 640, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string KeymapsInfoText =
@"WHAT THIS IS
The bot's own copy of your game's Controls → Key binds screen. When a sequence says 'Jump' or 'Open inventory', the engine looks the action up HERE to learn which key to press — so the bot always presses what YOUR game actually has bound.

AUTO-CAPTURE (the easy way)
1. In EQ Legends open Options → Controls → Key binds and set the category to ALL.
2. Leave that window visible and press ⟳ Auto-capture everything.
The bot brings the game forward, rewinds the list to the top, reads what it can see, scrolls the list a few notches, reads again — and keeps going until three passes in a row turn up nothing new. Press Stop whenever you like; everything captured so far is already saved. Your mouse pointer goes back where it was when it finishes.

Use ◉ Capture page for a single visible screen — handy for topping up one stubborn section.

FILTERING
Every column has its own filter and they combine: type 'target' under ACTION and 'mouse' under PRIMARY to see every targeting action bound to a mouse button. Type '-' in a key column to list actions with NOTHING bound there. The LOCK column has an 'only' box to show just your locked binds.

LOCKS
Click the padlock on any row to lock that bind. Locked binds are yours: when you import another member's mappings, everything else may change to match theirs, but locked rows are left exactly as they are. Locks are personal — they are never shared or published.

SHARING
Share ↗ publishes these mappings to your member page on the website, where friends can browse and search them with the same instant filters — and copy them into their own game. Turn sharing on (and get your link) from your account page, exactly like the armory.

FIXING READS
OCR isn't perfect. Remove a bad row with ✕ and add the correct one by hand in the add row. Hand-added rows merge by action name just like captured ones.";
}
