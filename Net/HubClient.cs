using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Net;

/// <summary>
/// What the hub sends back to a check-in. The first four fields mirror the JSON the
/// server returns; the rest are filled in client-side so the UI can show what happened.
/// </summary>
public sealed class HubResponse
{
    public bool Authorized { get; set; }
    public string? Tier { get; set; }
    public string[]? Roles { get; set; }
    public int Interval { get; set; }
    public string? Message { get; set; }

    // client-side
    public bool NetworkOk { get; set; }
    public string? Error { get; set; }
    public DateTime When { get; set; }

    public string RolesText => Roles is { Length: > 0 } ? string.Join(", ", Roles) : "—";
}

/// <summary>One past hub connection, as grouped by the server (roles used, work done, where from).</summary>
public sealed class ConnRow
{
    public long Start { get; set; }
    public long End { get; set; }
    public int Actions { get; set; }
    public int Seconds { get; set; }
    public int Kills { get; set; }
    public int Xp { get; set; }
    public int Checkins { get; set; }
    public string[]? Roles { get; set; }
    public string? Ip { get; set; }

    public DateTime StartLocal => DateTimeOffset.FromUnixTimeSeconds(Start).LocalDateTime;
    public string WhenText => StartLocal.ToString("MMM d  HH:mm");
    public string DurText
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(Seconds, End - Start));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, t.Minutes)}m";
        }
    }
    public string RolesText => Roles is { Length: > 0 } ? string.Join(", ", Roles) : "idle";
    public string ActionsText => Actions.ToString("N0");
    public string KillsText => Kills.ToString("N0");
    public string XpText => Xp.ToString("N0");
    public string FromText => Ip ?? "";
}

/// <summary>
/// Talks to the EQ Avatar Client Hub (/hub/api.php). On each check-in it POSTs the
/// character name, machine, app version, current role, and the *delta* in actions / seconds /
/// kills / xp since the last successful check-in — the server keeps the running totals, so we
/// only ever send the increment. The delta baseline is committed only when a post succeeds, so
/// a dropped network call never loses activity; it just rolls into the next check-in.
/// </summary>
public sealed class HubClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _s;

    private int _sentActions, _sentKills, _sentXp;
    private DateTime _lastCheckIn = DateTime.Now;   // wall-clock base for reported seconds

    public HubResponse? Last { get; private set; }

    public HubClient(AppSettings settings) => _s = settings;

    public string Machine =>
        string.IsNullOrWhiteSpace(_s.HubMachine) ? Environment.MachineName : _s.HubMachine.Trim();

    /// <summary>
    /// POST one check-in. Pass the app-session cumulative counters; the client turns them into
    /// deltas. A counter that went backwards (e.g. the grind role was restarted) is treated as a
    /// fresh count rather than a negative delta.
    /// </summary>
    public async Task<HubResponse> CheckIn(string role, int cumActions, int cumKills, int cumXp, CancellationToken ct = default)
    {
        int dA = cumActions >= _sentActions ? cumActions - _sentActions : cumActions;
        int dK = cumKills   >= _sentKills   ? cumKills   - _sentKills   : cumKills;
        int dX = cumXp      >= _sentXp      ? cumXp      - _sentXp      : cumXp;
        int secs = Math.Max(0, (int)(DateTime.Now - _lastCheckIn).TotalSeconds);

        var payload = new
        {
            api_key  = _s.HubApiKey,
            username = (_s.HubUsername ?? "").Trim(),
            machine  = Machine,
            version  = AppSettings.AppVersion,
            role,
            actions  = dA,
            seconds  = secs,
            kills    = dK,
            xp       = dX,
            @class   = _s.HubClass ?? "",
            level    = _s.HubLevel,
            race     = _s.HubRace ?? "",
            server   = _s.HubServer ?? "",
        };

        HubResponse r;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _s.HubUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);

            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            r = Parse(body);
            r.NetworkOk = true;
            if (r.Authorized)
            {
                _sentActions = cumActions;
                _sentKills   = cumKills;
                _sentXp      = cumXp;
                _lastCheckIn = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            r = new HubResponse { NetworkOk = false, Authorized = false, Error = ex.Message };
        }
        r.When = DateTime.Now;
        Last = r;
        return r;
    }

    /// <summary>POST the OCR'd character sheet to the hub as real_stats — the profile page
    /// flips to "read from in-game" and renders these numbers instead of estimates.</summary>
    public async Task<(bool ok, string message)> SendStats(object realStats, CancellationToken ct = default)
    {
        var payload = new
        {
            api_key   = _s.HubApiKey,
            username  = (_s.HubUsername ?? "").Trim(),
            machine   = Machine,
            version   = AppSettings.AppVersion,
            role      = "Idle",
            actions   = 0, seconds = 0, kills = 0, xp = 0,
            @class    = _s.HubClass ?? "",
            level     = _s.HubLevel,
            race      = _s.HubRace ?? "",
            server    = _s.HubServer ?? "",
            real_stats = realStats,
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _s.HubUrl)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            HubResponse r = Parse(body);
            return r.Authorized ? (true, "Profile updated — stats read from in-game.")
                                : (false, r.Message ?? r.Error ?? "hub declined the update");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Fetch this character's last 10 hub connections (server groups check-ins: a 15-minute
    /// silence starts a new connection). Null = network problem or unauthorized.
    /// </summary>
    public async Task<List<ConnRow>?> GetHistory(CancellationToken ct = default)
    {
        var payload = new
        {
            api_key  = _s.HubApiKey,
            username = (_s.HubUsername ?? "").Trim(),
            history  = 1,
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _s.HubUrl)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            HistoryResponse? h = JsonSerializer.Deserialize<HistoryResponse>(body, opts);
            return h is { Authorized: true } ? (h.History ?? new List<ConnRow>()) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Publish the equipment the app just read off the game screen, for one loadout.
    ///
    /// Gear goes to its own endpoint rather than riding along with the stats check-in, because
    /// it is keyed per LOADOUT: every EQ Legends loadout carries its own full 23-slot set, so
    /// equipment stored against the character alone would smear three of them together. The
    /// icons travel as base64 PNGs — the game's own 40x40 pixels, which is what makes the
    /// armory's icons match the inventory instead of approximating it.
    /// </summary>
    public async Task<(bool ok, string message)> SendEquipment(
        IReadOnlyList<string> classes, int level, string? race,
        IEnumerable<(int Id, string Name, bool Occupied, byte[]? IconPng, string? IconHash)> slots,
        CancellationToken ct = default)
    {
        if (classes.Count == 0) return (false, "no loadout to attach the gear to");

        var payload = new
        {
            api_key  = _s.HubApiKey,
            username = (_s.HubUsername ?? "").Trim(),
            loadout  = new { classes, level, race = race ?? "" },
            slots    = slots.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                occupied = s.Occupied,
                hash = s.IconHash,
                png = s.IconPng is null ? null : Convert.ToBase64String(s.IconPng),
            }).ToArray(),
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, EquipmentUrl())
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
            using var resp = await Http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return (false, $"hub said {(int)resp.StatusCode}: {Trim(body)}");
            using JsonDocument doc = JsonDocument.Parse(body);
            int filled = doc.RootElement.TryGetProperty("filled", out JsonElement f) ? f.GetInt32() : 0;
            int icons = doc.RootElement.TryGetProperty("icons", out JsonElement i) ? i.GetInt32() : 0;
            return (true, $"equipment published — {filled} slots filled, {icons} icons stored");
        }
        catch (Exception ex) { return (false, "equipment upload failed: " + ex.Message); }
    }

    /// <summary>HubUrl points at the check-in endpoint (…/hub/api.php); gear lives beside it.</summary>
    private string EquipmentUrl()
    {
        string url = _s.HubUrl ?? "";
        int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
        string root = i >= 0 ? url[..i] : (url.EndsWith("/") ? url : url + "/");
        return root + "api/equipment.php";
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120];

    private sealed class HistoryResponse
    {
        public bool Authorized { get; set; }
        public List<ConnRow>? History { get; set; }
    }

    private static HubResponse Parse(string body)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<HubResponse>(body, opts)
                   ?? new HubResponse { Error = "empty response" };
        }
        catch (Exception ex)
        {
            return new HubResponse { Error = "unreadable response: " + ex.Message };
        }
    }
}
