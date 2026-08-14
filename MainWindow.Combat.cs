using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EQAvatar.Spike.Charts;
using EQAvatar.Spike.Combat;
using EqLog = EQAvatar.Spike.Log;

namespace EQAvatar.Spike;

/// <summary>
/// Combat Meter panel (partial class). A player-centric DPS meter fed from the same log tap the
/// Maps panel already uses: every line goes to <see cref="_combat"/>, which groups damage into
/// fights and a rolling 15-minute window. This file owns the live charts (Command Center + Combat
/// panel), the per-fight drill-down, and the once-a-second housekeeping tick — which also doubles
/// as the Follower's "seconds actually spent in combat" sampler for session history.
/// </summary>
public partial class MainWindow
{
    private readonly CombatTracker _combat = new();
    private readonly DispatcherTimer _combatTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _lastFightCount = -1;

    // series colours: cyan = damage you dealt, coral = damage you took (matches the log reader palette)
    private static readonly Color CDealt = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color CTaken = Color.FromRgb(0xFF, 0x8A, 0x80);

    private void InitCombat()
    {
        _combatTimer.Tick += (_, _) => CombatTick();
        _combatTimer.Start();
    }

    /// <summary>Once a second: close a fight that has gone quiet, scroll the live window, sample the
    /// Follower's in-combat time, and repaint whichever live view is on screen.</summary>
    private void CombatTick()
    {
        _combat.Tick();

        // COMBAT TIME, FOR EVERY ROLE. This used to count only while the FOLLOWER was assisting,
        // so a grind, a hunt or a quest run recorded zero seconds of combat and every DPS figure
        // divided by nothing. Combat is now defined the way a damage meter defines it: a second
        // in which damage landed, in either direction, plus a short tail so the gap between two
        // swings does not read as peace.
        bool inCombat = (DateTime.Now - _lastDamageAt).TotalSeconds <= CombatTailSeconds;

        // The Follower can be usefully "in combat" while contributing no damage of its own, so
        // its own state still counts — but only when the damage rule has not already said so.
        if (!inCombat && Recorder.ActiveRole == "Follower" && _follower is { Running: true })
        {
            string st = _follower.Stats.State;
            inCombat = st.Contains("fight", StringComparison.OrdinalIgnoreCase)
                    || st.Contains("assist", StringComparison.OrdinalIgnoreCase);
        }
        if (Recorder.Active && inCombat) Recorder.RecordCombatSecond();

        Recorder.Flush();                                  // crash-proof the active session (1/min)

        if (!_ready) return;
        if (PanelCombat.Visibility == Visibility.Visible) RefreshCombatPanel();
        // the Command Center's small chart is driven by RefreshHome (300ms), but nudge it here too
        // so it keeps decaying during idle even if the 300ms path is throttled.
        else if (PanelHome.Visibility == Visibility.Visible) RefreshHomeDps();
    }

    /// <summary>Feed one parsed log line into the meter; bank any damage delta on the session.</summary>
    private void FeedCombat(EqLog.LogEvent ev)
    {
        (int dealt, int taken) = _combat.FeedLine(ev.Stamp, ev.Text);
        if (dealt == 0 && taken == 0) return;
        Recorder.RecordDamage(dealt, taken);
        // Wall clock, not the log stamp: CombatTick asks "was there damage in the last few
        // seconds?", and a replayed backlog of old lines must not be read as a fight happening now.
        _lastDamageAt = DateTime.Now;
    }

    /// <summary>How long after the last damage a second still counts as combat. A melee round is
    /// about three seconds and a caster's gaps are longer, so six keeps one fight as one fight
    /// without swallowing the walk to the next camp.</summary>
    private const double CombatTailSeconds = 6;

    /// <summary>When damage last landed, in either direction. Starts at MinValue so an app that
    /// has seen no combat records none.</summary>
    private DateTime _lastDamageAt = DateTime.MinValue;

    private List<ChartSeries> WindowSeries(int minutes)
    {
        (double[] d, double[] k) = _combat.Window(minutes);
        var series = new List<ChartSeries>(2);
        if (d.Any(v => v > 0)) series.Add(new ChartSeries("dealt", CDealt, d, Fill: true));
        if (k.Any(v => v > 0)) series.Add(new ChartSeries("taken", CTaken, k));
        return series;
    }

    /// <summary>Command Center: last 10 minutes of dealt-vs-taken DPS + a one-line summary.</summary>
    private void RefreshHomeDps()
    {
        List<ChartSeries> series = WindowSeries(10);
        HomeDpsChart.SetSeries(series, "10 min ago", "now", "no combat in the last 10 min");
        (double[] d, _) = _combat.Window(10);
        double now = d.Length > 0 ? d[^1] : 0;
        HomeDpsLabel.Text = _combat.TotalDealt > 0 ? $"{now:0} DPS" : "0 DPS";
    }

    /// <summary>Combat panel: the wide live chart, session totals, and the fights table.</summary>
    private void RefreshCombatPanel()
    {
        CombatChart.SetSeries(WindowSeries(15), "15 min ago", "now", "no combat yet — set the log folder and fight something");

        FightRecord? a = _combat.Active;
        CombatLiveLabel.Text = a != null
            ? $"● fighting {a.Target} — {a.DmgDealt:n0} dmg · {a.Dps:0} dps · {a.DurText}"
            : "no active fight — DPS is dealt vs. taken over the last 15 min";
        CombatTotals.Text = $"session  {_combat.TotalDealt:n0} dealt · {_combat.TotalTaken:n0} taken";

        // Rebind the table only when the number of closed fights changes, so the user's selection and
        // the drill-down chart don't get yanked out from under them every second.
        List<FightRecord> fights = _combat.Fights;
        if (fights.Count != _lastFightCount)
        {
            _lastFightCount = fights.Count;
            FightRecord? prev = FightsList.SelectedItem as FightRecord;
            FightsList.ItemsSource = fights;
            if (prev != null)
            {
                FightRecord? match = fights.FirstOrDefault(x => x.StartedAt == prev.StartedAt && x.Target == prev.Target);
                if (match != null) FightsList.SelectedItem = match;
            }
        }
    }

    /// <summary>Pick a fight → chart its damage-per-second and print its stat line.</summary>
    private void Fight_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (FightsList.SelectedItem is not FightRecord f)
        {
            FightTitle.Text = "Pick a fight";
            FightSub.Text = "its damage-per-second is charted here";
            FightChart.SetSeries(Array.Empty<ChartSeries>());
            return;
        }
        FightTitle.Text = f.Killed ? $"{f.Target}  ✓ killed" : f.Target;
        FightSub.Text =
            $"{f.TimeText} · {f.DurText} · {f.DmgDealt:n0} dmg · {f.DpsText} dps · max {f.MaxHit} · {f.Hits} hits ({f.Crits} crit) · took {f.DmgTaken:n0}";
        var vals = f.PerSecond.Select(v => (double)v).ToList();
        FightChart.SetSeries(
            vals.Count >= 2 ? new[] { new ChartSeries("dmg/s", CDealt, vals, Fill: true) } : Array.Empty<ChartSeries>(),
            "start", f.DurText, "too short to chart");
    }
}
