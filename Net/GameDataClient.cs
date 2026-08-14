using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Net;

/// <summary>
/// Reads the game's reference corpus from the hub instead of shipping it.
///
/// WHY NOT BUNDLE IT. `wiki_items` is 10,956 rows, the spells another 1,969, the drop records
/// 22,502, and the icon sheet 874 KB. Packed into the installer that is megabytes on every
/// download, frozen at release time, and a new release needed every time the wiki moves. Read
/// from the hub it is one reload for every client at once.
///
/// WHY IT STILL WORKS OFFLINE. Every response is written to
/// <c>%AppData%\EQAvatar\gamedata</c>. A fresh answer is served from the network and re-cached;
/// a stale one is served from disk when the network says no. The only thing you lose with the
/// hub unreachable is data you have never looked at.
///
/// ICONS. The app takes the whole 1280x960 atlas ONCE and cuts 40x40 cells out of it locally.
/// A list of 200 items would otherwise be 200 round trips. The per-icon endpoint is kept as the
/// fallback for the case where the sheet cannot be had at all.
/// </summary>
public sealed class GameDataClient
{
    private readonly AppSettings _s;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Catalog pages change only when the corpus is reloaded, which is rare.</summary>
    public static readonly TimeSpan ListTtl = TimeSpan.FromHours(12);

    public GameDataClient(AppSettings s) => _s = s;

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "gamedata");

    /// <summary>The endpoint, derived from the configured hub the same way the armory link is.</summary>
    private string Endpoint
    {
        get
        {
            string url = _s.HubUrl ?? "";
            int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
            string root = i >= 0 ? url.Substring(0, i) : url;
            if (root.Length > 0 && !root.EndsWith("/")) root += "/";
            return root + "api/gamedata.php";
        }
    }

    private string Url(string query) =>
        Endpoint + "?" + query + "&username=" + Uri.EscapeDataString((_s.HubUsername ?? "").Trim());

    private static string KeyOf(string query)
    {
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(query));
        return Convert.ToHexString(h).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// A JSON response, from the network when it can be had and from disk when it can't.
    /// Returns null only when there is neither — never throws at the caller.
    /// </summary>
    public async Task<JsonElement?> GetAsync(string query, TimeSpan? ttl = null)
    {
        Directory.CreateDirectory(CacheDir);
        string file = Path.Combine(CacheDir, KeyOf(query) + ".json");
        TimeSpan age = ttl ?? ListTtl;

        if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < age)
        {
            JsonElement? cached = ReadFile(file);
            if (cached is not null) return cached;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(query));
            req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
            using HttpResponseMessage res = await Http.SendAsync(req).ConfigureAwait(false);
            string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Stale(file);

            using var doc = JsonDocument.Parse(body);
            File.WriteAllText(file, body);
            return doc.RootElement.Clone();
        }
        catch { return Stale(file); }
    }

    /// <summary>The cached copy whatever its age — the answer when the hub is unreachable.</summary>
    private static JsonElement? Stale(string file) => File.Exists(file) ? ReadFile(file) : null;

    private static JsonElement? ReadFile(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- icons

    private static BitmapSource? _sheet;
    private static Dictionary<int, (int Col, int Row)>? _pos;
    private static int _cell = 40;
    private static bool _atlasTried;
    private static readonly Dictionary<int, BitmapSource> IconCache = new();

    private string SheetPath => Path.Combine(CacheDir, "icon-sheet.png");
    private string AtlasPath => Path.Combine(CacheDir, "icon-atlas.json");

    /// <summary>
    /// Fetch the atlas once per run. Cached on disk forever: an icon sheet is game art, it does
    /// not change between releases of anything.
    /// </summary>
    public async Task EnsureAtlasAsync()
    {
        if (_atlasTried) return;
        _atlasTried = true;
        Directory.CreateDirectory(CacheDir);

        try
        {
            if (!File.Exists(AtlasPath))
            {
                JsonElement? a = await GetAsync("p=atlas", TimeSpan.FromDays(3650)).ConfigureAwait(false);
                if (a is { } el) File.WriteAllText(AtlasPath, el.GetRawText());
            }
            if (!File.Exists(SheetPath))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Url("sheet=1"));
                req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
                using HttpResponseMessage res = await Http.SendAsync(req).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                    File.WriteAllBytes(SheetPath, await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            }

            if (File.Exists(AtlasPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(AtlasPath));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("cell", out JsonElement c) && c.TryGetInt32(out int n) && n > 0) _cell = n;
                if (root.TryGetProperty("pos", out JsonElement pos))
                {
                    var map = new Dictionary<int, (int, int)>();
                    foreach (JsonProperty p in pos.EnumerateObject())
                        if (int.TryParse(p.Name, out int id) && p.Value.GetArrayLength() >= 2)
                            map[id] = (p.Value[0].GetInt32(), p.Value[1].GetInt32());
                    _pos = map;
                }
            }
            if (File.Exists(SheetPath)) _sheet = LoadPng(SheetPath);
        }
        catch { /* the catalog is perfectly readable without pictures */ }
    }

    private static BitmapSource? LoadPng(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;      // release the file handle immediately
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// The game's own 40x40 art for an icon id, cut from the cached sheet. Null when the icon is
    /// unknown or the sheet never arrived — callers draw their placeholder and carry on.
    /// </summary>
    public BitmapSource? Icon(int? iconId)
    {
        if (iconId is not { } id || _sheet is null || _pos is null) return null;
        if (IconCache.TryGetValue(id, out BitmapSource? hit)) return hit;
        if (!_pos.TryGetValue(id, out (int Col, int Row) p)) return null;

        int x = p.Col * _cell, y = p.Row * _cell;
        if (x + _cell > _sheet.PixelWidth || y + _cell > _sheet.PixelHeight) return null;

        try
        {
            var cut = new CroppedBitmap(_sheet, new System.Windows.Int32Rect(x, y, _cell, _cell));
            cut.Freeze();
            IconCache[id] = cut;
            return cut;
        }
        catch { return null; }
    }

    /// <summary>Spell icons come through as strings, and most of them are not numbers at all —
    /// 1,257 of 1,927 are single letters that address nothing. Parse, or give up quietly.</summary>
    public static int? IconId(JsonElement row, string field = "icon")
    {
        if (!row.TryGetProperty(field, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer,
                                                                CultureInfo.InvariantCulture, out int m)) return m;
        return null;
    }

    /// <summary>How much of the corpus is on disk — shown on the Game Data dashboard.</summary>
    public static string CacheSummary()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return "nothing cached yet";
            var files = new DirectoryInfo(CacheDir).GetFiles();
            long bytes = 0;
            foreach (FileInfo f in files) bytes += f.Length;
            return $"{files.Length} cached response(s) · {bytes / 1024.0 / 1024.0:0.0} MB";
        }
        catch { return ""; }
    }
}
