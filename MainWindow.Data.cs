using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Map;

namespace EQAvatar.Spike;

/// <summary>
/// Game Data panel (partial class — new features get their own file now so releases stay
/// small). Four tabs over the Companion-derived catalogs: Mobs (searchable bestiary),
/// Raid Targets, Plane of Sky quests, and a Hunting Guide computed from mob density.
/// </summary>
public partial class MainWindow
{
    private bool _dataLoaded;
    private MobEntry? _dataSelectedMob;

    /// <summary>Lazy-load the catalogs the first time the panel opens, off the UI thread.</summary>
    private void EnsureDataLoaded()
    {
        if (_dataLoaded) return;
        _dataLoaded = true;
        DataStatus.Text = "loading catalogs…";
        _ = Task.Run(GameData.Ensure).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            DataStatus.Text = $"{GameData.Mobs.Count:n0} mobs · {GameData.Bosses.Count} raid targets · {GameData.Sky.Count} Plane of Sky quests — data adapted from EQ Legends Companion (MIT)";
            DataSearch_Changed(this, null!);
            FillRaid();
            FillSkyClasses();
            FillGuide();
        }));
    }

    private void DataTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DataViewMobs is null) return;   // during InitializeComponent
        string tag = (sender as RadioButton)?.Tag as string ?? "Mobs";
        DataViewMobs.Visibility = tag == "Mobs" ? Visibility.Visible : Visibility.Collapsed;
        DataViewRaid.Visibility = tag == "Raid" ? Visibility.Visible : Visibility.Collapsed;
        DataViewSky.Visibility = tag == "Sky" ? Visibility.Visible : Visibility.Collapsed;
        DataViewGuide.Visibility = tag == "Guide" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Mobs tab ----------------

    private void DataSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!GameData.Loaded) return;
        List<MobEntry> hits = GameData.SearchMobs(DataSearchBox.Text.Trim());
        MobsList.ItemsSource = hits;
        MobsCount.Text = hits.Count >= 250 ? "first 250 matches" : $"{hits.Count} matches";
    }

    private void MobsList_Selected(object sender, SelectionChangedEventArgs e)
    {
        _dataSelectedMob = MobsList.SelectedItem as MobEntry;
        MobDetail.Children.Clear();
        if (_dataSelectedMob is not { } m) return;

        void Line(string text, double size = 12, bool bold = false, string? color = null)
        {
            MobDetail.Children.Add(new TextBlock
            {
                Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = color is null ? (Brush)FindResource("Text") : Hex(color),
                Margin = new Thickness(0, 0, 0, 3),
            });
        }
        Line(m.Name, 16, bold: true);
        Line($"Level {m.LevelText}", 12, color: "#9FB6CC");
        Line("Zones: " + m.ZonesText, 12, color: "#9FB6CC");
        if (m.Locs.Count > 0 && m.Locs[0].Count >= 2 && m.Locs[0][0] is double ns && m.Locs[0][1] is double ew)
            Line($"Seen at /loc {ns:0}, {ew:0}", 11.5, color: "#5D6878");
        if (m.Drops.Count > 0)
        {
            Line("Drops", 12.5, bold: true);
            foreach (string d in m.Drops) Line("  • " + d, 12);
        }
        string? stem = m.Zones.Select(ZoneTable.ShortFor).FirstOrDefault(s => s != null);
        if (stem != null)
        {
            var btn = new Button { Content = "Show zone on map →", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            btn.Click += (_, _) => { LoadMapZone(stem); NavMaps.IsChecked = true; };
            MobDetail.Children.Add(btn);
        }
    }

    // ---------------- Raid Targets tab ----------------

    private void FillRaid()
    {
        RaidHost.Children.Clear();
        foreach (var group in GameData.Bosses.GroupBy(b => b.Category))
        {
            RaidHost.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.Bold,
                Foreground = Hex("#7FB2D9"), Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (BossEntry b in group)
            {
                var row = new TextBlock { FontSize = 13, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
                row.Inlines.Add(new System.Windows.Documents.Run(b.Name) { Foreground = (Brush)FindResource("Text"), FontWeight = FontWeights.SemiBold });
                row.Inlines.Add(new System.Windows.Documents.Run("   " + b.Zone) { Foreground = Hex("#5D6878") });
                RaidHost.Children.Add(row);
            }
        }
    }

    // ---------------- Plane of Sky tab ----------------

    private void FillSkyClasses()
    {
        var classes = GameData.Sky.Select(q => q.ClassName).Distinct().OrderBy(c => c).ToList();
        SkyClassBox.ItemsSource = classes;
        string mine = _settings.HubClass;
        SkyClassBox.SelectedItem = classes.Contains(mine) ? mine : classes.FirstOrDefault();
    }

    private void SkyClass_Changed(object sender, SelectionChangedEventArgs e)
    {
        SkyHost.Children.Clear();
        string? cls = SkyClassBox.SelectedItem as string;
        if (cls is null) return;
        foreach (SkyQuest q in GameData.Sky.Where(q => q.ClassName == cls))
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            void Add(string text, double size, bool bold, string color)
                => card.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = Hex(color), Margin = new Thickness(0, 0, 0, 2) });
            Add(q.Name, 14.5, true, "#EAF6FF");
            Add($"Giver: {q.Giver}   ·   Rune: {q.Rune}", 12, false, "#9FB6CC");
            Add($"Reward: {q.Reward}", 12.5, true, "#7CE38B");
            if (q.Items.Count > 0)
            {
                Add("Turn-ins:", 12, true, "#9FB6CC");
                foreach (SkyItem it in q.Items)
                {
                    string who = it.Who.Count > 0 ? " — drops from " + string.Join(", ", it.Who) : "";
                    string cnt = it.Count is int c and > 1 ? $" ×{c}" : "";
                    Add($"  • {it.Name}{cnt} ({it.Where}){who}", 12, false, "#C6D2DE");
                }
            }
            if (!string.IsNullOrWhiteSpace(q.RewardStats))
            {
                var stats = new TextBlock { Text = q.RewardStats, FontSize = 11, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Foreground = Hex("#5D6878"), Margin = new Thickness(0, 3, 0, 0) };
                card.Children.Add(stats);
            }
            var border = new Border { Background = Hex("#121A28"), BorderBrush = Hex("#26405A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = card };
            SkyHost.Children.Add(border);
        }
    }

    // ---------------- Hunting Guide tab ----------------

    private void GuideLevel_Changed(object sender, TextChangedEventArgs e) => FillGuide();

    private void FillGuide()
    {
        if (!GameData.Loaded || GuideHost is null) return;
        GuideHost.Children.Clear();
        if (!int.TryParse(GuideLevelBox.Text.Trim(), out int level)) { level = Math.Clamp(_settings.HubLevel, 1, 60); }
        foreach (HuntZone hz in GameData.HuntingGuide(level))
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var head = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new System.Windows.Documents.Run(hz.Zone) { Foreground = (Brush)FindResource("Text"), FontWeight = FontWeights.Bold });
            head.Inlines.Add(new System.Windows.Documents.Run($"   {hz.Count} mobs in range · levels {hz.MinLevel}–{hz.MaxLevel}") { Foreground = Hex("#5D6878") });
            card.Children.Add(head);
            card.Children.Add(new TextBlock
            {
                Text = "e.g. " + string.Join(" · ", hz.Sample.Select(m => $"{m.Name} ({m.LevelText})")),
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Hex("#9FB6CC"), Margin = new Thickness(0, 2, 0, 0),
            });
            string? stem = ZoneTable.ShortFor(hz.Zone);
            if (stem != null)
            {
                var link = new Button { Content = "map →", Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                string s2 = stem;
                link.Click += (_, _) => { LoadMapZone(s2); NavMaps.IsChecked = true; };
                card.Children.Add(link);
            }
            GuideHost.Children.Add(new Border { Background = Hex("#121A28"), BorderBrush = Hex("#26405A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = card });
        }
    }
}
