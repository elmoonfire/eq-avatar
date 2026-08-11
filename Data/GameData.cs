using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace EQAvatar.Spike.Data;

// Game-data catalogs, adapted from EQ Legends Companion's scraped data sets
// (github.com/jmoyers/everquest-companion, MIT): 7,872 mobs with levels/zones/drops/locs,
// 32 raid targets, and the 95 Plane of Sky class quests. Shipped as gzipped JSON resources
// (~283 KB total) and parsed lazily off the UI thread on first open.

public sealed class MobEntry
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("lo")] public int? Lo { get; set; }
    [JsonPropertyName("hi")] public int? Hi { get; set; }
    [JsonPropertyName("z")] public List<string> Zones { get; set; } = new();
    [JsonPropertyName("d")] public List<string> Drops { get; set; } = new();
    [JsonPropertyName("loc")] public List<List<double?>> Locs { get; set; } = new();

    [JsonIgnore] public string LevelText => Lo is null ? "?" : Lo == Hi ? Lo.ToString()! : $"{Lo}–{Hi}";
    [JsonIgnore] public string ZonesText => Zones.Count == 0 ? "—" : string.Join(", ", Zones);
}

public sealed class BossEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
    [JsonPropertyName("zone")] public string Zone { get; set; } = "";
}

public sealed class SkyItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("who")] public List<string> Who { get; set; } = new();
    [JsonPropertyName("where")] public string Where { get; set; } = "";
    [JsonPropertyName("count")] public int? Count { get; set; }
}

public sealed class SkyQuest
{
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("giver")] public string Giver { get; set; } = "";
    [JsonPropertyName("rune")] public string Rune { get; set; } = "";
    [JsonPropertyName("reward")] public string Reward { get; set; } = "";
    [JsonPropertyName("rewardStats")] public string RewardStats { get; set; } = "";
    [JsonPropertyName("items")] public List<SkyItem> Items { get; set; } = new();
}

/// <summary>One hunting-guide row: a zone ranked by how many catalog mobs sit in the level window.</summary>
public sealed record HuntZone(string Zone, int Count, int MinLevel, int MaxLevel, List<MobEntry> Sample);

public static class GameData
{
    private static readonly object Gate = new();
    private static List<MobEntry>? _mobs;
    private static List<BossEntry>? _bosses;
    private static List<SkyQuest>? _sky;

    public static IReadOnlyList<MobEntry> Mobs { get { Ensure(); return _mobs!; } }
    public static IReadOnlyList<BossEntry> Bosses { get { Ensure(); return _bosses!; } }
    public static IReadOnlyList<SkyQuest> Sky { get { Ensure(); return _sky!; } }
    public static bool Loaded => _mobs != null;

    /// <summary>Parse all three catalogs (call off the UI thread; ~100 ms once).</summary>
    public static void Ensure()
    {
        if (_mobs != null) return;
        lock (Gate)
        {
            if (_mobs != null) return;
            _mobs = Load<List<MobEntry>>("mobs") ?? new List<MobEntry>();
            _bosses = Load<List<BossEntry>>("bosses") ?? new List<BossEntry>();
            _sky = Load<List<SkyQuest>>("posky") ?? new List<SkyQuest>();
        }
    }

    private static T? Load<T>(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/assets/data/{name}.json.gz", UriKind.Absolute);
            using Stream? res = Application.GetResourceStream(uri)?.Stream;
            if (res is null) return default;
            using var gz = new GZipStream(res, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(gz);
        }
        catch { return default; }
    }

    /// <summary>Token search over name + zones + drops; every token must hit somewhere.</summary>
    public static List<MobEntry> SearchMobs(string query, int limit = 250)
    {
        Ensure();
        string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return _mobs!.Take(limit).ToList();
        var outList = new List<MobEntry>(Math.Min(limit, 64));
        foreach (MobEntry m in _mobs!)
        {
            bool all = true;
            foreach (string t in tokens)
            {
                bool hit = m.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                        || m.Zones.Any(z => z.Contains(t, StringComparison.OrdinalIgnoreCase))
                        || m.Drops.Any(d => d.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!hit) { all = false; break; }
            }
            if (!all) continue;
            outList.Add(m);
            if (outList.Count >= limit) break;
        }
        return outList;
    }

    /// <summary>Zones ranked by mob density inside [level-3 … level+2] — a computed hunting
    /// guide from the catalog (not hand-curated; labeled as such in the UI).</summary>
    public static List<HuntZone> HuntingGuide(int level, int top = 25)
    {
        Ensure();
        int lo = level - 3, hi = level + 2;
        var byZone = new Dictionary<string, List<MobEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (MobEntry m in _mobs!)
        {
            if (m.Lo is not int mlo || m.Hi is not int mhi) continue;
            if (mhi < lo || mlo > hi) continue;
            foreach (string z in m.Zones)
            {
                if (!byZone.TryGetValue(z, out List<MobEntry>? list)) byZone[z] = list = new List<MobEntry>();
                list.Add(m);
            }
        }
        return byZone
            .Select(kv => new HuntZone(kv.Key, kv.Value.Count,
                                       kv.Value.Min(m => m.Lo!.Value), kv.Value.Max(m => m.Hi!.Value),
                                       kv.Value.OrderBy(m => m.Lo).Take(6).ToList()))
            .OrderByDescending(h => h.Count)
            .Take(top)
            .ToList();
    }
}
