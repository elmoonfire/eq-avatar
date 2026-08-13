using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EQAvatar.Spike.Data;

/// <summary>One coordinate the wiki names in a quest's walkthrough — an NPC's spot, a mob's
/// spawn, or an unattributed landmark. Same axis convention as the log: X = east/west,
/// Y = north/south, Z = altitude.</summary>
public sealed class QuestLoc
{
    [JsonPropertyName("who")] public string Who { get; set; } = "";
    /// <summary>"npc", "spawn" or "spot".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }

    [JsonIgnore] public string LocText => $"{X:0.##}, {Y:0.##}, {Z:0.##}";
}

/// <summary>An item handed to an NPC to advance or finish a quest. This is the row the Quest
/// Runner automates: it is the only part of a quest that is a fixed, repeatable gesture.</summary>
public sealed class QuestTurnIn
{
    [JsonPropertyName("item")] public string Item { get; set; } = "";
    [JsonPropertyName("qty")] public int Qty { get; set; } = 1;
    [JsonPropertyName("npc")] public string Npc { get; set; } = "";
}

public sealed class QuestFaction
{
    [JsonPropertyName("faction")] public string Faction { get; set; } = "";
    [JsonPropertyName("delta")] public int Delta { get; set; }
}

/// <summary>One quest, as parsed from its eqlwiki.com page.</summary>
public sealed class QuestInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("startZone")] public string StartZone { get; set; } = "";
    [JsonPropertyName("startNpc")] public string StartNpc { get; set; } = "";
    [JsonPropertyName("endZone")] public string EndZone { get; set; } = "";
    [JsonPropertyName("endNpc")] public string EndNpc { get; set; } = "";
    [JsonPropertyName("levelMin")] public int LevelMin { get; set; }
    [JsonPropertyName("levelText")] public string LevelText { get; set; } = "";
    [JsonPropertyName("classes")] public List<string> Classes { get; set; } = new();
    [JsonPropertyName("relatedZones")] public List<string> RelatedZones { get; set; } = new();
    [JsonPropertyName("relatedNpcs")] public List<string> RelatedNpcs { get; set; } = new();
    [JsonPropertyName("rewards")] public List<string> Rewards { get; set; } = new();
    [JsonPropertyName("itemsNeeded")] public List<string> ItemsNeeded { get; set; } = new();
    [JsonPropertyName("turnIns")] public List<QuestTurnIn> TurnIns { get; set; } = new();
    [JsonPropertyName("locs")] public List<QuestLoc> Locs { get; set; } = new();
    [JsonPropertyName("factions")] public List<QuestFaction> Factions { get; set; } = new();
    [JsonPropertyName("expText")] public string ExpText { get; set; } = "";
    [JsonPropertyName("era")] public string Era { get; set; } = "";
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();

    [JsonIgnore] public string RewardText => Rewards.Count == 0 ? "—" : string.Join(", ", Rewards);
    [JsonIgnore] public string ClassText => Classes.Count == 0 ? "—" : string.Join(", ", Classes);
    /// <summary>Can this quest be driven by the Quest Runner at all? Only turn-ins are a fixed
    /// gesture; a "go kill six of these" step is the Grind role's job, not this one.</summary>
    [JsonIgnore] public bool Automatable => TurnIns.Count > 0;

    /// <summary>The coordinate for an NPC named in this quest, when the wiki gave one.</summary>
    public QuestLoc? LocFor(string who)
    {
        if (string.IsNullOrWhiteSpace(who)) return null;
        string w = QuestCatalog.Norm(who);
        return Locs.FirstOrDefault(l => QuestCatalog.Norm(l.Who) == w)
            ?? Locs.FirstOrDefault(l => w.Contains(QuestCatalog.Norm(l.Who)) || QuestCatalog.Norm(l.Who).Contains(w));
    }
}

/// <summary>The whole payload as published at /hub/api/quests.json.</summary>
public sealed class QuestPayload
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("generated")] public string Generated { get; set; } = "";
    [JsonPropertyName("questCount")] public int QuestCount { get; set; }
    [JsonPropertyName("zones")] public List<string> Zones { get; set; } = new();
    [JsonPropertyName("npcs")] public List<string> Npcs { get; set; } = new();
    [JsonPropertyName("quests")] public List<QuestInfo> Quests { get; set; } = new();
}

/// <summary>
/// The quest catalog: every quest on eqlwiki.com, parsed once on the server into
/// <c>/hub/api/quests.json</c> and mirrored here.
///
/// WHY IT IS NOT SCRAPED IN THE APP. The wiki is a MediaWiki whose pages are prose; turning 900
/// of them into rows takes a parser that will need fixing as pages are edited, and doing that in
/// every copy of the app would mean every user re-fetching 900 pages to see the same result. The
/// server does it once; the app downloads one file. That also means the zone and NPC lists behind
/// the page's dropdown filters are the REAL sets present in the data, not a hand-typed list that
/// drifts out of step with it.
///
/// The cache is checked against a tiny sidecar (<c>quests-meta.json</c>, ~180 bytes) so a normal
/// launch costs one small request and the half-megabyte body is only pulled when the catalog has
/// actually been rebuilt.
/// </summary>
public static class QuestCatalog
{
    private const string MetaUrl = "https://eqavatar.ldtlan.com/hub/api/quests-meta.json";
    private const string DataUrl = "https://eqavatar.ldtlan.com/hub/api/quests.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static QuestPayload? _payload;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "quests.json");

    public static bool Loaded => _payload is not null;
    public static IReadOnlyList<QuestInfo> Quests => _payload?.Quests ?? (IReadOnlyList<QuestInfo>)Array.Empty<QuestInfo>();
    public static IReadOnlyList<string> Zones => _payload?.Zones ?? (IReadOnlyList<string>)Array.Empty<string>();
    public static string Source => _payload?.Source ?? "eqlwiki.com";

    /// <summary>When the catalog was built from the wiki (UTC), or null if nothing is loaded.</summary>
    public static DateTime? Generated
    {
        get
        {
            if (_payload is null) return null;
            return DateTime.TryParse(_payload.Generated, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.AdjustToUniversal
                                     | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime d)
                ? d : null;
        }
    }

    /// <summary>Alphanumeric-lowercase form used for every name comparison, so the wiki's
    /// backticks, apostrophes and spacing can't break a lookup.</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // These strings come off the network, so the stack allocation gets a hard ceiling: a
        // stack overflow is the one failure this app cannot catch and report.
        if (s.Length > 512) s = s[..512];
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>Load the cached copy, then refresh from the hub if the published build differs.
    /// Never throws: with no cache and no network the page simply shows nothing to filter.</summary>
    public static async Task<(bool ok, string status)> EnsureAsync(bool force = false)
    {
        string cachedMd5 = "";
        if (_payload is null && File.Exists(CachePath))
        {
            try
            {
                _payload = JsonSerializer.Deserialize<QuestPayload>(await File.ReadAllTextAsync(CachePath), JsonOpts);
                if (_payload?.Quests is null) _payload = null; else Harden(_payload);
                cachedMd5 = Md5OfFile(CachePath);
            }
            catch { _payload = null; }
        }
        else if (File.Exists(CachePath))
        {
            cachedMd5 = Md5OfFile(CachePath);
        }

        // The sidecar is an optimisation, never a gate: if it 404s during a hub rebuild, or a
        // captive proxy hands back HTML, fall through to the full download rather than reporting
        // "offline" and quietly never updating again.
        if (!force && _payload is not null)
        {
            try
            {
                string metaJson = await Http.GetStringAsync(MetaUrl);
                using JsonDocument meta = JsonDocument.Parse(metaJson);
                string publishedMd5 = meta.RootElement.TryGetProperty("md5", out JsonElement m) ? (m.GetString() ?? "") : "";
                if (publishedMd5.Length > 0 && string.Equals(publishedMd5, cachedMd5, StringComparison.OrdinalIgnoreCase))
                    return (true, $"{Quests.Count} quests — up to date.");
            }
            catch { /* fall through to the full download */ }
        }

        try
        {
            string body = await Http.GetStringAsync(DataUrl);
            QuestPayload? fresh = JsonSerializer.Deserialize<QuestPayload>(body, JsonOpts);
            if (fresh is null || fresh.Quests is null || fresh.Quests.Count == 0)
                return (_payload is not null, "the hub returned no quests — showing the cached copy.");

            Harden(fresh);
            _payload = fresh;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, body);
            }
            catch { /* a read-only profile still gets a working page, just no cache */ }
            return (true, $"{fresh.Quests.Count} quests downloaded from the hub.");
        }
        catch (Exception ex)
        {
            if (_payload is null)
                return (false, "couldn't reach the hub and there's no cached catalog yet: " + ex.Message);
            // Don't print File.GetLastWriteTime blindly: with the payload in memory but the file
            // deleted it returns the year-1601 sentinel, which reads as data corruption.
            string when = File.Exists(CachePath) ? $" cached on {File.GetLastWriteTime(CachePath):d}" : " already loaded";
            return (true, $"offline — showing the copy{when}.");
        }
    }

    /// <summary>
    /// Make a downloaded payload safe to render.
    ///
    /// System.Text.Json writes an explicit JSON <c>null</c> straight over a property initializer,
    /// so <c>"rewards": null</c> in the feed leaves <c>Rewards</c> null despite <c>= new()</c> and
    /// the first row that draws it throws. The page can survive a stale catalog; it cannot survive
    /// an unhandled exception on the render path, so the nulls are flattened once, here, on the way
    /// in — where there is exactly one of them to get wrong.
    /// </summary>
    private static void Harden(QuestPayload p)
    {
        p.Zones ??= new List<string>();
        p.Npcs ??= new List<string>();
        p.Source ??= "";
        p.Generated ??= "";
        p.Quests.RemoveAll(q => q is null);
        foreach (QuestInfo q in p.Quests)
        {
            q.Name ??= ""; q.Url ??= "";
            q.StartZone ??= ""; q.StartNpc ??= "";
            q.EndZone ??= ""; q.EndNpc ??= "";
            q.LevelText ??= ""; q.ExpText ??= ""; q.Era ??= "";
            q.Classes ??= new List<string>();
            q.RelatedZones ??= new List<string>();
            q.RelatedNpcs ??= new List<string>();
            q.Rewards ??= new List<string>();
            q.ItemsNeeded ??= new List<string>();
            q.Categories ??= new List<string>();
            q.TurnIns ??= new List<QuestTurnIn>();
            q.Locs ??= new List<QuestLoc>();
            q.Factions ??= new List<QuestFaction>();
            q.TurnIns.RemoveAll(t => t is null);
            q.Locs.RemoveAll(l => l is null);
            q.Factions.RemoveAll(f => f is null);
            foreach (QuestTurnIn t in q.TurnIns) { t.Item ??= ""; t.Npc ??= ""; }
            foreach (QuestLoc l in q.Locs) l.Who ??= "";
            foreach (QuestFaction f in q.Factions) f.Faction ??= "";
        }
    }

    private static string Md5OfFile(string path)
    {
        try
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    public static QuestInfo? Find(string name) =>
        Quests.FirstOrDefault(q => Norm(q.Name) == Norm(name));
}
