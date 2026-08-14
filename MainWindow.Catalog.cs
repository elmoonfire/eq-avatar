using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EQAvatar.Spike.Net;

namespace EQAvatar.Spike;

/// <summary>
/// The catalog pages under Game Data — Items, Gear, Weapons, Spells, Procs, Focus Effects,
/// Clickies, Worn Effects and Drop Records (partial class).
///
/// ONE PANEL, NINE PAGES. Every one of these is the same shape: search a corpus, pick a row,
/// read the detail. Nine panels would be nine copies of the same bug. The rail entries all point
/// at this single panel and set a page key; only the filters and the detail renderer differ.
///
/// THE DATA IS NOT IN THIS APP. It comes from <see cref="GameDataClient"/>, which reads
/// `/hub/api/gamedata.php` and caches every response under %AppData%. Ten thousand items and a
/// 874 KB icon sheet do not belong in an installer, and a corpus that only the hub can refresh is
/// a corpus every user gets the moment it is refreshed. Offline, you keep everything you have
/// already looked at.
///
/// Built in code rather than XAML for the same reason as the rail: MainWindow.xaml belongs to
/// other workstreams, and a XAML resource that fails to resolve at runtime is a dead app rather
/// than a dead page.
/// </summary>
public partial class MainWindow
{
    private readonly record struct CatalogPage(string Key, string Title, string Glyph, string Blurb);

    /// <summary>Order here is the order in the rail.</summary>
    private static readonly CatalogPage[] CatalogPages =
    {
        new("items",    "Items",         "\uE7B8", "Every item in the game, with the game's own icon."),
        new("gear",     "Gear",          "\uEA18", "Anything that occupies an armour slot."),
        new("weapons",  "Weapons",       "\uE7C1", "Primary, secondary and ranged, by damage and delay."),
        new("spells",   "Spells",        "\uE945", "Every spell, who casts it and from what level."),
        new("procs",    "Procs",         "\uE734", "Combat effects that fire on their own."),
        new("focus",    "Focus Effects", "\uE890", "Passive modifiers to the spells you cast."),
        new("clickies", "Clickies",      "\uE962", "Items you click for an effect."),
        new("worn",     "Worn Effects",  "\uE7B3", "Effects that apply simply by being equipped."),
        new("drops",    "Drop Records",  "\uE81E", "What drops this, and what does that one drop."),
    };

    private GameDataClient? _gd;
    private Grid? _catalogPanel;
    private CatalogPage _catPage = CatalogPages[0];

    private TextBlock _catTitle = null!, _catBlurb = null!, _catCount = null!, _catStatus = null!;
    private TextBox _catSearch = null!;
    private ComboBox _catClass = null!;
    private TextBlock _catClassLabel = null!;
    private ListBox _catList = null!;
    private StackPanel _catDetail = null!;

    private readonly DispatcherTimer _catDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly List<JsonElement> _catRows = new();
    private int _catQuerySeq;
    /// <summary>Bumped on every selection change. A detail render that finishes after the user
    /// has moved on must throw its work away rather than append it under someone else's heading.</summary>
    private int _catDetailSeq;
    /// <summary>Set while the page switch is clearing the search box, so the resulting
    /// TextChanged does not queue a second query behind the one the switch already started.</summary>
    private bool _catSwitching;

    // ------------------------------------------------------------------ rail entries

    /// <summary>The nine rail buttons, in page order. Called while the rail is being built.</summary>
    private RadioButton[] BuildCatalogNav()
    {
        var made = new List<RadioButton>();
        foreach (CatalogPage p in CatalogPages)
        {
            CatalogPage page = p;                       // capture per iteration, not the loop variable
            var rb = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                GroupName = "nav",
                Content = NavItemContent(page.Glyph, page.Title)
            };
            rb.Checked += (_, _) => { if (_ready) ShowCatalog(page); };
            made.Add(rb);
        }
        return made.ToArray();
    }

    /// <summary>Take the catalog off screen — called whenever any other page is chosen.</summary>
    private void HideCatalog()
    {
        if (_catalogPanel is not null) _catalogPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowCatalog(CatalogPage page)
    {
        EnsureCatalogPanel();
        if (_catalogPanel is null) return;

        foreach (string p in Panels)
            if (FindName(p) is UIElement el) el.Visibility = Visibility.Collapsed;
        foreach (NavSection s in _navSections)
            if (s.Dashboard is { } d) d.Visibility = Visibility.Collapsed;
        _catalogPanel.Visibility = Visibility.Visible;

        _catPage = page;
        _catTitle.Text = page.Title;
        _catBlurb.Text = page.Blurb;
        _catCount.Text = "";
        _catDetail.Children.Clear();
        _catSwitching = true;
        _catSearch.Text = "";
        _catSwitching = false;

        bool classy = page.Key is "spells" or "items" or "gear" or "weapons";
        _catClass.Visibility = _catClassLabel.Visibility = classy ? Visibility.Visible : Visibility.Collapsed;

        _catSearch.ToolTip = page.Key == "drops"
            ? "Search by item, by mob, or by zone — all three at once."
            : "Search by name.";

        _ = RunCatalogQuery();
    }

    // ------------------------------------------------------------------ the panel

    private void EnsureCatalogPanel()
    {
        if (_catalogPanel is not null) return;
        if (PanelHome.Parent is not Grid host) return;

        _gd ??= new GameDataClient(_settings);
        _catDebounce.Tick += (_, _) => { _catDebounce.Stop(); _ = RunCatalogQuery(); };

        var root = new Grid { Margin = new Thickness(18), Visibility = Visibility.Collapsed };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- title
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        _catTitle = new TextBlock { FontSize = 22, Foreground = Hex("#EAF6FF"), VerticalAlignment = VerticalAlignment.Center };
        _catCount = new TextBlock
        {
            FontSize = 11.5, Foreground = Hex("#7FB2D9"), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(_catTitle);
        head.Children.Add(_catCount);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        _catBlurb = new TextBlock
        {
            FontSize = 12, Foreground = Hex("#8FA0B2"), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 4, 0, 0)
        };
        Grid.SetRow(_catBlurb, 1);
        root.Children.Add(_catBlurb);

        // ---- filters
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        _catSearch = new TextBox { Width = 320, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _catSearch.TextChanged += (_, _) =>
        {
            if (_catSwitching) return;
            _catDebounce.Stop();
            _catDebounce.Start();
        };
        bar.Children.Add(_catSearch);

        _catClassLabel = new TextBlock
        {
            Text = "Class", VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hex("#8FA0B2"), Margin = new Thickness(0, 0, 6, 0)
        };
        _catClass = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _catClass.Items.Add("Any class");
        foreach (string c in EqClasses) _catClass.Items.Add(c);
        _catClass.SelectedIndex = 0;
        _catClass.SelectionChanged += (_, _) => { if (_catalogPanel is not null) _ = RunCatalogQuery(); };
        bar.Children.Add(_catClassLabel);
        bar.Children.Add(_catClass);

        _catStatus = new TextBlock
        {
            FontSize = 11, Foreground = Hex("#5D6878"), VerticalAlignment = VerticalAlignment.Center
        };
        bar.Children.Add(_catStatus);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        // ---- list + detail
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });

        _catList = new ListBox
        {
            Margin = new Thickness(0, 0, 10, 0), Background = Hex("#0C0F13"), BorderBrush = Hex("#26405A"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_catList, ScrollBarVisibility.Disabled);
        _catList.SelectionChanged += (_, _) => ShowCatalogDetail();
        Grid.SetColumn(_catList, 0);
        split.Children.Add(_catList);

        _catDetail = new StackPanel();
        var detailBox = new Border
        {
            Background = Hex("#0C0F13"), BorderBrush = Hex("#26405A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _catDetail }
        };
        Grid.SetColumn(detailBox, 1);
        split.Children.Add(detailBox);

        Grid.SetRow(split, 3);
        root.Children.Add(split);

        host.Children.Add(root);
        _catalogPanel = root;
    }

    // ------------------------------------------------------------------ querying

    private async Task RunCatalogQuery()
    {
        if (_catalogPanel is null || _gd is null) return;
        int seq = ++_catQuerySeq;

        string q = _catSearch.Text.Trim();
        string cls = _catClass.SelectedIndex > 0 ? (string)_catClass.SelectedItem : "";

        var url = new StringBuilder();
        if (_catPage.Key == "drops")
        {
            // Drop Records opens on nothing rather than on 22,502 rows nobody asked for.
            if (q.Length < 2)
            {
                _catList.Items.Clear();
                _catRows.Clear();
                _catDetail.Children.Clear();
                _catCount.Text = "";
                _catStatus.Text = "Type an item, a mob or a zone — it searches all three.";
                return;
            }
            url.Append("p=drops&q=").Append(Uri.EscapeDataString(q)).Append("&limit=300");
        }
        else
        {
            url.Append("p=").Append(_catPage.Key).Append("&limit=200");
            if (q.Length > 0) url.Append("&q=").Append(Uri.EscapeDataString(q));
            if (cls.Length > 0) url.Append("&cls=").Append(Uri.EscapeDataString(cls));
        }

        _catStatus.Text = "reading…";
        await _gd.EnsureAtlasAsync();
        JsonElement? res = await _gd.GetAsync(url.ToString());
        if (seq != _catQuerySeq) return;                 // a newer keystroke has overtaken this one

        _catList.Items.Clear();
        _catRows.Clear();
        _catDetail.Children.Clear();

        if (res is not { } root || !root.TryGetProperty("rows", out JsonElement rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            _catStatus.Text = "The hub could not be reached, and nothing for this page is cached yet.";
            _catCount.Text = "";
            return;
        }

        foreach (JsonElement r in rows.EnumerateArray())
        {
            _catRows.Add(r);
            _catList.Items.Add(CatalogRow(r));
        }

        int total = root.TryGetProperty("total", out JsonElement t) && t.TryGetInt32(out int n) ? n : _catRows.Count;
        _catCount.Text = total.ToString("N0");
        _catStatus.Text = total > _catRows.Count
            ? $"showing the first {_catRows.Count:N0} of {total:N0} — narrow the search to see the rest"
            : $"{_catRows.Count:N0} shown · {GameDataClient.CacheSummary()}";
    }

    /// <summary>One row: the game's icon where there is one, the name, and the line that matters
    /// most for this page — a weapon's ratio, an item's slot, a spell's classes, a drop's mob.</summary>
    private ListBoxItem CatalogRow(JsonElement r)
    {
        var grid = new Grid { Margin = new Thickness(2, 3, 2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        BitmapSource? art = _gd?.Icon(GameDataClient.IconId(r));
        var icon = new Border
        {
            Width = 32, Height = 32, Margin = new Thickness(0, 0, 9, 0), CornerRadius = new CornerRadius(4),
            Background = Hex("#101A26"), BorderBrush = Hex("#1E2C3C"), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (art is not null)
            icon.Child = new Image { Source = art, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = Str(r, _catPage.Key == "drops" ? "item" : "name"),
            Foreground = Hex("#EAF6FF"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = RowSubtitle(r), Foreground = Hex("#7A8CA0"), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new ListBoxItem { Content = grid, Padding = new Thickness(4, 2, 4, 2) };
    }

    private string RowSubtitle(JsonElement r)
    {
        switch (_catPage.Key)
        {
            case "drops":
                return Join(" · ", Str(r, "mob"), Str(r, "zone"));

            case "spells":
            {
                var who = new List<string>();
                if (r.TryGetProperty("classes", out JsonElement cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement c in cs.EnumerateArray())
                        who.Add(Str(c, "class") + " " + Num(c, "level"));
                return Join(" · ", string.Join(", ", who.Take(6)), Str(r, "skill"));
            }

            case "weapons":
            {
                string d = Num(r, "dmg"), dl = Num(r, "delay");
                string ratio = "";
                if (double.TryParse(d, out double dd) && double.TryParse(dl, out double ddl) && ddl > 0)
                    ratio = $"ratio {dd / ddl:0.00}";
                return Join(" · ", Str(r, "skill"), d.Length > 0 ? $"{d}/{dl}" : "", ratio);
            }

            default:
            {
                string ac = Num(r, "ac"), hp = Num(r, "hp"), mana = Num(r, "mana");
                var bits = new List<string> { Str(r, "slots") };
                if (ac.Length > 0) bits.Add("AC " + ac);
                if (hp.Length > 0) bits.Add("HP " + hp);
                if (mana.Length > 0) bits.Add("Mana " + mana);
                return Join(" · ", bits.ToArray());
            }
        }
    }

    // ------------------------------------------------------------------ detail

    private void ShowCatalogDetail()
    {
        int seq = ++_catDetailSeq;
        _catDetail.Children.Clear();
        int i = _catList.SelectedIndex;
        if (i < 0 || i >= _catRows.Count)
        {
            _catDetail.Children.Add(Dim("Pick a row to see everything about it."));
            return;
        }
        JsonElement row = _catRows[i];

        if (_catPage.Key == "drops") { RenderDropDetail(row); return; }
        if (_catPage.Key == "spells") { _ = RenderSpellDetail(row, seq); return; }
        _ = RenderItemDetail(row, seq);
    }

    private async Task RenderItemDetail(JsonElement row, int seq)
    {
        int id = int.TryParse(Num(row, "id"), out int n) ? n : 0;
        DetailHeader(Str(row, "name"), GameDataClient.IconId(row), Str(row, "kind"));
        UIElement reading = Dim("reading…");
        _catDetail.Children.Add(reading);

        JsonElement? res = _gd is null ? null : await _gd.GetAsync("p=item&id=" + id);
        if (seq != _catDetailSeq) return;                // the selection moved on while we waited
        _catDetail.Children.Remove(reading);
        if (res is not { } d || !d.TryGetProperty("item", out JsonElement it))
        {
            _catDetail.Children.Add(Dim("That item's detail isn't cached and the hub is unreachable."));
            return;
        }

        foreach (string f in new[] { "flags", "slots", "classes", "size", "era" })
            if (Str(it, f).Length > 0) _catDetail.Children.Add(Pair(Nice(f), Str(it, f).Replace("|", " · ")));

        var stats = new List<string>();
        foreach (string s in new[] { "ac", "hp", "mana", "endur", "str", "sta", "agi", "dex", "wis",
                                     "int", "cha", "mr", "fr", "cr", "dr", "pr", "vr", "dmg", "delay" })
            if (Num(it, s).Length > 0) stats.Add(s.ToUpperInvariant() + " " + Num(it, s));
        if (stats.Count > 0) _catDetail.Children.Add(Pair("Stats", string.Join("   ", stats)));
        if (Num(it, "weight").Length > 0) _catDetail.Children.Add(Pair("Weight", Num(it, "weight")));

        if (d.TryGetProperty("effects", out JsonElement fx) && fx.GetArrayLength() > 0)
        {
            _catDetail.Children.Add(Head("Effects"));
            foreach (JsonElement e in fx.EnumerateArray())
                _catDetail.Children.Add(Pair(Nice(Str(e, "kind")),
                    Join(" · ", Str(e, "name"), Str(e, "cast_time"), Str(e, "note"))));
        }

        if (d.TryGetProperty("drops", out JsonElement dr) && dr.GetArrayLength() > 0)
        {
            _catDetail.Children.Add(Head($"Drops from ({dr.GetArrayLength()})"));
            foreach (JsonElement e in dr.EnumerateArray().Take(60))
                _catDetail.Children.Add(Pair(Str(e, "mob"), Str(e, "zone")));
            if (dr.GetArrayLength() > 60) _catDetail.Children.Add(Dim($"…and {dr.GetArrayLength() - 60} more"));
        }
        else _catDetail.Children.Add(Dim("No drop record — quested, crafted, or simply not catalogued."));
    }

    private async Task RenderSpellDetail(JsonElement row, int seq)
    {
        int id = int.TryParse(Num(row, "id"), out int n) ? n : 0;
        DetailHeader(Str(row, "name"), GameDataClient.IconId(row), Str(row, "skill"));
        JsonElement? res = _gd is null ? null : await _gd.GetAsync("p=spell&id=" + id);
        if (seq != _catDetailSeq) return;                // the selection moved on while we waited
        if (res is not { } d || !d.TryGetProperty("spell", out JsonElement sp))
        {
            _catDetail.Children.Add(Dim("That spell's detail isn't cached and the hub is unreachable."));
            return;
        }

        if (Str(sp, "description").Length > 0) _catDetail.Children.Add(Body(Str(sp, "description")));
        foreach (string f in new[] { "target", "type", "resist", "duration", "mana", "range",
                                     "casting_time", "recast_time", "era" })
        {
            string v = Str(sp, f).Length > 0 ? Str(sp, f) : Num(sp, f);
            if (v.Length > 0) _catDetail.Children.Add(Pair(Nice(f), v));
        }

        if (d.TryGetProperty("classes", out JsonElement cs) && cs.GetArrayLength() > 0)
        {
            _catDetail.Children.Add(Head("Who casts it"));
            foreach (JsonElement c in cs.EnumerateArray())
                _catDetail.Children.Add(Pair(Str(c, "class"), "level " + Num(c, "level")));
        }
    }

    private void RenderDropDetail(JsonElement row)
    {
        DetailHeader(Str(row, "item"), GameDataClient.IconId(row), Str(row, "zone"));
        _catDetail.Children.Add(Pair("Dropped by", Str(row, "mob")));
        _catDetail.Children.Add(Pair("Zone", Str(row, "zone")));

        string mob = Str(row, "mob");
        if (mob.Length == 0) return;
        var btn = new TextBlock
        {
            Text = "Show everything " + mob + " drops →", Foreground = Hex("#4FC3F7"),
            FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
            TextWrapping = TextWrapping.Wrap
        };
        btn.MouseLeftButtonUp += async (_, _) =>
        {
            int seq = ++_catDetailSeq;
            _catDetail.Children.Clear();
            DetailHeader(mob, null, "everything this one drops");
            JsonElement? res = _gd is null ? null
                : await _gd.GetAsync("p=drops&mob=" + Uri.EscapeDataString(mob) + "&limit=300");
            if (seq != _catDetailSeq) return;
            if (res is not { } d || !d.TryGetProperty("rows", out JsonElement rows))
            { _catDetail.Children.Add(Dim("Nothing came back for that one.")); return; }
            foreach (JsonElement e in rows.EnumerateArray())
                _catDetail.Children.Add(Pair(Str(e, "item"), Str(e, "zone")));
        };
        _catDetail.Children.Add(btn);
    }

    private void DetailHeader(string name, int? icon, string sub)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        BitmapSource? art = _gd?.Icon(icon);
        if (art is not null)
            row.Children.Add(new Image
            {
                Source = art, Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0),
                Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center
            });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = name, FontSize = 15, Foreground = Hex("#EAF6FF"), TextWrapping = TextWrapping.Wrap });
        if (sub.Length > 0) t.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Hex("#7A8CA0") });
        row.Children.Add(t);
        _catDetail.Children.Add(row);
    }

    // ------------------------------------------------------------------ small helpers

    private TextBlock Dim(string s) => new()
    { Text = s, Foreground = Hex("#5D6878"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private TextBlock Body(string s) => new()
    { Text = s, Foreground = Hex("#B9C6D4"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    private TextBlock Head(string s) => new()
    {
        Text = s.ToUpperInvariant(), Foreground = Hex("#5D6878"), FontSize = 10,
        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4)
    };

    private Grid Pair(string k, string v)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = new TextBlock { Text = k, Foreground = Hex("#7A8CA0"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        var b = new TextBlock { Text = v, Foreground = Hex("#DCE7F2"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 1);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    private static string Nice(string field) => field switch
    {
        "casting_time" => "Cast time",
        "recast_time"  => "Recast",
        "req_level"    => "Required level",
        "rec_level"    => "Recommended",
        "cast_time"    => "Cast",
        "click"        => "Clicky",
        "proc"         => "Proc",
        "focus"        => "Focus",
        "worn"         => "Worn",
        _              => char.ToUpperInvariant(field[0]) + field[1..].Replace('_', ' ')
    };

    private static string Join(string sep, params string[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>A string field, whatever JSON type the corpus happened to store it as.</summary>
    private static string Str(JsonElement e, string field)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(field, out JsonElement v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True   => "yes",
            JsonValueKind.False  => "",
            _                    => ""
        };
    }

    /// <summary>A numeric field as text, empty when the corpus has nothing — a null AC and an AC
    /// of 0 are different facts and neither should print as "0".</summary>
    private static string Num(JsonElement e, string field)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(field, out JsonElement v)) return "";
        if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = v.GetString() ?? "";
            return s.Trim().Length == 0 ? "" : s;
        }
        return "";
    }
}
