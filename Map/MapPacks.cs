using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EQAvatar.Spike.Map;

// Map-pack discovery + per-layer cross-pack resolution — C# port of EQ Legends Companion's
// src/main/maps/packs.ts (github.com/jmoyers/everquest-companion, MIT).
//
// The system: `<eqRoot>\maps` is the "default" pack (the game's own, EQL-authoritative
// geometry); every subdirectory of it (`brewall`) is another pack. The pack choice is PER
// LAYER, because the two corpora are lopsided in opposite directions — measured by the
// Companion: the default set holds 285 label points across 58 `_1` files while Brewall holds
// 26,607 across 562. So "Auto" reads GEOMETRY from the default pack and LABELS from Brewall,
// and either half can be overridden. `MapData.Sources` records what actually got used.
//
// <eqRoot> is READ-ONLY, always: EverQuest rewrites ~100 default maps on every launch, so
// nothing here ever writes into the game directory.

/// <summary>One installed map pack.</summary>
public sealed record MapPack(string Id, string Name, string Dir, int ZoneCount, int FileCount);

/// <summary>Per-layer pack preference. Null = Auto (the resolution order does the right thing).</summary>
public sealed record MapPackPrefs(string? Geometry = null, string? Labels = null);

public sealed class PackIndex
{
    public MapPack Pack = null!;
    /// <summary>lowercased stem → layer → the file's REAL name (casing preserved for the read).</summary>
    public readonly Dictionary<string, Dictionary<int, string>> Files = new();
}

public sealed record LayerPick(int Layer, string PackId, string File, string Path);

public static class MapPacksUtil
{
    public const string DefaultPackId = "default";
    public const string DefaultPackName = "Game default maps";
    private static readonly int[] Layers = { 0, 1, 2, 3 };
    /// <summary>Layers sourced from the LABELS pack: 1 = POIs (the search corpus), 2 = legend.</summary>
    private static readonly int[] LabelLayers = { 1, 2 };

    /// <summary>Split "Thurgadina1_1.txt" → (stem "thurgadina1", layer 1). Anchored to only
    /// _1/_2/_3 so a stem's own trailing digit can never be eaten as a layer.</summary>
    public static (string stem, int layer)? SplitMapFileName(string name)
    {
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
        string baseName = name[..^4].ToLowerInvariant();
        if (baseName.Length == 0) return null;
        if (baseName.Length >= 2 && baseName[^2] == '_' && baseName[^1] is '1' or '2' or '3')
        {
            string stem = baseName[..^2];
            if (stem.Length == 0) return null;
            return (stem, baseName[^1] - '0');
        }
        return (baseName, 0);
    }

    public static PackIndex? IndexPackDir(string dir, string id, string name)
    {
        if (!Directory.Exists(dir)) return null;
        var idx = new PackIndex();
        int fileCount = 0;
        IEnumerable<string> names;
        try { names = Directory.EnumerateFiles(dir).Select(System.IO.Path.GetFileName)!; }
        catch { return null; }
        foreach (string? file in names)
        {
            if (file is null) continue;
            var split = SplitMapFileName(file);
            if (split is null) continue;
            fileCount++;
            if (!idx.Files.TryGetValue(split.Value.stem, out var byLayer))
                idx.Files[split.Value.stem] = byLayer = new Dictionary<int, string>();
            // First spelling wins — deterministic on a case-sensitive filesystem.
            if (!byLayer.ContainsKey(split.Value.layer)) byLayer[split.Value.layer] = file;
        }
        if (fileCount == 0) return null;
        idx.Pack = new MapPack(id, name, dir, idx.Files.Count, fileCount);
        return idx;
    }

    /// <summary>Every pack under eqRoot\maps: the default pack first (authoritative geometry),
    /// then each subdirectory (brewall, …).</summary>
    public static List<PackIndex> DiscoverPacks(string? eqRoot)
    {
        var outPacks = new List<PackIndex>();
        if (string.IsNullOrWhiteSpace(eqRoot)) return outPacks;
        string mapsDir = System.IO.Path.Combine(eqRoot, "maps");
        PackIndex? def = IndexPackDir(mapsDir, DefaultPackId, DefaultPackName);
        if (def != null) outPacks.Add(def);
        try
        {
            foreach (string sub in Directory.Exists(mapsDir) ? Directory.EnumerateDirectories(mapsDir) : Enumerable.Empty<string>())
            {
                string id = System.IO.Path.GetFileName(sub).ToLowerInvariant();
                if (id == DefaultPackId || id.Length == 0) continue;
                PackIndex? idx = IndexPackDir(sub, id, System.IO.Path.GetFileName(sub));
                if (idx != null) outPacks.Add(idx);
            }
        }
        catch { /* an unreadable maps dir is the fresh-machine case, not an error */ }
        return outPacks;
    }

    /// <summary>Packs in preference order for one layer. Geometry keeps discovery order
    /// (default first). LABELS INVERT IT (Brewall's 26,607 points vs default's 285). An
    /// explicit preference that names a real pack always goes first.</summary>
    public static List<PackIndex> PackOrder(IReadOnlyList<PackIndex> packs, int layer, MapPackPrefs prefs)
    {
        bool labels = LabelLayers.Contains(layer);
        List<PackIndex> ordered = labels
            ? packs.Where(p => p.Pack.Id != DefaultPackId).Concat(packs.Where(p => p.Pack.Id == DefaultPackId)).ToList()
            : packs.ToList();
        string? wanted = labels ? prefs.Labels : prefs.Geometry;
        if (wanted != null)
        {
            int at = ordered.FindIndex(p => p.Pack.Id == wanted);
            if (at > 0) { PackIndex p = ordered[at]; ordered.RemoveAt(at); ordered.Insert(0, p); }
        }
        return ordered;
    }

    /// <summary>Pick the pack that supplies one layer of one zone: an explicitly preferred pack
    /// that has the file wins outright; else the first pack with a NON-EMPTY file; else the
    /// first that has it at all (an empty layer file is a valid empty layer).</summary>
    public static LayerPick? ResolveLayer(IReadOnlyList<PackIndex> packs, string zone, int layer, MapPackPrefs prefs)
    {
        string? wanted = LabelLayers.Contains(layer) ? prefs.Labels : prefs.Geometry;
        LayerPick? fallback = null;
        foreach (PackIndex p in PackOrder(packs, layer, prefs))
        {
            if (!p.Files.TryGetValue(zone, out var byLayer) || !byLayer.TryGetValue(layer, out string? file)) continue;
            var pick = new LayerPick(layer, p.Pack.Id, file, System.IO.Path.Combine(p.Pack.Dir, file));
            if (p.Pack.Id == wanted) return pick;
            fallback ??= pick;
            try { if (new FileInfo(pick.Path).Length > 0) return pick; } catch { /* keep looking */ }
        }
        return fallback;
    }

    public static List<LayerPick> ResolveZoneLayers(IReadOnlyList<PackIndex> packs, string zone, MapPackPrefs prefs)
        => Layers.Select(l => ResolveLayer(packs, zone, l, prefs)).Where(p => p != null).Cast<LayerPick>().ToList();
}

/// <summary>The read-only library the Maps panel calls: pack list, zone list, parsed-zone LRU.</summary>
public sealed class MapLibrary
{
    private const int CacheMax = 8;
    private readonly string? _eqRoot;
    private List<PackIndex>? _packs;
    private readonly Dictionary<string, MapData> _cache = new();
    private readonly List<string> _lru = new();

    public MapLibrary(string? eqRoot) => _eqRoot = eqRoot;

    public List<PackIndex> Packs() => _packs ??= MapPacksUtil.DiscoverPacks(_eqRoot);

    public List<string> Zones()
    {
        var stems = new SortedSet<string>();
        foreach (PackIndex p in Packs())
            foreach (string stem in p.Files.Keys) stems.Add(stem);
        return stems.ToList();
    }

    public MapData? Get(string zone, MapPackPrefs prefs)
    {
        string key = $"{prefs.Geometry}|{prefs.Labels}|{zone}";
        if (_cache.TryGetValue(key, out MapData? hit))
        {
            _lru.Remove(key); _lru.Add(key);
            return hit;
        }
        List<LayerPick> picks = MapPacksUtil.ResolveZoneLayers(Packs(), zone, prefs);
        if (picks.Count == 0) return null;
        var parts = new List<MapParseResult>();
        var sources = new List<MapSource>();
        foreach (LayerPick pick in picks)
        {
            string? text;
            try { text = File.ReadAllText(pick.Path); } catch { continue; }   // drop the layer, keep the map
            parts.Add(EqMapParser.ParseMapText(text, pick.Layer));
            sources.Add(new MapSource(pick.Layer, pick.PackId, pick.File));
        }
        if (parts.Count == 0) return null;
        MapData data = EqMapParser.BuildMapData(parts, zone, sources);
        _cache[key] = data; _lru.Add(key);
        if (_lru.Count > CacheMax) { _cache.Remove(_lru[0]); _lru.RemoveAt(0); }
        return data;
    }

    public void Refresh() { _packs = null; _cache.Clear(); _lru.Clear(); }
}
