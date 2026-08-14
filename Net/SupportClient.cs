using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Diag;

namespace EQAvatar.Spike.Net;

/// <summary>One of your tickets, as the queue lists it.</summary>
public sealed class SupportTicketRow
{
    public int Id { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    [JsonPropertyName("status_label")] public string? StatusLabel { get; set; }
    public bool Done { get; set; }
    public long Created { get; set; }

    public DateTime CreatedLocal => DateTimeOffset.FromUnixTimeSeconds(Created).LocalDateTime;
    public string WhenText => CreatedLocal.ToString("MMM d");
    public string Glyph => Kind switch
    {
        "crash" => "\U0001F4A5", "question" => "❓", "idea" => "\U0001F4A1", _ => "\U0001F41E",
    };
}

/// <summary>One post on a ticket. Officer replies come back with <see cref="Staff"/> set.</summary>
public sealed class SupportMessage
{
    public string? Author { get; set; }
    public bool Staff { get; set; }
    public string? Body { get; set; }
    public long Created { get; set; }

    public DateTime CreatedLocal => DateTimeOffset.FromUnixTimeSeconds(Created).LocalDateTime;
}

/// <summary>A ticket with everything the app is allowed to see. Internal officer notes never
/// appear here — the hub does not send them, whatever is asked for.</summary>
public sealed class SupportTicket
{
    public int Id { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Status { get; set; }
    [JsonPropertyName("status_label")] public string? StatusLabel { get; set; }
    public bool Done { get; set; }
    public long Created { get; set; }
    public long Updated { get; set; }
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    public string? Os { get; set; }
    public string? Screen { get; set; }

    public List<SupportMessage> Messages { get; set; } = new();
}

/// <summary>A published release, for "what has shipped since".</summary>
public sealed class ReleaseRow
{
    public string? Tag { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public long When { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Talks to the hub's support endpoint (<c>/hub/api/support.php</c>) — Phase 4 of the web
/// programme. Raising a ticket from in here is the whole point of the feature: a report filed
/// from the machine that broke arrives with the version, the operating system and the screen it
/// actually broke on, and nobody has to be asked for any of them.
///
/// WHAT THE HUB DOES WITH IT. The hub keeps the full report — the metrics, the account, the
/// address — and files a SANITIZED issue on the private support repo carrying only title,
/// description, app version, OS and a link back. That decision lives on the server, in one
/// function, so nothing in this client can widen it by accident.
///
/// AUTH is the same shared key and character name every other hub call uses, so this needs no new
/// credential and no new setting. <c>/hub/api/</c> is the one path Cloudflare's challenge skips,
/// which is why the endpoint lives there and not beside the members pages.
/// </summary>
public sealed class SupportClient
{
    // Its own HttpClient with a longer fuse than the check-in's: a member pressing "send" will
    // wait a few seconds for an answer, where a check-in that hangs holds up the bot loop.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _s;

    public SupportClient(AppSettings settings) => _s = settings;

    public string Character => (_s.HubUsername ?? "").Trim();

    /// <summary>Is there enough to file anything at all? The endpoint keys tickets by character.</summary>
    public bool Ready => _s.HubEnabled && Character.Length > 0 && (_s.HubApiKey ?? "").Length > 0;

    /// <summary>
    /// HubUrl points at the check-in endpoint (…/hub/api.php); support lives beside it, exactly
    /// as equipment does. Derived rather than stored so a member who points the app at a test hub
    /// gets a test support queue too, instead of quietly filing tickets on the live one.
    /// </summary>
    public string Url()
    {
        string url = _s.HubUrl ?? "";
        int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
        string root = i >= 0 ? url[..i] : (url.EndsWith("/") ? url : url + "/");
        return root + "api/support.php";
    }

    /* ------------------------------------------------------------------ writing */

    /// <summary>
    /// Raise a ticket. Returns the hub id and the member-facing url so the app can offer to open
    /// it. <c>duplicate</c> comes back true when the hub matched an identical report from the last
    /// two minutes — a double-click is not two bugs, and it says so rather than pretending.
    /// </summary>
    public async Task<(bool ok, int id, string url, bool duplicate, string message)> OpenTicket(
        string kind, string title, string body, CancellationToken ct = default)
    {
        if (!Ready) return (false, 0, "", false, "Set your character name on the Account page first.");

        var payload = new
        {
            op = "ticket",
            api_key = _s.HubApiKey,
            username = Character,
            kind,
            title,
            body,
            app_version = AppSettings.AppVersion,
            os = SupportMetrics.Os,
            screen = SupportMetrics.Screen,
            source = "app",
        };

        try
        {
            using HttpResponseMessage resp = await Post(payload, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            if (!resp.IsSuccessStatusCode || !Bool(root, "ok"))
                return (false, 0, "", false, Str(root, "error") ?? $"the hub said {(int)resp.StatusCode}");

            return (true, Int(root, "id"), Str(root, "url") ?? "", Bool(root, "duplicate"), "");
        }
        catch (Exception ex)
        {
            BotLog.Log("support", "ticket failed: " + ex.Message);
            return (false, 0, "", false, ex.Message);
        }
    }

    /// <summary>
    /// Post a fault. The hub dedupes by fingerprint and returns the running count, which is what
    /// lets <see cref="CrashReporter"/> stop shouting about one it has already reported a hundred
    /// times — only the client can make that decision, so only the client is told the number.
    /// </summary>
    public async Task<(bool ok, int count, string message)> ReportError(
        string kind, string message, string stack, string? appVersion = null, CancellationToken ct = default)
    {
        if (!Ready) return (false, 0, "no character name set");

        var payload = new
        {
            op = "error",
            api_key = _s.HubApiKey,
            username = Character,
            kind,
            message,
            stack,
            app_version = appVersion ?? AppSettings.AppVersion,
            os = SupportMetrics.Os,
        };

        try
        {
            using HttpResponseMessage resp = await Post(payload, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!resp.IsSuccessStatusCode || !Bool(root, "ok"))
                return (false, 0, Str(root, "error") ?? $"the hub said {(int)resp.StatusCode}");
            return (true, Int(root, "count"), "");
        }
        catch (Exception ex) { return (false, 0, ex.Message); }
    }

    /// <summary>Add to one of your own tickets.</summary>
    public async Task<(bool ok, string message)> Reply(int id, string body, CancellationToken ct = default)
    {
        if (!Ready) return (false, "no character name set");
        try
        {
            using HttpResponseMessage resp = await Post(
                new { op = "reply", api_key = _s.HubApiKey, username = Character, id, body }, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(text);
            return resp.IsSuccessStatusCode && Bool(doc.RootElement, "ok")
                ? (true, "")
                : (false, Str(doc.RootElement, "error") ?? $"the hub said {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /* ------------------------------------------------------------------ reading */

    /// <summary>Your recent tickets. Null means the hub could not be reached — which is not the
    /// same as having none, and the window says so differently.</summary>
    public async Task<List<SupportTicketRow>?> List(int n = 20, CancellationToken ct = default)
    {
        if (!Ready) return null;
        try
        {
            string url = $"{Url()}?op=list&n={n}&username={Uri.EscapeDataString(Character)}";
            string text = await Get(url, ct);
            var r = JsonSerializer.Deserialize<ListResponse>(text, Json);
            return r is { Ok: true } ? (r.Tickets ?? new List<SupportTicketRow>()) : null;
        }
        catch (Exception ex) { BotLog.Log("support", "list failed: " + ex.Message); return null; }
    }

    /// <summary>One of your tickets, with the officer replies.</summary>
    public async Task<SupportTicket?> Get(int id, CancellationToken ct = default)
    {
        if (!Ready) return null;
        try
        {
            string url = $"{Url()}?op=get&id={id}&username={Uri.EscapeDataString(Character)}";
            string text = await Get(url, ct);
            var r = JsonSerializer.Deserialize<GetResponse>(text, Json);
            if (r is not { Ok: true, Ticket: not null }) return null;
            r.Ticket.Messages = r.Messages ?? new List<SupportMessage>();
            return r.Ticket;
        }
        catch (Exception ex) { BotLog.Log("support", "get failed: " + ex.Message); return null; }
    }

    /// <summary>
    /// What has shipped. Deliberately unauthenticated on the server — it is the public repo's
    /// release list — so this works even before a character name has been set, which is exactly
    /// when someone is most likely to be looking for "is this already fixed".
    /// </summary>
    public async Task<List<ReleaseRow>?> Changelog(int n = 8, CancellationToken ct = default)
    {
        try
        {
            string text = await Get($"{Url()}?op=changelog&n={n}", ct);
            var r = JsonSerializer.Deserialize<ChangelogResponse>(text, Json);
            return r is { Ok: true } ? (r.Releases ?? new List<ReleaseRow>()) : null;
        }
        catch (Exception ex) { BotLog.Log("support", "changelog failed: " + ex.Message); return null; }
    }

    /* -------------------------------------------------------------------- plumbing */

    private async Task<HttpResponseMessage> Post(object payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url())
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
        return await Http.SendAsync(req, ct);
    }

    private async Task<string> Get(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
        using HttpResponseMessage resp = await Http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class ListResponse
    {
        public bool Ok { get; set; }
        public List<SupportTicketRow>? Tickets { get; set; }
    }

    private sealed class GetResponse
    {
        public bool Ok { get; set; }
        public SupportTicket? Ticket { get; set; }
        public List<SupportMessage>? Messages { get; set; }
    }

    private sealed class ChangelogResponse
    {
        public bool Ok { get; set; }
        public List<ReleaseRow>? Releases { get; set; }
    }
}
