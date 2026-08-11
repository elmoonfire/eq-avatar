using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Sessions;

namespace EQAvatar.Spike.Net;

/// <summary>One remote command pulled from the hub queue (issued from the phone app or the website).</summary>
public sealed class RemoteCommand
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public JsonElement Payload { get; set; }

    /// <summary>Read a payload field as text ("" payloads and missing fields both return null).</summary>
    public string? Str(string name) =>
        Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(name, out JsonElement v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;
}

/// <summary>
/// Remote control-plane client — the desktop side of the mobile/web companion.
/// Talks to the /hub/api/ endpoints with the same shared key + username the check-in uses:
///   - polls queued commands every few seconds and hands them to the app to execute,
///   - posts a live status snapshot (role, zone, /loc, counters) so phones can watch,
///   - uploads finished session records so reporting/charts work away from this PC.
/// Runs on a background task; anything that touches the UI or the game happens inside the
/// executor callback, which MainWindow marshals onto the dispatcher.
/// </summary>
public sealed class RemoteClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettings _s;
    private readonly Func<RemoteCommand, Task<(bool ok, string result)>> _execute;
    private readonly Func<object?> _snapshot;
    private CancellationTokenSource? _cts;

    private const int PollSeconds = 4;      // command latency ceiling
    private const int StatusEvery = 5;      // ticks → ~20 s between status posts
    private const int SessionsEvery = 75;   // ticks → ~5 min between session syncs
    private const int SessionsPerSync = 10; // upload at most this many records per sync

    public event Action<string>? Log;

    public RemoteClient(AppSettings settings,
                        Func<RemoteCommand, Task<(bool ok, string result)>> execute,
                        Func<object?> statusSnapshot)
    { _s = settings; _execute = execute; _snapshot = statusSnapshot; }

    /// <summary>.../hub/api.php (the check-in endpoint) → .../hub/api (the control-plane folder).</summary>
    private string ApiBase
    {
        get
        {
            string u = (_s.HubUrl ?? "").Trim();
            if (u.EndsWith("/api.php", StringComparison.OrdinalIgnoreCase)) return u[..^8] + "/api";
            return u.TrimEnd('/') + "/api";
        }
    }

    private string User => (_s.HubUsername ?? "").Trim();
    private bool Ready => _s.HubEnabled && _s.RemoteControlEnabled && User.Length > 0;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private async Task Loop(CancellationToken ct)
    {
        int tick = 0;
        bool announced = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Ready)
                {
                    if (!announced) { announced = true; Log?.Invoke($"remote control on — polling the hub as {User}"); }
                    await PollCommands(ct);
                    if (tick % StatusEvery == 0) await PostStatus(ct);
                    if (tick % SessionsEvery == 0) await SyncSessions(ct);
                    tick++;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log?.Invoke("remote loop hiccup: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollCommands(CancellationToken ct)
    {
        string url = $"{ApiBase}/commands.php?username={Uri.EscapeDataString(User)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
        using HttpResponseMessage resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("commands", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement c in list.EnumerateArray())
        {
            var cmd = new RemoteCommand
            {
                Id = c.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0,
                Kind = c.TryGetProperty("kind", out JsonElement k) ? (k.GetString() ?? "") : "",
                Payload = c.TryGetProperty("payload", out JsonElement p) ? p.Clone() : default,
            };
            if (cmd.Id == 0 || cmd.Kind.Length == 0) continue;
            Log?.Invoke($"command #{cmd.Id}: {cmd.Kind}");
            bool ok; string result;
            try { (ok, result) = await _execute(cmd); }
            catch (Exception ex) { ok = false; result = "error: " + ex.Message; }
            await Complete(cmd.Id, ok, result, ct);
            Log?.Invoke($"  → {(ok ? "done" : "failed")}: {result}");
        }
    }

    private Task Complete(long id, bool ok, string result, CancellationToken ct) =>
        PostJson($"{ApiBase}/commands.php",
                 new { api_key = _s.HubApiKey, username = User, op = "complete", id, ok, result }, ct);

    private async Task PostStatus(CancellationToken ct)
    {
        object? st = _snapshot();
        if (st is null) return;
        await PostJson($"{ApiBase}/status.php",
                       new { api_key = _s.HubApiKey, username = User, status = st }, ct);
    }

    private async Task<string> PostJson(string url, object payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-API-KEY", _s.HubApiKey);
        using HttpResponseMessage resp = await Http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---- session-history sync ----------------------------------------------------------

    private static string MarkerPath => Path.Combine(AppSettings.Dir, "sessions_uploaded.json");

    private static Dictionary<string, long> LoadMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(MarkerPath)) ?? new();
        }
        catch { }
        return new Dictionary<string, long>();
    }

    /// <summary>Upload session records the hub hasn't seen yet (or that changed), newest first.</summary>
    private async Task SyncSessions(CancellationToken ct)
    {
        Dictionary<string, long> done = LoadMarker();
        List<SessionRecord> pending = SessionStore.LoadAll()   // newest first
            .Where(r => r.Id.Length > 0 && (!done.TryGetValue(r.Id, out long t) || t != r.EndedAt.Ticks))
            .Take(SessionsPerSync).ToList();
        if (pending.Count == 0) return;

        var payload = new
        {
            api_key = _s.HubApiKey,
            username = User,
            sessions = pending.Select(r => new
            {
                sid = r.Id,
                started_at = ToEpoch(r.StartedAt),
                ended_at = ToEpoch(r.EndedAt),
                role = r.Role,
                zone = r.PrimaryZone,
                kills = r.Kills,
                xp_ticks = r.XpTicks,
                aa = r.AaPoints,
                deaths = r.Deaths,
                dmg_dealt = r.DmgDealt,
                dmg_taken = r.DmgTaken,
                json = r,                       // full record: settings, per-minute series, trail
            }).ToList(),
        };
        string body = await PostJson($"{ApiBase}/sessions.php", payload, ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True)
        {
            foreach (SessionRecord r in pending) done[r.Id] = r.EndedAt.Ticks;
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.WriteAllText(MarkerPath, JsonSerializer.Serialize(done));
            }
            catch { }
            Log?.Invoke($"session history synced to hub ({pending.Count} record(s))");
        }
    }

    private static long ToEpoch(DateTime dt) =>
        dt.Year < 2000 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeSeconds();
}
