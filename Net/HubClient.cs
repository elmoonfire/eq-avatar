using System;
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
