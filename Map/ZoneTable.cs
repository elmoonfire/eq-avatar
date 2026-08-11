using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EQAvatar.Spike.Map;

// Long zone name -> map-file stem. Ported from EQ Legends Companion's src/shared/zones.ts
// (github.com/jmoyers/everquest-companion, MIT) — a HAND-AUTHORED table, because there is no
// algorithm: their measurement showed naive normalization resolves only 7 of 51 log-printed
// zone names ("The Plane of Sky" -> airplane, "Nagafen's Lair" -> soldungb). 128 zones:
// everything their live log printed plus the classic/Kunark/Velious corpus.
//
// Ambiguous names (bare "Freeport", "Neriak", "Qeynos" — each 2-3 map files) are deliberately
// absent: they resolve to null and the UI keeps its zone picker instead of guessing.

public static class ZoneTable
{
    public sealed record Zone(string Short, string Name);

    private static readonly List<Zone> All = new();
    private static readonly Dictionary<string, string> KeyToShort = new();

    private static void Add(string shortName, string name, params string[] aliases)
    {
        All.Add(new Zone(shortName, name));
        KeyToShort.TryAdd(ZoneKey(name), shortName);
        foreach (string a in aliases) KeyToShort.TryAdd(ZoneKey(a), shortName);
    }

    /// <summary>Every known zone (stem + display name), table order.</summary>
    public static IReadOnlyList<Zone> Zones => All;

    /// <summary>Display name for a stem, or the stem itself when unknown.</summary>
    public static string NameFor(string stem)
    {
        foreach (Zone z in All) if (z.Short == stem) return z.Name;
        return stem;
    }

    /// <summary>Map-file stem for a long zone name (log or wiki spelling), or null.</summary>
    public static string? ShortFor(string? longName)
    {
        string key = ZoneKey(longName);
        return key.Length > 0 && KeyToShort.TryGetValue(key, out string? s) ? s : null;
    }

    // The fold, ported verbatim: instance suffixes and tier parentheticals stripped, lowercased,
    // separators collapsed, one leading article dropped.
    private static readonly Regex SoloGroup = new(@"\s*-\s*(Solo|Group)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TierOrdinal = new(@"\s+\d+\s*\([^)]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex TierParen = new(@"\s+\([^)]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[\s-]+", RegexOptions.Compiled);

    public static string ZoneKey(string? zone)
    {
        string s = zone ?? "";
        s = SoloGroup.Replace(s, "");
        s = TierOrdinal.Replace(s, "");
        s = TierParen.Replace(s, "");
        s = Separators.Replace(s.ToLowerInvariant(), " ").Trim();
        if (s.StartsWith("the ")) s = s[4..].Trim();
        return s;
    }

    static ZoneTable()
    {
        Add("befallen", "Befallen");
        Add("blackburrow", "Blackburrow");
        Add("butcher", "Butcherblock Mountains");
        Add("ecommons", "East Commonlands");
        Add("freporte", "East Freeport");
        Add("erudsxing", "Erud's Crossing");
        Add("erudnext", "Erudin");
        Add("erudnint", "Erudin Palace");
        Add("everfrost", "Everfrost Peaks");
        Add("grobb", "Grobb");
        Add("highpass", "Highpass Hold");
        Add("innothule", "Innothule Swamp");
        Add("kithicor", "Kithicor Forest");
        Add("soldungb", "Nagafen's Lair");
        Add("najena", "Najena");
        Add("nektulos", "Nektulos Forest");
        Add("neriakb", "Neriak - Commons");
        Add("neriaka", "Neriak - Foreign Quarter");
        Add("newsebexp", "New Sebilis Expedition");
        Add("freportn", "North Freeport");
        Add("kaladima", "North Kaladim");
        Add("qeynos2", "North Qeynos");
        Add("oggok", "Oggok");
        Add("paineel", "Paineel");
        Add("permafrost", "Permafrost Keep", "The Permafrost Caverns");
        Add("qeytoqrg", "Qeynos Hills");
        Add("kaladimb", "South Kaladim");
        Add("qeynos", "South Qeynos");
        Add("guktop", "The City of Guk", "Upper Guk");
        Add("eastkarana", "The Eastern Plains of Karana", "East Karana", "Eastern Karana");
        Add("feerrott", "The Feerrott");
        Add("paw", "The Lair of the Splitpaw", "Splitpaw Lair", "Infected Paw");
        Add("lavastorm", "The Lavastorm Mountains");
        Add("nro", "The Northern Desert of Ro", "North Ro");
        Add("northkarana", "The Northern Plains of Karana", "North Karana", "Northern Karana");
        Add("oasis", "The Oasis of Marr");
        Add("oot", "The Ocean of Tears");
        Add("fearplane", "The Plane of Fear");
        Add("hateplane", "The Plane of Hate");
        Add("airplane", "The Plane of Sky");
        Add("rathemtn", "The Rathe Mountains", "Mountains of Rathe");
        Add("gukbottom", "The Ruins of Old Guk", "Lower Guk");
        Add("hole", "The Ruins of Old Paineel", "The Hole");
        Add("sro", "The Southern Desert of Ro", "South Ro");
        Add("southkarana", "The Southern Plains of Karana", "South Karana", "Southern Karana");
        Add("soltemple", "The Temple of Solusek Ro");
        Add("tox", "Toxxulia Forest");
        Add("commons", "West Commonlands");
        Add("freportw", "West Freeport");
        Add("akanon", "Ak'Anon");
        Add("arena", "The Arena");
        Add("barter", "The Barter Hall");
        Add("bazaar", "The Bazaar");
        Add("cauldron", "Dagnor's Cauldron");
        Add("cazicthule", "Cazic-Thule");
        Add("crushbone", "Clan Crushbone", "Crushbone");
        Add("felwithea", "North Felwithe", "Northern Felwithe");
        Add("felwitheb", "South Felwithe", "Southern Felwithe");
        Add("gfaydark", "The Greater Faydark");
        Add("guildlobby", "The Guild Lobby");
        Add("halas", "Halas");
        Add("highkeep", "High Keep", "HighKeep");
        Add("kedge", "Kedge Keep");
        Add("kerraridge", "Kerra Isle", "Kerra Island");
        Add("lakerathe", "Lake Rathetear", "Lake Rathe");
        Add("lfaydark", "The Lesser Faydark");
        Add("beholder", "Gorge of King Xorbb", "Beholder's Maze");
        Add("misty", "Misty Thicket");
        Add("mistmoore", "Castle Mistmoore", "Mistmoore Castle");
        Add("neriakc", "Neriak - Third Gate");
        Add("neriakd", "Neriak Palace");
        Add("nexus", "The Nexus");
        Add("poknowledge", "Plane of Knowledge");
        Add("qcat", "Qeynos Catacombs", "Qeynos Aqueducts");
        Add("qrg", "Surefall Glade");
        Add("rivervale", "Rivervale");
        Add("runnyeye", "Clan RunnyEye", "Runnyeye Citadel");
        Add("soldunga", "Solusek's Eye");
        Add("steamfont", "Steamfont Mountains");
        Add("stonebrunt", "Stonebrunt Mountains");
        Add("unrest", "The Estate of Unrest", "Unrest");
        Add("warrens", "The Warrens");
        Add("qey2hh1", "The Western Plains of Karana", "West Karana", "Western Karana");
        Add("burningwood", "The Burning Wood", "Burning Woods");
        Add("cabeast", "Cabilis East", "East Cabilis");
        Add("cabwest", "Cabilis West", "West Cabilis");
        Add("chardok", "Chardok");
        Add("charasis", "Howling Stones");
        Add("citymist", "City of Mist");
        Add("dalnir", "Crypt of Dalnir");
        Add("dreadlands", "The Dreadlands");
        Add("droga", "Temple of Droga");
        Add("emeraldjungle", "The Emerald Jungle");
        Add("fieldofbone", "The Field of Bone");
        Add("firiona", "Firiona Vie");
        Add("frontiermtns", "Frontier Mountains");
        Add("kaesora", "Kaesora");
        Add("karnor", "Karnor's Castle");
        Add("kurn", "Kurn's Tower");
        Add("lakeofillomen", "Lake of Ill Omen");
        Add("nurga", "Mines of Nurga");
        Add("overthere", "The Overthere");
        Add("sebilis", "Old Sebilis");
        Add("skyfire", "Skyfire Mountains");
        Add("swampofnohope", "The Swamp of No Hope");
        Add("timorous", "Timorous Deep");
        Add("trakanon", "Trakanon's Teeth");
        Add("veeshan", "Veeshan's Peak");
        Add("warslikswood", "Warslik's Woods", "Warsliks Woods");
        Add("cobaltscar", "Cobalt Scar");
        Add("crystal", "Crystal Caverns");
        Add("eastwastes", "Eastern Wastes");
        Add("frozenshadow", "Tower of Frozen Shadow");
        Add("greatdivide", "The Great Divide");
        Add("growthplane", "Plane of Growth");
        Add("iceclad", "Iceclad Ocean");
        Add("kael", "Kael Drakkel");
        Add("mischiefplane", "Plane of Mischief");
        Add("necropolis", "Dragon Necropolis");
        Add("sirens", "Siren's Grotto");
        Add("skyshrine", "Skyshrine");
        Add("sleeper", "Sleeper's Tomb");
        Add("templeveeshan", "Temple of Veeshan");
        Add("thurgadina", "Thurgadin");
        Add("thurgadinb", "Icewell Keep");
        Add("velketor", "Velketor's Labyrinth");
        Add("wakening", "The Wakening Land", "Wakening Lands");
        Add("westwastes", "Western Wastes");
    }
}
