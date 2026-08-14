using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EQAvatar.Spike.Data;

/// <summary>One stat off an item's window, and what it becomes at a given upgrade tier.</summary>
public sealed record ItemStat(string Name, double Base, bool IsWeaponDamage);

/// <summary>An item as the wiki describes it, plus enough to scale it up the upgrade ladder.</summary>
public sealed class ItemInfo
{
    public string Name { get; set; } = "";
    /// <summary>The hub's own row id, so the app can link straight to our page for it.</summary>
    public int Id { get; set; }
    public string Slot { get; set; } = "";
    public int IconId { get; set; }
    /// <summary>The item window pasted as text — kept verbatim so a stat this parser doesn't know
    /// about is still readable by a human rather than silently dropped.</summary>
    public string StatsBlock { get; set; } = "";
    public List<ItemStat> Stats { get; set; } = new();
    public DateTime Fetched { get; set; } = DateTime.Now;
    public string Url { get; set; } = "";
}

/// <summary>
/// Item facts for the Auto Merge forecast — from OUR hub, falling back to the wiki.
///
/// The hub carries the whole 10,956-item corpus in typed columns (`hub/api/gamedata.php?p=items`
/// and `?p=item&amp;id=`), which is better than re-parsing a wiki page in every way that matters:
/// AC and HP arrive as numbers instead of being fished out of prose, the row carries an id we can
/// link to our own page with, and `?icon=` returns the game's own 40×40 art so the preview is the
/// real item rather than a photograph of your bag. The wiki stays as the fallback for the two
/// cases the hub can't answer — no hub username configured, or the hub unreachable — because a
/// forecast that works offline is worth more than one that is always perfectly sourced.
///
/// WHAT IS AND ISN'T COMPUTED. The +0…+10 rules are documented on the wiki's "Item Upgrade System"
/// page and implemented here exactly as written: a cumulative +10% per tier, rounded DOWN, with a
/// guaranteed minimum of +1 per tier on reaching it; weapon damage rises at +5% instead, and
/// weapon delay never falls. Anything this parser can't classify is shown as its raw line and NOT
/// scaled — an invented number on a screen you are about to spend a week feeding would be worse
/// than a blank.
/// </summary>
public static class ItemLookup
{
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        // MediaWiki front-ends routinely 403 a request with no User-Agent, and a 403 here would
        // surface to the user as "check the spelling" — blaming them for our own missing header.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("EQAvatar/1.0 (+https://eqavatar.ldtlan.com)");
        return c;
    }
    private const string Api = "https://eqlwiki.com/api.php";

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "items");

    private static string CachePath(string name)
    {
        string safe = new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        // A hash of the ORIGINAL name, because the sanitiser alone maps "Blade of Fire",
        // "Blade-of-Fire" and "blade of fire" onto one file.
        int h = 17;
        foreach (char c in name) h = h * 31 + c;
        return Path.Combine(CacheDir, safe.ToLowerInvariant() + "_" + (h & 0x7FFFFFF).ToString("x") + ".json");
    }

    /// <summary>Cached copy, or null. Never touches the network — safe to call while rendering.</summary>
    public static ItemInfo? Cached(string name)
    {
        try
        {
            string p = CachePath(name);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    /// <summary>
    /// Look the item up: our hub first, the wiki if the hub can't answer. Throws on a network
    /// failure so the caller can say "couldn't reach it" rather than "check the spelling".
    /// </summary>
    public static async Task<ItemInfo?> FetchAsync(string name, EQAvatar.Spike.Config.AppSettings settings)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) return null;
        string root = (settings.HubUrl ?? "").Trim();
        int cut = root.IndexOf("/hub/", StringComparison.OrdinalIgnoreCase);
        root = cut > 0 ? root[..cut] : root.TrimEnd('/');

        ItemInfo? hub = null;
        if (!string.IsNullOrWhiteSpace(settings.HubUsername) && !string.IsNullOrWhiteSpace(settings.HubApiKey))
            hub = await FromHubAsync(name, new EQAvatar.Spike.Net.GameDataClient(settings), root);

        // A hub row with NO stats must not win. Our typed columns don't model regen, and the item
        // this page exists for is entirely regen — so a row that came back bare would otherwise
        // overwrite a perfectly good wiki-derived cache with "this item has no stats", forever.
        if (hub is { Stats.Count: > 0 }) { Cache(name, hub); return hub; }

        ItemInfo? wiki = await FetchWikiAsync(name);
        if (wiki is not null && hub is not null)
        {
            // Keep what only the hub knows — its row id and the game's own icon — on the wiki result.
            wiki.Id = hub.Id;
            wiki.IconId = hub.IconId;
            wiki.Url = hub.Url.Length > 0 ? hub.Url : wiki.Url;
            if (wiki.Slot.Length == 0) wiki.Slot = hub.Slot;
            Cache(name, wiki);
        }
        return wiki ?? hub;
    }

    /// <summary>
    /// The hub's typed row, through the SHARED <see cref="EQAvatar.Spike.Net.GameDataClient"/> —
    /// not a second HTTP path of our own. That client already owns the endpoint, the credentials,
    /// the disk cache and the offline fallback, and two clients for one API is two places for the
    /// hub URL to be derived slightly differently.
    ///
    /// `p=items&amp;q=` is a LIKE search, so the exact-name match is picked out here rather than
    /// trusting the first row: "Talisman of Kejaar Kerrath" and a hypothetical "Greater Talisman of
    /// Kejaar Kerrath" both come back, and forecasting the wrong one silently is worse than
    /// forecasting nothing.
    /// </summary>
    private static async Task<ItemInfo?> FromHubAsync(string name, EQAvatar.Spike.Net.GameDataClient gd, string root)
    {
        try
        {
            JsonElement? listed = await gd.GetAsync("p=items&limit=25&q=" + Uri.EscapeDataString(name),
                                                    TimeSpan.FromHours(12));
            if (listed is not { } list || !list.TryGetProperty("rows", out JsonElement rows)) return null;

            JsonElement? exact = null, first = null;
            foreach (JsonElement r in rows.EnumerateArray())
            {
                first ??= r;
                string n = r.TryGetProperty("name", out JsonElement nm) ? (nm.GetString() ?? "") : "";
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { exact = r; break; }
            }
            // A LIKE search for "Rusty Sword" returns "Rusty Sword of Doom" too. Accepting a lone
            // near-miss silently forecasts a DIFFERENT item under the name you typed, so the only
            // fallback allowed is one that at least starts with what was asked for.
            bool firstLooksRight = first is { } f0
                && (f0.TryGetProperty("name", out JsonElement fn) ? (fn.GetString() ?? "") : "")
                   .StartsWith(name, StringComparison.OrdinalIgnoreCase);
            JsonElement? row = exact ?? (rows.GetArrayLength() == 1 && firstLooksRight ? first : null);
            if (row is null) return null;

            int id = row.Value.TryGetProperty("id", out JsonElement idv) && idv.ValueKind == JsonValueKind.Number
                   ? idv.GetInt32() : 0;
            var info = new ItemInfo
            {
                Id = id,
                Name = Str(row.Value, "name") is { Length: > 0 } nn ? nn : name,
                Slot = Str(row.Value, "slot_primary"),
                IconId = EQAvatar.Spike.Net.GameDataClient.IconId(row.Value) ?? 0,
                Url = root + "/hub/gamedata.php?p=items&q=" + Uri.EscapeDataString(name),
                Fetched = DateTime.Now,
            };
            info.Stats = TypedStats(row.Value);

            // The full record carries the statsblock, which is where regen — and anything else the
            // typed columns don't model — still lives.
            if (id > 0)
            {
                JsonElement? full = await gd.GetAsync($"p=item&id={id}", TimeSpan.FromDays(7));
                if (full is { } fe && fe.TryGetProperty("item", out JsonElement it))
                {
                    info.StatsBlock = Str(it, "statsblock");
                    if (info.Slot.Length == 0) info.Slot = Str(it, "slots");
                    foreach (ItemStat extra in ParseStats(info.StatsBlock))
                        if (!info.Stats.Any(x => string.Equals(x.Name, extra.Name, StringComparison.OrdinalIgnoreCase)))
                            info.Stats.Add(extra);
                }
            }
            return info;
        }
        catch { return null; }        // the wiki fallback covers everything this can't answer
    }

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>The hub's typed columns, which need no parsing at all.</summary>
    private static List<ItemStat> TypedStats(JsonElement row)
    {
        var stats = new List<ItemStat>();
        (string Label, string Col, bool Weapon)[] map =
        {
            ("AC", "ac", false), ("HP", "hp", false), ("Mana", "mana", false), ("Endurance", "endur", false),
            ("STR", "str", false), ("STA", "sta", false), ("AGI", "agi", false), ("DEX", "dex", false),
            ("WIS", "wis", false), ("INT", "int", false), ("CHA", "cha", false),
            ("SV Magic", "mr", false), ("SV Fire", "fr", false), ("SV Cold", "cr", false),
            ("SV Disease", "dr", false), ("SV Poison", "pr", false),
            ("DMG", "dmg", true), ("Delay", "delay", false),
        };
        foreach ((string label, string col, bool weapon) in map)
            if (row.TryGetProperty(col, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
            {
                double d = v.GetDouble();
                if (d != 0) stats.Add(new ItemStat(label, d, weapon));
            }
        return stats;
    }

    private static void Cache(string name, ItemInfo info)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string dest = CachePath(name), tmp = dest + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(info));
            File.Move(tmp, dest, overwrite: true);
        }
        catch { }
    }

    /// <summary>The wiki fallback: parse the page's own statsblock.</summary>
    private static async Task<ItemInfo?> FetchWikiAsync(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) return null;
        try
        {
            string url = Api + "?action=parse&prop=wikitext&format=json&page="
                       + Uri.EscapeDataString(name.Replace(' ', '_'));
            string body = await Http.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("parse", out JsonElement parse)) return null;
            string text = parse.GetProperty("wikitext").GetProperty("*").GetString() ?? "";
            if (text.Length == 0) return null;

            var info = new ItemInfo
            {
                Name = parse.TryGetProperty("title", out JsonElement t) ? (t.GetString() ?? name) : name,
                Url = "https://eqlwiki.com/" + Uri.EscapeDataString(name.Replace(' ', '_')),
                StatsBlock = Field(text, "statsblock"),
                Slot = Field(text, "slot"),
                Fetched = DateTime.Now,
            };
            if (int.TryParse(Field(text, "lucy_img_ID"), out int icon)) info.IconId = icon;
            info.Stats = ParseStats(info.StatsBlock);
            if (info.Slot.Length == 0)
            {
                Match sm = Regex.Match(Regex.Replace(info.StatsBlock, "<[^>]+>", " "),
                                       @"Slot:\s*([A-Za-z ]+)", RegexOptions.IgnoreCase);
                if (sm.Success) info.Slot = sm.Groups[1].Value.Trim();
            }

            // Temp-then-move inside Cache(): two impatient clicks used to race on the same file, and
            // the loser's IOException was swallowed into "couldn't find it" for a fetch that worked.
            Cache(name, info);
            return info;
        }
        catch (HttpRequestException) { throw; }        // "couldn't reach the wiki" ≠ "no such item"
        catch (TaskCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>A named parameter out of the page's {{Itempage}} template.</summary>
    private static string Field(string wikitext, string key)
    {
        Match m = Regex.Match(wikitext, @"\|\s*" + Regex.Escape(key) + @"\s*=\s*(.*?)(?=\n\s*\||\n\}\}|\}\}|$)",
                              RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// Pull labelled numbers out of the statsblock.
    ///
    /// The block is the game's item window pasted as free text with &lt;br&gt; between lines, and
    /// ONE line can carry two stats ("AC: 2 Mana: +10"), so this matches labels wherever they
    /// appear rather than assuming a position.
    /// </summary>
    private static List<ItemStat> ParseStats(string block)
    {
        var stats = new List<ItemStat>();
        var claimed = new List<(int Start, int End)>();
        if (block.Length == 0) return stats;
        string flat = Regex.Replace(block, "<[^>]+>", " ");
        flat = flat.Replace("&nbsp;", " ");

        // The labels worth scaling. DMG is the weapon-damage case (+5%/tier); Delay is listed so
        // it can be SHOWN and explicitly not scaled, because "never reduced" is a fact worth
        // seeing next to the numbers that do move.
        // The wiki writes the game's own wording, so these are the game's own labels — "HP Regen",
        // not "Regeneration". Guessing the friendly name finds nothing: the Talisman of Kejaar
        // Kerrath is ENTIRELY regen stats, and a label list without them would have shown the one
        // item this page exists for as having no stats at all.
        string[] labels =
        {
            "AC", "HP Regen", "Mana Regen", "End Regen", "HP", "Mana", "Endurance",
            "DMG", "Damage", "Delay", "Attack", "Haste",
            "STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA",
            "SV Fire", "SV Cold", "SV Magic", "SV Poison", "SV Disease",
        };
        foreach (string label in labels)
        {
            // Anchored so "HP" cannot match inside "HP Regen" — the longer labels are listed first
            // and claimed first, and a shorter one that would overlap an already-claimed span is
            // skipped rather than reported as a second stat with the same number.
            Match m = Regex.Match(flat, @"(?<![A-Za-z])" + Regex.Escape(label) + @"\s*:\s*([+-]?\d+(?:\.\d+)?)",
                                  RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (claimed.Any(r => m.Index >= r.Start && m.Index < r.End)) continue;
            claimed.Add((m.Index, m.Index + m.Length));
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            bool weaponDamage = label is "DMG" or "Damage";
            if (stats.Any(x => string.Equals(x.Name, label, StringComparison.OrdinalIgnoreCase))) continue;
            stats.Add(new ItemStat(label, v, weaponDamage));
        }
        return stats;
    }

    /// <summary>
    /// A stat's value at a plus level, by the wiki's documented rules: cumulative percentage per
    /// tier, rounded down, with a guaranteed minimum of +1 per tier reached. Delay is returned
    /// unchanged — the rules say weapon delay is never reduced.
    /// </summary>
    public static double AtTier(ItemStat stat, int plus)
    {
        if (plus <= 0) return stat.Base;
        // The guaranteed +1 is a floor for stats that GROW. Applied blindly it inflates a listed
        // 0 into 10 at +10 and walks a -2 resist up toward zero, inventing an upgrade out of a
        // rounding rule.
        if (stat.Base <= 0) return stat.Base;
        if (string.Equals(stat.Name, "Delay", StringComparison.OrdinalIgnoreCase)) return stat.Base;
        double pct = stat.IsWeaponDamage ? 0.05 : 0.10;
        double v = stat.Base;
        for (int i = 0; i < plus; i++)
        {
            double next = Math.Floor(v * (1 + pct));
            if (next <= v) next = v + 1;          // the guaranteed minimum, so small stats still move
            v = next;
        }
        return v;
    }
}
