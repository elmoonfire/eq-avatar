using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The loadout menu behind the title-bar chip (partial class).
///
/// The chip's second line shows the loadout the last inventory read found — race, the three
/// classes, and the level. Hovering it drops a mega-menu of every other loadout this character
/// has been seen wearing, each as a card fronted by that loadout's PRIMARY class emblem, playing
/// the same animation the game plays beside the gear. Swap in game, read again, and the card
/// that was in the title bar simply moves into the menu.
///
/// This is built in code rather than XAML on purpose. The title bar lives in MainWindow.xaml,
/// which the Key Mappings workstream owns, and this app has twice been killed at startup by a
/// XAML resource that could not be resolved at load time (0.9.21, 0.9.25). Constructing the
/// popup here keeps the two workstreams out of each other's file and keeps a mistake in this
/// code a runtime no-op instead of a window that never opens.
/// </summary>
public partial class MainWindow
{
    private readonly LoadoutStore _loadouts = LoadoutStore.Load();
    private Popup? _loadoutPopup;
    private StackPanel? _loadoutList;
    private DispatcherTimer? _loadoutCloseTimer;

    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    /// <summary>The chip's subtitle text for the current loadout, or null if there isn't one yet.</summary>
    private string? CurrentLoadoutLine()
    {
        Loadout? c = _loadouts.Current;
        if (c is null) return null;
        string race = string.IsNullOrWhiteSpace(c.Race) ? "" : c.Race + " ";
        return $"{race}{c.Display} · Lv {Math.Max(1, c.Level)}";
    }

    /// <summary>Take what the inventory read saw. Returns true if the loadout actually changed.</summary>
    private bool RecordLoadout(Ocr.InventorySnapshot snap)
    {
        bool changed = _loadouts.Record(snap.Classes, snap.Level, _settings.HubRace);
        RefreshLoadoutUi();
        return changed;
    }

    /// <summary>Wire the popup to the chip once, and keep its contents current.</summary>
    private void RefreshLoadoutUi()
    {
        if (Chip is null) return;
        EnsureLoadoutPopup();

        if (CurrentLoadoutLine() is string line && ChipSub is not null)
            ChipSub.Text = line;

        if (_loadoutList is null) return;
        _loadoutList.Children.Clear();

        Loadout[] previous = _loadouts.Previous.ToArray();
        if (previous.Length == 0)
        {
            _loadoutList.Children.Add(new TextBlock
            {
                Text = "No other loadouts recorded yet.\nSwap in game and read your inventory —\nthis one moves down here.",
                Foreground = B("#9AA7B4"),
                FontSize = 11,
                Margin = new Thickness(4, 2, 4, 4),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (Loadout l in previous) _loadoutList.Children.Add(LoadoutCard(l));
    }

    private void EnsureLoadoutPopup()
    {
        if (_loadoutPopup is not null) return;

        _loadoutList = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };

        var header = new TextBlock
        {
            Text = "PREVIOUS LOADOUTS",
            Foreground = B("#7E8AA0"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(12, 10, 12, 2),
        };

        var shell = new Border
        {
            Background = B("#0C1119"),
            BorderBrush = B("#2F3849"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            MinWidth = 260,
            MaxWidth = 340,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black,
            },
            Child = new StackPanel { Children = { header, _loadoutList } },
        };

        _loadoutPopup = new Popup
        {
            PlacementTarget = Chip,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -8,
            VerticalOffset = 6,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,           // we close it ourselves, so moving into it doesn't dismiss it
            Child = shell,
        };

        // Hover to open. A short close delay lets the pointer cross the gap between the chip and
        // the popup without it vanishing underneath them.
        _loadoutCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _loadoutCloseTimer.Tick += (_, _) =>
        {
            _loadoutCloseTimer!.Stop();
            if (!Chip.IsMouseOver && !shell.IsMouseOver) _loadoutPopup!.IsOpen = false;
        };

        void Open(object? s, MouseEventArgs e)
        {
            _loadoutCloseTimer!.Stop();
            if (_loadouts.Current is not null) _loadoutPopup!.IsOpen = true;
        }
        void Close(object? s, MouseEventArgs e) => _loadoutCloseTimer!.Start();

        Chip.MouseEnter += Open;
        Chip.MouseLeave += Close;
        shell.MouseEnter += Open;
        shell.MouseLeave += Close;
    }

    /// <summary>One loadout as a card: primary-class emblem on the left, classes and level right.</summary>
    private Border LoadoutCard(Loadout l)
    {
        var row = new Grid { Margin = new Thickness(10, 8, 12, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The emblem is the loadout's PRIMARY class — that is what the loadout reads as at a glance.
        FrameworkElement emblem = ClassAnim.Create(l.Primary, 64)
                                  ?? FallbackEmblem(l.Primary);
        emblem.VerticalAlignment = VerticalAlignment.Center;
        emblem.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(emblem, 0);
        row.Children.Add(emblem);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = l.Display,
            Foreground = B("#EAF3FF"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        });
        string primaryFull = ClassAnim.Canonical(l.Primary) ?? l.Primary;
        text.Children.Add(new TextBlock
        {
            Text = primaryFull + (l.Classes.Count > 1 ? " (primary)" : ""),
            Foreground = B("#7E8AA0"),
            FontSize = 10,
            Margin = new Thickness(0, 1, 0, 3),
        });
        var meta = new StackPanel { Orientation = Orientation.Horizontal };
        meta.Children.Add(new Border
        {
            Background = B("#152033"),
            BorderBrush = B("#2A3B55"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 1, 8, 1),
            Child = new TextBlock { Text = "Lv " + Math.Max(1, l.Level), Foreground = B("#B8C6D9"), FontSize = 11 },
        });
        if (!string.IsNullOrWhiteSpace(l.Race))
            meta.Children.Add(new TextBlock
            {
                Text = l.Race, Foreground = B("#7E8AA0"), FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var card = new Border
        {
            Background = B("#111826"),
            BorderBrush = B("#243044"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand,
            Child = row,
            ToolTip = $"Last seen {l.LastSeen:d MMM HH:mm} — swap to it in game and read your inventory to make it current",
        };
        card.MouseEnter += (_, _) => { card.Background = B("#162030"); card.BorderBrush = B("#3A4A66"); };
        card.MouseLeave += (_, _) => { card.Background = B("#111826"); card.BorderBrush = B("#243044"); };
        return card;
    }

    /// <summary>A plain lettered badge for a class with no emblem art — never let the menu break.</summary>
    private Border FallbackEmblem(string cls) => new()
    {
        Width = 40, Height = 64,
        Background = B("#152033"),
        BorderBrush = B("#2A3B55"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = new TextBlock
        {
            Text = (cls.Length > 0 ? cls : "?").ToUpperInvariant(),
            Foreground = B("#8FA3BF"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}
