using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// The game's own animated class emblems — the art that plays to the left of the gear in the
/// in-game inventory window.
///
/// WHERE THIS COMES FROM: the client defines sixteen <c>A_ClassAnim01…16</c> animations in
/// <c>uifiles/default_modern/EQUI_Animations.xml</c>, one per class in the canonical class order
/// (1 Warrior … 16 Berserker, the same order as the Loadout page's per-class level fields). Each
/// is a cycle of 64x128 frames tiled across <c>&lt;class&gt;01.tga</c> / <c>02.tga</c> in
/// <c>uifiles/default</c>, at 125 ms a frame with one long hold frame at the end — so every
/// emblem plays a short flourish and then rests for a few seconds, which is exactly what it does
/// in game. Those frames were lifted out and laid into a horizontal strip per class under
/// <c>assets/class/</c>, with <c>class-anims.json</c> carrying the per-frame durations.
///
/// They are embedded rather than fetched from the hub like the mascot art: class emblems are
/// fixed game data that will never need a refresh, and the loadout menu should not have a
/// network dependency to draw itself.
/// </summary>
public static class ClassAnim
{
    /// <summary>Canonical EQ class order — the index used by A_ClassAnimNN and by the client's
    /// per-class level fields (EQType 1028 Warrior … 1043 Berserker).</summary>
    public static readonly string[] Order =
    {
        "Warrior", "Cleric", "Paladin", "Ranger", "ShadowKnight", "Druid", "Monk", "Bard",
        "Rogue", "Shaman", "Necromancer", "Wizard", "Magician", "Enchanter", "Beastlord", "Berserker",
    };

    /// <summary>Three-letter abbreviations as the game writes them in the character header.</summary>
    private static readonly Dictionary<string, string> Abbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WAR"] = "Warrior", ["CLR"] = "Cleric", ["PAL"] = "Paladin", ["RNG"] = "Ranger",
        ["SHD"] = "ShadowKnight", ["SHK"] = "ShadowKnight", ["SK"] = "ShadowKnight",
        ["DRU"] = "Druid", ["MNK"] = "Monk", ["BRD"] = "Bard", ["ROG"] = "Rogue",
        ["SHM"] = "Shaman", ["NEC"] = "Necromancer", ["WIZ"] = "Wizard", ["MAG"] = "Magician",
        ["ENC"] = "Enchanter", ["BST"] = "Beastlord", ["BER"] = "Berserker",
    };

    private sealed class Meta
    {
        public int index { get; set; }
        public int frames { get; set; }
        public int w { get; set; }
        public int h { get; set; }
        public List<int> durations { get; set; } = new();
        public string strip { get; set; } = "";
    }

    private static Dictionary<string, Meta>? _meta;
    private static readonly Dictionary<string, BitmapSource> _strips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"PAL" or "Paladin" or "paladin" → "Paladin"; null when it isn't a class.</summary>
    public static string? Canonical(string? cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return null;
        string t = cls.Trim();
        if (Abbrev.TryGetValue(t, out string? full)) return full;
        foreach (string name in Order)
            if (name.Equals(t, StringComparison.OrdinalIgnoreCase)) return name;
        // "Shadow Knight" and friends
        string squashed = t.Replace(" ", "");
        foreach (string name in Order)
            if (name.Equals(squashed, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    private static Dictionary<string, Meta> LoadMeta()
    {
        if (_meta is not null) return _meta;
        try
        {
            using Stream? s = Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/class/class-anims.json"))?.Stream;
            if (s is not null)
            {
                _meta = JsonSerializer.Deserialize<Dictionary<string, Meta>>(s) ?? new();
                return _meta;
            }
        }
        catch { /* fall through to an empty set — callers degrade to a text badge */ }
        return _meta = new Dictionary<string, Meta>();
    }

    private static BitmapSource? Strip(string cls, Meta m)
    {
        if (_strips.TryGetValue(cls, out BitmapSource? cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/assets/class/" + m.strip);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _strips[cls] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// An <see cref="Image"/> that plays the class emblem, honouring the client's own per-frame
    /// durations (including the long rest frame). Returns null if the class isn't recognised or
    /// its art is missing, so callers can fall back to something plain rather than crash — this
    /// draws inside the title bar, and a throw there would take the window with it.
    /// </summary>
    public static FrameworkElement? Create(string? className, double height = 96)
    {
        string? cls = Canonical(className);
        if (cls is null) return null;
        Dictionary<string, Meta> meta = LoadMeta();
        if (!meta.TryGetValue(cls, out Meta? m) || m.frames <= 0) return null;
        BitmapSource? strip = Strip(cls, m);
        if (strip is null) return null;

        double scale = height / m.h;
        var img = new Image
        {
            Width = m.w * scale,
            Height = height,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            Source = Frame(strip, m, 0),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        if (m.frames == 1) return img;

        int i = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(40, m.durations.Count > 0 ? m.durations[0] : 125)),
        };
        timer.Tick += (_, _) =>
        {
            i = (i + 1) % m.frames;
            img.Source = Frame(strip, m, i);
            timer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(40, i < m.durations.Count ? m.durations[i] : 125));
        };
        // Only run while the emblem is actually on screen — the loadout menu is a popup, and a
        // dozen of these ticking behind a closed popup is pure waste.
        img.Loaded += (_, _) => timer.Start();
        img.Unloaded += (_, _) => timer.Stop();
        img.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) timer.Start(); else timer.Stop(); };
        return img;
    }

    private static readonly Dictionary<(string, int), CroppedBitmap> _frames = new();

    private static BitmapSource Frame(BitmapSource strip, Meta m, int i)
    {
        var key = (m.strip, i);
        if (_frames.TryGetValue(key, out CroppedBitmap? c)) return c;
        var cb = new CroppedBitmap(strip, new Int32Rect(i * m.w, 0, m.w, m.h));
        cb.Freeze();
        _frames[key] = cb;
        return cb;
    }
}
