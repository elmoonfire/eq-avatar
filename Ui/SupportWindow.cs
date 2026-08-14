using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Diag;
using EQAvatar.Spike.Net;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// Report a problem, and read what the officers said back.
///
/// BUILT IN CODE, NOT XAML — the same reason <c>MainWindow.Sections.cs</c> gives. Two releases
/// (0.9.21, 0.9.25) died at startup on XAML resource lookups that were fine in the designer, and
/// a window whose whole job is to report failures is a poor place to risk one. Nothing here
/// resolves a StaticResource; every brush is a literal, so this window cannot be broken by a
/// change to App.xaml.
///
/// IT ASKS FOR TWO THINGS. A line saying what happened and a paragraph saying more. Version,
/// operating system and screen travel automatically and are shown at the bottom so nobody has to
/// wonder what was sent — this is the machine that actually broke, which is the entire advantage
/// this window has over filing the same report on the website from a phone.
/// </summary>
public sealed class SupportWindow : Window
{
    // The app's palette, as literals. See the class note on why nothing is looked up.
    private static readonly Brush Bg      = New("#101317");
    private static readonly Brush Panel   = New("#191E25");
    private static readonly Brush Line    = New("#20262F");
    private static readonly Brush Text    = New("#E6EDF3");
    private static readonly Brush Dim     = New("#9AA7B4");
    private static readonly Brush Faint   = New("#5D6878");
    private static readonly Brush Accent  = New("#4FC3F7");
    private static readonly Brush Good    = New("#7CE38B");
    private static readonly Brush Bad     = New("#FF8A80");
    private static readonly Brush Field   = New("#0C0F13");

    private readonly SupportClient _client;

    private readonly StackPanel _list = new();
    private readonly Border _right = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _footer = new();

    private List<SupportTicketRow> _rows = new();

    public SupportWindow(AppSettings settings, Window? owner = null)
    {
        _client = new SupportClient(settings);

        Title = "EQ Avatar — Support";
        Width = 940;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        Background = Bg;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen
                                              : WindowStartupLocation.CenterOwner;
        if (owner is not null) Owner = owner;
        ShowInTaskbar = false;

        Content = BuildChrome();
        ShowNewReport();

        Loaded += async (_, _) => await Refresh();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /* ------------------------------------------------------------------ chrome */

    private UIElement BuildChrome()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        /* --- title strip: drag to move, one close button ------------------- */
        var titleGrid = new Grid { Background = Panel, Height = 42 };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        heading.Children.Add(new TextBlock
        {
            Text = "\U0001F6DF  Support", Foreground = Text, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        heading.Children.Add(new TextBlock
        {
            Text = "   report a problem, read the replies", Foreground = Faint, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleGrid.Children.Add(heading);

        var close = Chip("✕", () => Close());
        close.Margin = new Thickness(0, 0, 10, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(close, 1);
        titleGrid.Children.Add(close);

        Grid.SetRow(titleGrid, 0);
        root.Children.Add(titleGrid);

        /* --- body: your reports on the left, one thing on the right -------- */
        var body = new Grid { Margin = new Thickness(0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid { Background = Panel };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var newBtn = Button("＋  Report something", ShowNewReport, primary: true);
        newBtn.Margin = new Thickness(12, 12, 12, 6);
        Grid.SetRow(newBtn, 0);
        left.Children.Add(newBtn);

        _status.Foreground = Faint;
        _status.FontSize = 11;
        _status.Margin = new Thickness(14, 2, 12, 8);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 1);
        left.Children.Add(_status);

        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };
        Grid.SetRow(listScroll, 2);
        left.Children.Add(listScroll);

        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        _right.Background = Bg;
        _right.BorderBrush = Line;
        _right.BorderThickness = new Thickness(1, 0, 0, 0);
        _right.Padding = new Thickness(20, 16, 20, 16);
        Grid.SetColumn(_right, 1);
        body.Children.Add(_right);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        /* --- footer: exactly what travels with a report -------------------- */
        var footerBar = new Border
        {
            Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
        };
        _footer.Foreground = Faint;
        _footer.FontSize = 11;
        _footer.TextWrapping = TextWrapping.Wrap;
        footerBar.Child = _footer;
        Grid.SetRow(footerBar, 2);
        root.Children.Add(footerBar);

        UpdateFooter();
        return root;
    }

    private void UpdateFooter(string? extra = null)
    {
        int pending = CrashReporter.PendingCount();
        string s = "Sent with every report: " + SupportMetrics.Summary(_client.Character, AppSettings.AppVersion);
        if (pending > 0) s += $"  ·  {pending} crash report(s) waiting to go up";
        if (!string.IsNullOrEmpty(extra)) s += "  ·  " + extra;
        _footer.Text = s;
    }

    /* ------------------------------------------------------------------ the list */

    private async Task Refresh()
    {
        _status.Text = "Loading your reports…";
        List<SupportTicketRow>? rows = await _client.List();

        _list.Children.Clear();
        if (rows is null)
        {
            // "Could not ask" and "you have none" are different facts and must not share a message.
            _status.Text = _client.Ready
                ? "Could not reach the hub just now. You can still write a report — it will be sent when you press Send."
                : "Set your character name on the Account page and your reports will show up here.";
            return;
        }

        _rows = rows;
        _status.Text = rows.Count == 0 ? "You have not reported anything yet."
                                       : $"{rows.Count} report{(rows.Count == 1 ? "" : "s")}";

        foreach (SupportTicketRow r in rows) _list.Children.Add(ListRow(r));
    }

    private UIElement ListRow(SupportTicketRow r)
    {
        var b = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        b.MouseEnter += (_, _) => b.Background = New("#12FFFFFF");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += async (_, _) => await OpenTicket(r.Id);

        var col = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = r.Glyph + "  ", FontSize = 13 });
        top.Children.Add(new TextBlock
        {
            Text = r.Title ?? "", Foreground = Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200,
        });
        col.Children.Add(top);

        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        sub.Children.Add(StatusChip(r.Status, r.StatusLabel));
        sub.Children.Add(new TextBlock
        {
            Text = $"  #{r.Id} · {r.WhenText}", Foreground = Faint, FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        col.Children.Add(sub);

        b.Child = col;
        return b;
    }

    /* ------------------------------------------------------------ the report form */

    private void ShowNewReport()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "What went wrong?", Foreground = Text, FontSize = 17, FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Officers read these. Your app version, operating system and screen size go with it "
                 + "automatically, so there is nothing to look up.",
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
        });

        panel.Children.Add(Label("Kind"));
        var kind = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(8, 6, 8, 6),
            Background = Field, Foreground = Text, BorderBrush = New("#2A3B55"), FontSize = 13,
        };
        kind.Items.Add(new ComboBoxItem { Content = "\U0001F41E  Something is broken", Tag = "bug" });
        kind.Items.Add(new ComboBoxItem { Content = "\U0001F4A5  It crashed", Tag = "crash" });
        kind.Items.Add(new ComboBoxItem { Content = "❓  A question", Tag = "question" });
        kind.Items.Add(new ComboBoxItem { Content = "\U0001F4A1  An idea", Tag = "idea" });
        kind.SelectedIndex = 0;
        panel.Children.Add(kind);

        panel.Children.Add(Label("In one line"));
        TextBox title = Input();
        title.MaxLength = 160;
        title.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(title);

        panel.Children.Add(Label("What happened"));
        TextBox body = Input();
        body.AcceptsReturn = true;
        body.TextWrapping = TextWrapping.Wrap;
        body.MinHeight = 150;
        body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        body.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(body);

        panel.Children.Add(new TextBlock
        {
            Text = "If it happens in one zone, with one role, or on one quest and not others, say so — "
                 + "that is usually the thing that finds it.",
            Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
        });

        var note = new TextBlock { Foreground = Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                                   Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        panel.Children.Add(note);

        var send = Button("Send it", () => { }, primary: true);
        send.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(send);

        send.MouseLeftButtonUp += async (_, _) =>
        {
            string t = title.Text.Trim();
            string d = body.Text.Trim();
            if (t.Length == 0) { Warn(note, "Give it a one-line summary so it can be found again."); return; }
            if (d.Length < 10) { Warn(note, "A sentence or two about what happened, please — \"it broke\" is not enough to go on."); return; }

            note.Visibility = Visibility.Collapsed;
            SetChipText(send, "Sending…");

            string k = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "bug";
            (bool ok, int id, string url, bool duplicate, string message) = await _client.OpenTicket(k, t, d);

            SetChipText(send, "Send it");
            if (!ok) { Warn(note, "That did not go: " + message); return; }

            await Refresh();
            ShowSent(id, url, duplicate);
        };

        _right.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
    }

    private void ShowSent(int id, string url, bool duplicate)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = duplicate ? "You had already sent that" : "Sent — thank you",
            Foreground = Good, FontSize = 18, FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = duplicate
                ? $"An identical report from the last couple of minutes is already on the queue as #{id}, so this "
                  + "one was folded into it rather than filed twice."
                : $"It is with the officers as report #{id}. Any reply appears in the list on the left, and here.",
            Foreground = Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var open = Button("Open report #" + id, async () => await OpenTicket(id));
        row.Children.Add(open);
        if (url.Length > 0)
        {
            var web = Button("View on the hub", () => OpenUrl(url));
            web.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(web);
        }
        var again = Button("Report something else", ShowNewReport);
        again.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(again);
        panel.Children.Add(row);

        _right.Child = panel;
        UpdateFooter();
    }

    /* ------------------------------------------------------------------ a thread */

    private async Task OpenTicket(int id)
    {
        _right.Child = new TextBlock { Text = "Loading…", Foreground = Dim, FontSize = 13 };
        SupportTicket? t = await _client.Get(id);
        if (t is null)
        {
            _right.Child = new TextBlock
            {
                Text = "That report could not be loaded. Either the hub is unreachable, or it is not one of yours.",
                Foreground = Dim, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            };
            return;
        }

        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = t.Title ?? "", Foreground = Text, FontSize = 17, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 14) };
        meta.Children.Add(StatusChip(t.Status, t.StatusLabel));
        meta.Children.Add(new TextBlock
        {
            Text = $"  #{t.Id} · opened {DateTimeOffset.FromUnixTimeSeconds(t.Created).LocalDateTime:MMM d, HH:mm}"
                 + $" · v{t.AppVersion} · {t.Os}",
            Foreground = Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(meta);

        panel.Children.Add(Bubble("You", t.Body ?? "", t.Created, staff: false));

        foreach (SupportMessage m in t.Messages)
            panel.Children.Add(Bubble(m.Author ?? "", m.Body ?? "", m.Created, m.Staff));

        if (t.Messages.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No replies yet. Officers pick these up between raids, not instantly.",
                Foreground = Faint, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 12),
            });
        }

        if (!t.Done)
        {
            panel.Children.Add(Label("Add to this"));
            TextBox reply = Input();
            reply.AcceptsReturn = true;
            reply.TextWrapping = TextWrapping.Wrap;
            reply.MinHeight = 90;
            reply.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(reply);

            var note = new TextBlock { Foreground = Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                                       Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            panel.Children.Add(note);

            var sendReply = Button("Send", () => { }, primary: true);
            sendReply.HorizontalAlignment = HorizontalAlignment.Left;
            sendReply.MouseLeftButtonUp += async (_, _) =>
            {
                string body = reply.Text.Trim();
                if (body.Length == 0) { Warn(note, "Nothing to send."); return; }
                SetChipText(sendReply, "Sending…");
                (bool ok, string message) = await _client.Reply(t.Id, body);
                SetChipText(sendReply, "Send");
                if (!ok) { Warn(note, "That did not go: " + message); return; }
                await OpenTicket(t.Id);
                await Refresh();
            };
            panel.Children.Add(sendReply);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "This one is closed. If it comes back, start a new report rather than replying here — "
                     + "a fresh report captures a fresh set of numbers.",
                Foreground = Faint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        }

        _right.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
    }

    private UIElement Bubble(string author, string text, long created, bool staff)
    {
        var b = new Border
        {
            BorderBrush = staff ? Accent : Line,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 8),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = author + (staff ? "  · officer" : "")
                 + "  · " + DateTimeOffset.FromUnixTimeSeconds(created).LocalDateTime.ToString("MMM d, HH:mm"),
            Foreground = staff ? Accent : Faint, FontSize = 11,
        });
        col.Children.Add(new TextBlock
        {
            Text = text, Foreground = New("#C7D3DF"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0), LineHeight = 18,
        });
        b.Child = col;
        return b;
    }

    /* ------------------------------------------------------------------ furniture */

    private static SolidColorBrush New(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static TextBlock Label(string s) => new()
    {
        Text = s.ToUpperInvariant(), Foreground = New("#9AA7B4"), FontSize = 10.5,
        Margin = new Thickness(0, 0, 0, 5),
    };

    private static TextBox Input() => new()
    {
        Background = New("#0C0F13"),
        Foreground = New("#E6EDF3"),
        CaretBrush = New("#E6EDF3"),
        BorderBrush = New("#2A3B55"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 7, 9, 7),
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    /// <summary>A clickable chip. Buttons are borders rather than <c>Button</c>s so nothing here
    /// depends on the app's Button template — see the class note.</summary>
    private static Border Chip(string text, Action onClick)
    {
        var b = new Border
        {
            Background = New("#12FFFFFF"),
            BorderBrush = New("#2F3A4B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(11, 5, 11, 5),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, Foreground = New("#CFE0EE"), FontSize = 12 },
        };
        b.MouseLeftButtonUp += (_, _) => onClick();
        b.MouseEnter += (_, _) => b.BorderBrush = New("#4FC3F7");
        b.MouseLeave += (_, _) => b.BorderBrush = New("#2F3A4B");
        return b;
    }

    private static Border Button(string text, Action onClick, bool primary = false)
    {
        Border b = Chip(text, onClick);
        b.Padding = new Thickness(16, 8, 16, 8);
        if (primary)
        {
            b.Background = New("#1C4A5E");
            b.BorderBrush = New("#4FC3F7");
            if (b.Child is TextBlock tb) { tb.Foreground = New("#EAF6FF"); tb.FontWeight = FontWeights.SemiBold; }
        }
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (b.Child is TextBlock t2) t2.HorizontalAlignment = HorizontalAlignment.Center;
        return b;
    }

    private static void SetChipText(Border chip, string text)
    {
        if (chip.Child is TextBlock tb) tb.Text = text;
    }

    private static void Warn(TextBlock note, string message)
    {
        note.Text = message;
        note.Visibility = Visibility.Visible;
    }

    /// <summary>The hub's own status colours, so a ticket reads the same here and on the website.</summary>
    private static UIElement StatusChip(string? status, string? label)
    {
        string hex = status switch
        {
            "new" => "#4FC3F7",
            "ack" => "#7CE38B",
            "open" => "#FFB74D",
            "waiting" => "#C89AE6",
            "resolved" => "#7CE38B",
            "closed" => "#8FA0B2",
            _ => "#8FA0B2",
        };
        return new Border
        {
            // #1A prefix = the same colour at 10% alpha, which is how the hub tints its chips.
            Background = New("#1A" + hex[1..]),
            BorderBrush = New(hex),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 1, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label ?? status ?? "", Foreground = New(hex), FontSize = 10.5, FontWeight = FontWeights.Bold,
            },
        };
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { BotLog.Log("support", "could not open " + url + ": " + ex.Message); }
    }
}
