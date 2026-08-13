using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EQAvatar.Spike;

/// <summary>
/// The left rail as collapsible sections, plus a summary dashboard behind each section heading
/// (partial class).
///
/// WHY THIS IS CODE AND NOT XAML. Every element here could have been written in
/// <c>MainWindow.xaml</c>, and it would have been shorter. It isn't, for two reasons that have
/// already cost this project releases: that file is shared with the Key Mappings workstream, and
/// two releases (0.9.21, 0.9.25) died at startup on XAML resources that resolved fine in the
/// designer and not at runtime. Built here, the rail is reshaped from the elements the XAML
/// already created — the same RadioButtons, the same styles, looked up by name — so a mistake
/// shows up as a rail that didn't reorganise, not as an app that won't open. The whole build is
/// wrapped in a try/catch that leaves the original rail untouched if anything goes wrong.
///
/// This file is SELF-CONTAINED on purpose: it edits neither <c>MainWindow.xaml</c> nor
/// <c>MainWindow.xaml.cs</c>. It hooks itself up with a class handler for Loaded (see the static
/// constructor), and it shows and hides its own dashboards rather than being added to the
/// <c>Panels</c> list, so two chats can work on this app at once without touching the same lines.
///
/// WHAT CHANGES. The five flat captions (COMMAND, INSIGHT, GAME DATA, DEBUG, ACCOUNT) become
/// clickable headings. Clicking a heading opens that section's dashboard; clicking the chevron on
/// its right collapses or expands the pages under it, animated, and the collapsed set is
/// remembered between runs. "Game Data" is promoted out of INSIGHT into its own section, because
/// it is no longer one page — it is Mobs, Raid Targets, Plane of Sky and the Hunting Guide in the
/// app, and eight more catalogs on the hub.
/// </summary>
public partial class MainWindow
{
    private sealed class NavSection
    {
        public string Title = "", Glyph = "", Panel = "", Blurb = "";
        public Border Header = null!;
        public Border Body = null!;
        public StackPanel Items = null!;
        public TextBlock Chevron = null!, Caption = null!, HeadGlyph = null!;
        /// <summary>The left half: glyph + caption, opens the section's page.</summary>
        public Border LabelHit = null!;
        /// <summary>The right half: the chip around the chevron, collapses and expands.</summary>
        public Border ChevronChip = null!;
        /// <summary>What the caption and glyph go back to when the mouse leaves — depends on
        /// whether this section is the current one, so hover can't strand them the wrong colour.</summary>
        public Brush CaptionRest = Brushes.Gray, GlyphRest = Brushes.Gray;
        public bool Expanded = true;
        public readonly List<RadioButton> Buttons = new();
        public NavPage[] Pages = Array.Empty<NavPage>();
        public NavPage[] HubPages = Array.Empty<NavPage>();
        /// <summary>The dashboard behind the heading, built on first open. Null for Command,
        /// whose dashboard is the Command Center the XAML already provides.</summary>
        public UIElement? Dashboard;
    }

    /// <summary>One card on a section dashboard. Exactly one of <paramref name="Nav"/> (a nav
    /// button in this app) or <paramref name="HubPage"/> (a page on the web hub) is set.</summary>
    private readonly record struct NavPage(string Glyph, string Title, string Blurb,
                                           RadioButton? Nav = null, string? HubPage = null);

    private readonly List<NavSection> _navSections = new();
    private bool _navBuilt;
    private string _activeSectionPanel = "";

    private static readonly FontFamily Mdl2 = new("Segoe MDL2 Assets");
    private static string NavStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "nav.json");

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Wire the rail build to the window coming up, without editing the constructor in
    /// <c>MainWindow.xaml.cs</c>. A class handler fires for every MainWindow that loads; the
    /// dispatcher hop puts the work after every instance Loaded handler has run, so the rail is
    /// reshaped against a window that is fully initialised.
    /// </summary>
    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), LoadedEvent, new RoutedEventHandler(
            (s, _) =>
            {
                if (s is not MainWindow w) return;
                w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(w.BuildNavSections));
            }));
    }

    /// <summary>
    /// Reshape the rail. Safe to call more than once; only the first call does work.
    ///
    /// The rail's original children are kept until the new one is standing, so a failure here is a
    /// rail that looks exactly like 0.9.41 rather than an empty strip down the side of the window.
    /// </summary>
    private void BuildNavSections()
    {
        if (_navBuilt) return;
        _navBuilt = true;
        StackPanel? rail = NavHome.Parent as StackPanel;
        UIElement[] original = rail is null ? Array.Empty<UIElement>()
                                            : rail.Children.Cast<UIElement>().ToArray();
        try { BuildNavSectionsCore(); }
        catch (Exception ex)
        {
            try
            {
                if (rail is not null && !rail.Children.Contains(NavHome))
                {
                    rail.Children.Clear();
                    foreach (UIElement el in original)
                    {
                        // Some may already have been re-homed into a half-built section.
                        if (el is FrameworkElement fe && fe.Parent is Panel p) p.Children.Remove(el);
                        rail.Children.Add(el);
                    }
                    _navSections.Clear();
                }
                Diag.BotLog.Log("nav", "sections not built: " + ex);
            }
            catch { /* nothing left to try */ }
        }
    }

    private void BuildNavSectionsCore()
    {
        if (NavHome.Parent is not StackPanel rail) return;

        // Game Data grows three siblings that drive the same panel's tabs. NavData itself stays —
        // MainWindow.ArtUi.cs checks it directly — it just becomes the "Mobs" page it always was.
        RelabelNav(NavData, "Mobs");
        NavData.Checked += (_, _) => SelectDataTab("Mobs");
        RadioButton navRaid = MakeNavItem("", "Raid Targets", "Raid");
        RadioButton navSky = MakeNavItem("", "Plane of Sky", "Sky");
        RadioButton navGuide = MakeNavItem("", "Hunting Guide", "Guide");

        var defs = new (string Title, string Glyph, string Panel, string Blurb, RadioButton[] Items)[]
        {
            ("Command", "", "PanelHome",
             "Everything that makes the character move on its own.",
             new[] { NavHome, NavGrind, NavFollower, NavSequencer, NavMouse }),

            ("Insight", "", "PanelSecInsight",
             "What actually happened — parsed out of the game's own log and your recorded runs.",
             new[] { NavCombat, NavMaps, NavKeymaps, NavSessions, NavLog }),

            ("Game Data", "", "PanelSecGameData",
             "The world's reference data: who drops what, where they live, and what the gear does.",
             new[] { NavData, navRaid, navSky, navGuide }),

            ("Debug", "", "PanelSecDebug",
             "Watch the plumbing: what the app sends, and what the game receives.",
             new[] { NavInput, NavLogin }),

            ("Account", "", "PanelSecAccount",
             "Your character, your subscription, and how this app behaves.",
             new[] { NavProfile, NavLicensing, NavSettings }),
        };

        HashSet<string> collapsed = LoadCollapsed();

        // The buttons are still the rail's logical children; WPF refuses to reparent a live child,
        // so the rail is emptied before anything is re-homed into a section.
        rail.Children.Clear();

        foreach ((string title, string glyph, string panel, string blurb, RadioButton[] items) in defs)
        {
            var sec = new NavSection { Title = title, Glyph = glyph, Panel = panel, Blurb = blurb };
            sec.Buttons.AddRange(items);

            sec.Items = new StackPanel();
            foreach (RadioButton rb in items)
            {
                sec.Items.Children.Add(rb);
                rb.Checked += (_, _) => OnNavItemChecked(sec);
            }
            sec.Body = new Border { Child = sec.Items, ClipToBounds = true };
            sec.Header = BuildSectionHeader(sec);

            rail.Children.Add(sec.Header);
            rail.Children.Add(sec.Body);
            _navSections.Add(sec);
        }

        foreach (NavSection sec in _navSections)
            SetSectionExpanded(sec, !collapsed.Contains(sec.Title), animate: false);

        // Whatever page is showing at startup, open its section so the user can see where they are.
        NavSection? current = _navSections.FirstOrDefault(s => s.Buttons.Any(b => b.IsChecked == true));
        if (current is not null) OnNavItemChecked(current);
    }

    /// <summary>
    /// A section heading is TWO controls sharing a line: the label opens the section's page, the
    /// chevron collapses it. That is easy to miss when both live under one flat caption, so each
    /// gets its own hit area and its own hover — mousing the label lights the label, mousing the
    /// chevron lights a small button-shaped chip on the right. The chip carries a faint fill and
    /// border even at rest, because a control that only appears once you are already on it cannot
    /// tell you it is there.
    /// </summary>
    private Border BuildSectionHeader(NavSection sec)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        sec.CaptionRest = Hex("#5D6878");
        sec.GlyphRest = Hex("#6E7C8C");

        sec.HeadGlyph = new TextBlock
        {
            Text = sec.Glyph, FontFamily = Mdl2, FontSize = 11, Width = 22,
            VerticalAlignment = VerticalAlignment.Center, Foreground = sec.GlyphRest
        };
        sec.Caption = new TextBlock
        {
            Text = sec.Title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Foreground = sec.CaptionRest
        };
        sec.Chevron = new TextBlock
        {
            Text = "", FontFamily = Mdl2, FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hex("#8B99AA"),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(0)
        };

        // ---- right half: the collapse/expand chip
        sec.ChevronChip = new Border
        {
            Child = sec.Chevron, Width = 24, Height = 20, CornerRadius = new CornerRadius(5),
            Background = Hex("#0EFFFFFF"), BorderBrush = Hex("#28FFFFFF"), BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        sec.ChevronChip.MouseEnter += (_, _) =>
        {
            sec.ChevronChip.Background = Hex("#284FC3F7");
            sec.ChevronChip.BorderBrush = Hex("#4FC3F7");
            sec.Chevron.Foreground = Hex("#EAF6FF");
        };
        sec.ChevronChip.MouseLeave += (_, _) =>
        {
            sec.ChevronChip.Background = Hex("#0EFFFFFF");
            sec.ChevronChip.BorderBrush = Hex("#28FFFFFF");
            sec.Chevron.Foreground = Hex("#8B99AA");
        };
        sec.ChevronChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SetSectionExpanded(sec, !sec.Expanded, animate: true);
            SaveCollapsed();
        };

        // ---- left half: the label, which opens the section's page
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(sec.HeadGlyph);
        label.Children.Add(sec.Caption);
        sec.LabelHit = new Border
        {
            Child = label, Background = Brushes.Transparent, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 5, 8, 5), Cursor = Cursors.Hand,
            ToolTip = "Open the " + sec.Title + " overview"
        };
        sec.LabelHit.MouseEnter += (_, _) =>
        {
            if (_activeSectionPanel != sec.Panel) sec.LabelHit.Background = Hex("#12FFFFFF");
            sec.Caption.Foreground = Hex("#EAF6FF");
            sec.HeadGlyph.Foreground = Hex("#4FC3F7");
        };
        sec.LabelHit.MouseLeave += (_, _) =>
        {
            sec.LabelHit.Background = _activeSectionPanel == sec.Panel ? Hex("#184FC3F7") : Brushes.Transparent;
            sec.Caption.Foreground = sec.CaptionRest;
            sec.HeadGlyph.Foreground = sec.GlyphRest;
        };
        sec.LabelHit.MouseLeftButtonUp += (_, _) =>
        {
            if (!sec.Expanded) { SetSectionExpanded(sec, true, animate: true); SaveCollapsed(); }
            ShowSectionDashboard(sec);
        };

        Grid.SetColumn(sec.LabelHit, 0);
        Grid.SetColumn(sec.ChevronChip, 1);
        grid.Children.Add(sec.LabelHit);
        grid.Children.Add(sec.ChevronChip);

        var head = new Border { Child = grid, Margin = new Thickness(0, 12, 2, 2) };
        return head;
    }

    private RadioButton MakeNavItem(string glyph, string label, string dataTab)
    {
        var rb = new RadioButton
        {
            Style = (Style)FindResource("NavItem"),
            GroupName = "nav",
            Content = NavItemContent(glyph, label)
        };
        rb.Checked += (_, _) => { if (_ready) { ShowPanel("PanelData"); SelectDataTab(dataTab); } };
        return rb;
    }

    private StackPanel NavItemContent(string glyph, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Style = (Style)FindResource("NavGlyph"), Text = glyph });
        sp.Children.Add(new TextBlock { Text = label });
        return sp;
    }

    /// <summary>Change a XAML-built nav item's caption without touching its glyph.</summary>
    private static void RelabelNav(RadioButton rb, string label)
    {
        if (rb.Content is StackPanel sp)
            foreach (TextBlock tb in sp.Children.OfType<TextBlock>())
                if (tb.FontFamily?.Source != Mdl2.Source) { tb.Text = label; return; }
    }

    /// <summary>Drive the Game Data panel's own tab strip from the rail.</summary>
    private void SelectDataTab(string tag)
    {
        if (PanelData is null) return;
        foreach (RadioButton rb in Descendants(PanelData).OfType<RadioButton>())
            if (rb.GroupName == "datatab" && (rb.Tag as string) == tag) { rb.IsChecked = true; return; }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
            {
                yield return d;
                foreach (DependencyObject g in Descendants(d)) yield return g;
            }
    }

    // ------------------------------------------------------------------ expand / collapse

    private void SetSectionExpanded(NavSection sec, bool expand, bool animate)
    {
        sec.Expanded = expand;
        sec.ChevronChip.ToolTip = (expand ? "Hide the " : "Show the ") + sec.Title + " pages";

        var rot = (RotateTransform)sec.Chevron.RenderTransform;
        if (animate)
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(expand ? 0 : -90, TimeSpan.FromMilliseconds(190))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { rot.BeginAnimation(RotateTransform.AngleProperty, null); rot.Angle = expand ? 0 : -90; }

        if (!animate)
        {
            sec.Body.BeginAnimation(FrameworkElement.HeightProperty, null);
            sec.Body.Height = expand ? double.NaN : 0;
            sec.Items.Opacity = expand ? 1 : 0;
            return;
        }

        // Animating from NaN does nothing, so pin the current height first.
        if (double.IsNaN(sec.Body.Height)) sec.Body.Height = sec.Body.ActualHeight;

        double target = 0;
        if (expand)
        {
            sec.Items.Measure(new Size(sec.Body.ActualWidth > 1 ? sec.Body.ActualWidth : 220,
                                       double.PositiveInfinity));
            target = sec.Items.DesiredSize.Height;
            // A zero here would animate the section open to nothing and leave it looking broken.
            if (target < 1) target = sec.Buttons.Count * 38;
        }

        var slide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(190))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (expand)
            slide.Completed += (_, _) =>
            {
                // Hand the height back to the layout system so the rail keeps sizing itself.
                if (!sec.Expanded) return;
                sec.Body.BeginAnimation(FrameworkElement.HeightProperty, null);
                sec.Body.Height = double.NaN;
            };
        sec.Body.BeginAnimation(FrameworkElement.HeightProperty, slide);
        sec.Items.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(expand ? 220 : 130)));
    }

    private void OnNavItemChecked(NavSection sec)
    {
        // ShowPanel() only knows about the panels declared in XAML, so a dashboard has to take
        // itself off screen when a real page is chosen. Every panel switch in the app goes through
        // a nav RadioButton being checked, and every one of them lands here.
        foreach (NavSection s in _navSections)
            if (s.Dashboard is { } d) d.Visibility = Visibility.Collapsed;

        SetActiveSection("");                       // a page, not a dashboard, is showing
        foreach (NavSection s in _navSections)
        {
            bool mine = ReferenceEquals(s, sec);
            s.CaptionRest = mine ? Hex("#9FB6CC") : Hex("#5D6878");
            s.GlyphRest = mine ? Hex("#4FC3F7") : Hex("#6E7C8C");
            s.Caption.Foreground = s.CaptionRest;
            s.HeadGlyph.Foreground = s.GlyphRest;
        }
        if (!sec.Expanded) { SetSectionExpanded(sec, true, animate: true); SaveCollapsed(); }
    }

    private void SetActiveSection(string panel)
    {
        _activeSectionPanel = panel;
        foreach (NavSection s in _navSections)
        {
            bool on = s.Panel == panel && panel.Length > 0;
            s.LabelHit.Background = on ? Hex("#184FC3F7") : Brushes.Transparent;
            s.CaptionRest = on ? Hex("#EAF6FF") : Hex("#5D6878");
            s.GlyphRest = on ? Hex("#4FC3F7") : Hex("#6E7C8C");
            s.Caption.Foreground = s.CaptionRest;
            s.HeadGlyph.Foreground = s.GlyphRest;
        }
    }

    private HashSet<string> LoadCollapsed()
    {
        try
        {
            if (!File.Exists(NavStatePath)) return new HashSet<string>();
            string[]? keys = JsonSerializer.Deserialize<string[]>(File.ReadAllText(NavStatePath));
            return new HashSet<string>(keys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(); }
    }

    private void SaveCollapsed()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NavStatePath)!);
            string[] keys = _navSections.Where(s => !s.Expanded).Select(s => s.Title).ToArray();
            File.WriteAllText(NavStatePath, JsonSerializer.Serialize(keys));
        }
        catch { /* a remembered rail is a nicety, never a reason to fail */ }
    }

    // ------------------------------------------------------------------ section dashboards

    private void ShowSectionDashboard(NavSection sec)
    {
        // COMMAND already has a dashboard — it is the Command Center.
        if (sec.Panel == "PanelHome") { NavHome.IsChecked = true; return; }

        EnsureSectionPanel(sec);
        if (sec.Dashboard is null) return;

        // Clearing the group first means no rail item is left looking selected behind a dashboard.
        foreach (NavSection s in _navSections)
            foreach (RadioButton rb in s.Buttons) rb.IsChecked = false;

        foreach (string p in Panels)
            if (FindName(p) is UIElement el) el.Visibility = Visibility.Collapsed;
        foreach (NavSection s in _navSections)
            if (s.Dashboard is { } d) d.Visibility = ReferenceEquals(d, sec.Dashboard)
                                                    ? Visibility.Visible : Visibility.Collapsed;

        SetActiveSection(sec.Panel);
    }

    private void EnsureSectionPanel(NavSection sec)
    {
        if (sec.Dashboard is not null) return;
        if (PanelHome.Parent is not Grid host) return;

        FillPages(sec);

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(DashHeading(sec));

        var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (NavPage p in sec.Pages) wrap.Children.Add(PageCard(p));
        body.Children.Add(wrap);

        if (sec.HubPages.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "ON THE HUB", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#5D6878"), Margin = new Thickness(2, 14, 0, 2)
            });
            body.Children.Add(new TextBlock
            {
                Text = "Catalogs too big to ship inside the app. These open eqavatar.ldtlan.com in your browser, "
                     + "where every item and spell can be shown at any upgrade level from +0 to +10.",
                FontSize = 11.5, Foreground = Hex("#8FA0B2"), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 10)
            });
            var hub = new WrapPanel();
            foreach (NavPage p in sec.HubPages) hub.Children.Add(PageCard(p));
            body.Children.Add(hub);
        }

        var panel = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed
        };
        host.Children.Add(panel);
        sec.Dashboard = panel;
    }

    private StackPanel DashHeading(NavSection sec)
    {
        var sp = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = sec.Glyph, FontFamily = Mdl2, FontSize = 22, Foreground = Hex("#4FC3F7"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = sec.Title, FontSize = 22, Foreground = Hex("#EAF6FF"),
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(row);
        sp.Children.Add(new TextBlock
        {
            Text = sec.Blurb, FontSize = 12.5, Foreground = Hex("#8FA0B2"), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 6, 0, 0)
        });
        return sp;
    }

    private Border PageCard(NavPage p)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock
        {
            Text = p.Glyph, FontFamily = Mdl2, FontSize = 14, Width = 24, Foreground = Hex("#7FB2D9"),
            VerticalAlignment = VerticalAlignment.Center
        });
        top.Children.Add(new TextBlock
        {
            Text = p.Title, FontSize = 14, Foreground = Hex("#EAF6FF"), VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock
        {
            Text = p.Blurb, FontSize = 11.5, Foreground = Hex("#8FA0B2"), TextWrapping = TextWrapping.Wrap
        });
        if (p.HubPage is not null)
            sp.Children.Add(new TextBlock
            {
                Text = "opens in your browser ↗", FontSize = 10.5, Foreground = Hex("#5D6878"),
                Margin = new Thickness(0, 6, 0, 0)
            });

        var card = new Border
        {
            Child = sp, Width = 250, Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12),
            Background = Hex("#0F1620"), BorderBrush = Hex("#22384F"), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        card.MouseEnter += (_, _) => { card.Background = Hex("#14202E"); card.BorderBrush = Hex("#4FC3F7"); };
        card.MouseLeave += (_, _) => { card.Background = Hex("#0F1620"); card.BorderBrush = Hex("#22384F"); };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (p.Nav is { } rb) rb.IsChecked = true;
            else if (p.HubPage is { } page) OpenHubPage(page);
        };
        return card;
    }

    private void OpenHubPage(string page)
    {
        string url = _settings.HubUrl;
        int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
        string baseUrl = i >= 0 ? url.Substring(0, i) : url;
        if (baseUrl.Length == 0) { ShowToast("Set the hub address on the Settings page first"); return; }
        try { Process.Start(new ProcessStartInfo(baseUrl + "gamedata.php?p=" + page) { UseShellExecute = true }); }
        catch (Exception ex) { GrindLogLine("Couldn't open browser: " + ex.Message); }
    }

    /// <summary>What each section's dashboard lists. Built once, when the dashboard is first opened.</summary>
    private void FillPages(NavSection sec)
    {
        switch (sec.Panel)
        {
            case "PanelSecInsight":
                sec.Pages = new[]
                {
                    new NavPage("", "Combat", "Live parse of the fight log — damage in, damage out, and what finally killed you.", NavCombat),
                    new NavPage("", "Maps", "Zone maps with your position on them, plus heatmaps built from where you have actually ground.", NavMaps),
                    new NavPage("", "Key Mappings", "Every action the bot can press, mapped to the key your game really has bound to it.", NavKeymaps),
                    new NavPage("", "Sessions", "Every recorded run: XP an hour, kills, downtime, and how it compares to the last one.", NavSessions),
                    new NavPage("", "Log Reader", "Tail EverQuest's log file and watch events land as the game writes them.", NavLog),
                };
                break;

            case "PanelSecGameData":
                sec.Pages = new[]
                {
                    new NavPage("", "Mobs", "7,872 creatures, searchable by name, by zone, or by what they drop.", NavData),
                    new NavPage("", "Raid Targets", "32 raid encounters with their loot, and the zones they sit in.", NavSectionButton(sec, 1)),
                    new NavPage("", "Plane of Sky", "Every island, key and class reward on the way up.", NavSectionButton(sec, 2)),
                    new NavPage("", "Hunting Guide", "Zones ranked for your level — computed from the mob catalog, not hand-curated.", NavSectionButton(sec, 3)),
                };
                sec.HubPages = new[]
                {
                    new NavPage("", "Items", "Every item, searchable and filterable, with its in-game icon.", null, "items"),
                    new NavPage("", "Spells", "Every spell by class and level, with its gem icon.", null, "spells"),
                    new NavPage("", "Gear", "Items that occupy an armour slot, ranked by what they give you.", null, "gear"),
                    new NavPage("", "Weapons", "Anything with a damage and delay, sorted by ratio.", null, "weapons"),
                    new NavPage("", "Procs", "Items that fire a spell when you hit something.", null, "procs"),
                    new NavPage("", "Focus Effects", "Gear that bends your spells — range, duration, damage.", null, "focus"),
                    new NavPage("", "Clickies", "Items you can click for an effect, worn or in a bag.", null, "clickies"),
                    new NavPage("", "Worn Effects", "Effects that apply just for having the item equipped.", null, "worn"),
                };
                break;

            case "PanelSecDebug":
                sec.Pages = new[]
                {
                    new NavPage("", "Input Probe", "Send one key or one click and see exactly what the game received.", NavInput),
                    new NavPage("", "Login Console", "Drive the launcher and log a character in without touching the keyboard.", NavLogin),
                };
                break;

            case "PanelSecAccount":
                sec.Pages = new[]
                {
                    new NavPage("", "Profile", "Your character, your loadouts, and the gear the last inventory read found.", NavProfile),
                    new NavPage("", "Licensing", "Subscription tier, linked devices, and connected services.", NavLicensing),
                    new NavPage("", "Settings", "Hotkeys, window behaviour, the hub address, and the update channel.", NavSettings),
                };
                break;
        }
    }

    /// <summary>The nth nav button of a section — used by the Game Data dashboard, whose extra
    /// buttons are created here rather than named in XAML.</summary>
    private static RadioButton? NavSectionButton(NavSection sec, int index) =>
        index >= 0 && index < sec.Buttons.Count ? sec.Buttons[index] : null;
}
