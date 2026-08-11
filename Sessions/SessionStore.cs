using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Sessions;

/// <summary>
/// One recorded automation session: who ran (which role), for how long, what it produced
/// (kills / xp ticks / AA points / deaths, all counted from the log while the session was
/// active), the SETTINGS it ran with (so two sessions can be compared to tune them), and the
/// movement trail per zone (so any session can be replayed as a heatmap over the real map).
///
/// Honest numbers note: the EQ log prints "You gain experience!!" with no amount, so XP is
/// counted in TICKS (gains), not points — the per-hour rate is ticks/hour. AA points ARE
/// discrete ("You have gained an ability point!"), so that count is exact.
/// </summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public int Actions { get; set; }
    public int Kills { get; set; }
    public int XpTicks { get; set; }
    public int AaPoints { get; set; }
    public int Deaths { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
    /// <summary>zone display name -> [ew, ns] samples, in order.</summary>
    public Dictionary<string, List<double[]>> Trail { get; set; } = new();

    // ---- computed, for the viewer ----
    [JsonIgnore] public double DurationSeconds => Math.Max(0, (EndedAt - StartedAt).TotalSeconds);
    [JsonIgnore] public string DateText => StartedAt.ToString("MMM d  HH:mm");
    [JsonIgnore] public string DurationText
    {
        get
        {
            TimeSpan t = TimeSpan.FromSeconds(DurationSeconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
        }
    }
    [JsonIgnore] public double Hours => DurationSeconds / 3600.0;
    [JsonIgnore] public double XpPerHour => Hours >= 0.03 ? XpTicks / Hours : 0;
    [JsonIgnore] public double AaPerHour => Hours >= 0.03 ? AaPoints / Hours : 0;
    [JsonIgnore] public double KillsPerHour => Hours >= 0.03 ? Kills / Hours : 0;
    [JsonIgnore] public string XpRateText => XpPerHour <= 0 ? "—" : $"{XpPerHour:0.#}/h";
    [JsonIgnore] public string AaRateText => AaPerHour <= 0 ? "—" : $"{AaPerHour:0.##}/h";
    [JsonIgnore] public string PrimaryZone =>
        Trail.Count == 0 ? "—" : Trail.OrderByDescending(kv => kv.Value.Count).First().Key;
    [JsonIgnore] public int TrailPoints => Trail.Sum(kv => kv.Value.Count);
}

/// <summary>Disk store: one JSON file per session under %AppData%\EQAvatar\sessions.</summary>
public static class SessionStore
{
    public static string Dir => Path.Combine(AppSettings.Dir, "sessions");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static void Save(SessionRecord r)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, r.Id + ".json"), JsonSerializer.Serialize(r, Opts));
        }
        catch { /* a lost record must never take the app down */ }
    }

    public static List<SessionRecord> LoadAll()
    {
        var outList = new List<SessionRecord>();
        try
        {
            if (!Directory.Exists(Dir)) return outList;
            foreach (string f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    SessionRecord? r = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(f));
                    if (r != null) outList.Add(r);
                }
                catch { /* one corrupt record shouldn't hide the rest */ }
            }
        }
        catch { }
        return outList.OrderByDescending(r => r.StartedAt).ToList();
    }

    public static void Delete(string id)
    {
        try { File.Delete(Path.Combine(Dir, id + ".json")); } catch { }
    }
}

/// <summary>
/// Records the active session. MainWindow calls Begin when a role starts, feeds it log events
/// as they stream past (locations, xp, AA, kills, deaths), and calls End when the role stops —
/// which finalizes the counters and writes the record to disk.
/// </summary>
public sealed class SessionRecorder
{
    private SessionRecord? _active;
    private double _lastEw = double.NaN, _lastNs = double.NaN;
    private const int TrailCap = 25000;
    private int _trailCount;

    public bool Active => _active != null;
    public string? ActiveRole => _active?.Role;

    public void Begin(string role, Dictionary<string, string> settings)
    {
        End(0);                                   // safety: close a dangling session first
        _active = new SessionRecord
        {
            Id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{role}",
            Role = role,
            StartedAt = DateTime.Now,
            Settings = settings,
        };
        _lastEw = _lastNs = double.NaN;
        _trailCount = 0;
    }

    public void RecordLoc(string zone, double ew, double ns)
    {
        if (_active is null || _trailCount >= TrailCap) return;
        // drop near-duplicates so idle standing doesn't balloon the file
        if (!double.IsNaN(_lastEw) && Math.Abs(ew - _lastEw) < 3 && Math.Abs(ns - _lastNs) < 3) return;
        _lastEw = ew; _lastNs = ns;
        string key = string.IsNullOrWhiteSpace(zone) ? "Unknown" : zone;
        if (!_active.Trail.TryGetValue(key, out List<double[]>? list)) _active.Trail[key] = list = new List<double[]>();
        list.Add(new[] { ew, ns });
        _trailCount++;
    }

    public void RecordXp() { if (_active != null) _active.XpTicks++; }
    public void RecordAa() { if (_active != null) _active.AaPoints++; }
    public void RecordKill() { if (_active != null) _active.Kills++; }
    public void RecordDeath() { if (_active != null) _active.Deaths++; }

    /// <summary>Finalize and persist. <paramref name="actions"/> comes from the role's own
    /// counters (keys sent / mobs considered / assists) at the moment it stopped.</summary>
    public SessionRecord? End(int actions)
    {
        if (_active is null) return null;
        SessionRecord r = _active;
        _active = null;
        r.EndedAt = DateTime.Now;
        r.Actions = Math.Max(r.Actions, actions);
        if (r.DurationSeconds < 5) return null;   // a mis-click isn't a session
        SessionStore.Save(r);
        return r;
    }
}
