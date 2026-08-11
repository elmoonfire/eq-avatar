using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EQAvatar.Spike.Combat;

/// <summary>
/// Player-centric combat model fed line-by-line from the log. The line grammar is ported from
/// EQ Legends Companion's parseCombat.ts (github.com/jmoyers/everquest-companion, MIT), whose
/// regexes were measured against a 116 MB real log — including the full melee verb list (a
/// bare "slashes?" drops all first-person melee), typed spell damage, DoT ticks, damage
/// shields, and the trailing "(Critical)" modifier.
///
/// It produces three things:
///   • FIGHTS — damage grouped per target, closed by a kill line or an idle gap. Each fight
///     carries totals, max hit, crit count, DPS, and a per-second damage series for charting.
///   • A ROLLING WINDOW — 5-second damage buckets for the last 15 minutes (the Command
///     Center's live chart).
///   • Per-line deltas returned to the caller so the session recorder can bucket them.
/// </summary>
public sealed class FightRecord
{
    public string Target = "";
    public DateTime StartedAt, EndedAt;
    public long DmgDealt, DmgTaken;
    public int Hits, Crits, MaxHit;
    public bool Killed;
    public readonly List<int> PerSecond = new();     // dealt damage per second since start

    public double DurationSeconds => Math.Max(1, (EndedAt - StartedAt).TotalSeconds);
    public double Dps => DmgDealt / DurationSeconds;
    public string TimeText => StartedAt.ToString("HH:mm:ss");
    public string DurText => $"{(int)DurationSeconds}s";
    public string DpsText => $"{Dps:0.#}";
}

public sealed class CombatTracker
{
    // ---- grammar (ported verbatim where it matters) ----
    private const string MeleeVerbs =
        "hit(?:s)?|slash(?:es)?|pierce(?:s)?|crush(?:es)?|bash(?:es)?|kick(?:s)?|bite(?:s)?|claw(?:s)?|gore(?:s)?|maul(?:s)?|punch(?:es)?|strike(?:s)?|slice(?:s)?|backstab(?:s)?|slam(?:s)?|sting(?:s)?|rend(?:s)?|smash(?:es)?|gnaw(?:s)?|lash(?:es)?|smite(?:s)?|cleave(?:s)?|reave(?:s)?|shoot(?:s)?|frenzies on|frenzy on|flurries|flurry";
    private static readonly Regex MeleeRe = new(
        $@"^(.+?) (?:{MeleeVerbs}) (.+?) for (\d+) points? of damage\.(?: \((.+?)\))?$", RegexOptions.Compiled);
    private static readonly Regex SpellRe = new(
        @"^(.+?) (?:hits?) (.+?) for (\d+) points of ([\w-]+) damage by (.+?)\.(?: \((.+?)\))?$", RegexOptions.Compiled);
    private static readonly Regex DotRe = new(
        @"^(.+?) has taken (\d+) damage from (.+?)\.(?: \((.+?)\))?$", RegexOptions.Compiled);
    private static readonly Regex DsRe = new(
        @"^(.+?) is \w+ by (YOUR|.+?'s) (.+?) for (\d+) points? of non-melee damage\.$", RegexOptions.Compiled);
    private static readonly Regex DsIncRe = new(
        @"^YOU are \w+ by (.+?)'s (.+?) for (\d+) points? of non-melee damage!$", RegexOptions.Compiled);
    private static readonly Regex SlainByYou = new(
        @"^(.+?) has been slain by you!?$", RegexOptions.Compiled);
    private static readonly Regex YouHaveSlain = new(
        @"^You have slain (.+?)!?$", RegexOptions.Compiled);

    private const int FightGapSeconds = 8;
    private const int BucketSeconds = 5;
    private const int WindowBuckets = 180;           // 15 minutes of live chart

    private readonly object _gate = new();
    private readonly List<FightRecord> _fights = new();
    private FightRecord? _active;
    private readonly long[] _dealtBuckets = new long[WindowBuckets];
    private readonly long[] _takenBuckets = new long[WindowBuckets];
    private long _bucketEpoch = -1;                  // absolute bucket index of the newest slot
    public long TotalDealt { get; private set; }
    public long TotalTaken { get; private set; }

    /// <summary>Feed one log line (timestamp already stripped). Returns (dealt, taken) deltas.</summary>
    public (int dealt, int taken) FeedLine(DateTime? stamp, string msg)
    {
        DateTime t = stamp ?? DateTime.Now;
        int dealt = 0, taken = 0;
        string? target = null;
        bool crit = false;

        Match m = MeleeRe.Match(msg);
        if (m.Success)
        {
            string atk = m.Groups[1].Value;
            string def = m.Groups[2].Value;
            int dmg = int.Parse(m.Groups[3].Value);
            crit = m.Groups[4].Success && m.Groups[4].Value.Contains("Critical", StringComparison.OrdinalIgnoreCase);
            if (atk.Equals("You", StringComparison.OrdinalIgnoreCase)) { dealt = dmg; target = def; }
            else if (def.Equals("YOU", StringComparison.OrdinalIgnoreCase)) { taken = dmg; target = atk; }
        }
        else if ((m = SpellRe.Match(msg)).Success)
        {
            string atk = m.Groups[1].Value; string def = m.Groups[2].Value;
            int dmg = int.Parse(m.Groups[3].Value);
            crit = m.Groups[6].Success && m.Groups[6].Value.Contains("Critical", StringComparison.OrdinalIgnoreCase);
            if (atk.Equals("You", StringComparison.OrdinalIgnoreCase)) { dealt = dmg; target = def; }
            else if (def.Equals("YOU", StringComparison.OrdinalIgnoreCase)) { taken = dmg; target = atk; }
        }
        else if ((m = DotRe.Match(msg)).Success)
        {
            // "<Mob> has taken N damage from your <Spell>." — ours only when the source says so.
            if (m.Groups[3].Value.StartsWith("your ", StringComparison.OrdinalIgnoreCase))
            { dealt = int.Parse(m.Groups[2].Value); target = m.Groups[1].Value; }
        }
        else if ((m = DsIncRe.Match(msg)).Success)
        {
            taken = int.Parse(m.Groups[3].Value); target = m.Groups[1].Value;
        }
        else if ((m = DsRe.Match(msg)).Success)
        {
            if (m.Groups[2].Value.Equals("YOUR", StringComparison.OrdinalIgnoreCase))
            { dealt = int.Parse(m.Groups[4].Value); target = m.Groups[1].Value; }
        }
        else if ((m = SlainByYou.Match(msg)).Success || (m = YouHaveSlain.Match(msg)).Success)
        {
            CloseFight(t, m.Groups[1].Value, killed: true);
            return (0, 0);
        }

        if (dealt == 0 && taken == 0) return (0, 0);
        lock (_gate)
        {
            TotalDealt += dealt; TotalTaken += taken;
            Bucket(t, dealt, taken);
            Fight(t, target ?? "unknown", dealt, taken, crit);
        }
        return (dealt, taken);
    }

    private void Bucket(DateTime t, int dealt, int taken)
    {
        BucketAdvance(t);
        if (_bucketEpoch < 0) _bucketEpoch = t.Ticks / TimeSpan.TicksPerSecond / BucketSeconds;
        _dealtBuckets[^1] += dealt; _takenBuckets[^1] += taken;
    }

    /// <summary>Scroll the rolling window forward to time <paramref name="t"/> WITHOUT adding damage,
    /// so the live chart decays toward zero during idle — not only when the next hit finally lands.</summary>
    private void BucketAdvance(DateTime t)
    {
        long abs = t.Ticks / TimeSpan.TicksPerSecond / BucketSeconds;
        if (_bucketEpoch < 0) { _bucketEpoch = abs; return; }
        if (abs <= _bucketEpoch) return;
        long shift = Math.Min(abs - _bucketEpoch, WindowBuckets);
        for (int i = 0; i < WindowBuckets - shift; i++)
        { _dealtBuckets[i] = _dealtBuckets[i + shift]; _takenBuckets[i] = _takenBuckets[i + shift]; }
        for (long i = WindowBuckets - shift; i < WindowBuckets; i++)
        { _dealtBuckets[i] = 0; _takenBuckets[i] = 0; }
        _bucketEpoch = abs;
    }

    private void Fight(DateTime t, string target, int dealt, int taken, bool crit)
    {
        // idle gap or target change closes the current fight
        if (_active != null && ((t - _active.EndedAt).TotalSeconds > FightGapSeconds
                                 || (dealt > 0 && !_active.Target.Equals(target, StringComparison.OrdinalIgnoreCase))))
            CloseActive();
        if (_active is null)
            _active = new FightRecord { Target = target, StartedAt = t, EndedAt = t };
        _active.EndedAt = t;
        _active.DmgDealt += dealt; _active.DmgTaken += taken;
        if (dealt > 0) { _active.Hits++; if (crit) _active.Crits++; if (dealt > _active.MaxHit) _active.MaxHit = dealt; }
        int sec = (int)Math.Clamp((t - _active.StartedAt).TotalSeconds, 0, 3600);
        while (_active.PerSecond.Count <= sec) _active.PerSecond.Add(0);
        _active.PerSecond[sec] += dealt;
    }

    private void CloseFight(DateTime t, string target, bool killed)
    {
        lock (_gate)
        {
            if (_active != null && _active.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
            { _active.Killed = killed; _active.EndedAt = t; CloseActive(); }
        }
    }

    private void CloseActive()
    {
        if (_active is null) return;
        if (_active.DmgDealt + _active.DmgTaken > 0) _fights.Insert(0, _active);
        if (_fights.Count > 200) _fights.RemoveAt(_fights.Count - 1);
        _active = null;
    }

    /// <summary>Close the active fight if it has gone quiet (call from a UI timer).</summary>
    public void Tick()
    {
        lock (_gate)
        {
            BucketAdvance(DateTime.Now);          // let the live window scroll toward zero while idle
            if (_active != null && (DateTime.Now - _active.EndedAt).TotalSeconds > FightGapSeconds)
                CloseActive();
        }
    }

    public List<FightRecord> Fights { get { lock (_gate) return _fights.ToList(); } }
    public FightRecord? Active { get { lock (_gate) return _active; } }

    /// <summary>Dealt/taken DPS per 5s bucket over the last N minutes, oldest first.</summary>
    public (double[] dealt, double[] taken) Window(int minutes = 10)
    {
        lock (_gate)
        {
            int buckets = Math.Clamp(minutes * 60 / BucketSeconds, 2, WindowBuckets);
            var d = new double[buckets]; var k = new double[buckets];
            for (int i = 0; i < buckets; i++)
            {
                d[i] = _dealtBuckets[WindowBuckets - buckets + i] / (double)BucketSeconds;
                k[i] = _takenBuckets[WindowBuckets - buckets + i] / (double)BucketSeconds;
            }
            return (d, k);
        }
    }
}
