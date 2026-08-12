using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using EQAvatar.Spike.Map;
using EQAvatar.Spike.Sessions;
using EqLog = EQAvatar.Spike.Log;

namespace EQAvatar.Spike;

/// <summary>
/// Session History panel (partial class). Every role run is recorded to disk; this view lists
/// them with sortable, filterable columns (click a header to sort — find your best XP/h run),
/// shows the exact settings each session ran with so two runs can be compared, and replays any
/// session's travel as a heatmap over the real zone map.
/// </summary>
public partial class MainWindow
{
    internal readonly SessionRecorder Recorder = new();
    private List<SessionRecord> _sessions = new();
    private ListCollectionView? _sessView;
    private string _sessSortProp = "StartedAt";
    private bool _sessSortAsc;
    private System.Windows.Point[]? _sessionHeatPts;   // when set, the Maps heat layer shows a recorded session
    private string _sessionHeatLabel = "";

    private void RefreshSessions()
    {
        _sessions = SessionStore.LoadAll();
        _sessView = new ListCollectionView(_sessions) { Filter = SessFilter };
        ApplySessSort();
        SessList.ItemsSource = _sessView;
        SessCount.Text = $"{_sessions.Count} recorded session(s)";
        if (_sessions.Count == 0)
            SessDetailHint.Text = "No sessions yet — run Grind or Follower and it records itself: duration, kills, XP ticks, AA points, your settings, and the travel trail.";
    }

    private bool SessFilter(object o)
    {
        if (o is not SessionRecord r) return false;
        string role = (SessRoleBox.SelectedItem as ComboBoxItem)?.Content as string ?? "All roles";
        if (role != "All roles" && !r.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) return false;
        string q = SessFilterBox.Text.Trim();
        if (q.Length == 0) return true;
        return r.PrimaryZone.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Role.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Settings.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void SessFilter_Changed(object sender, EventArgs e) => _sessView?.Refresh();

    private void SessRefresh_Click(object sender, RoutedEventArgs e) => RefreshSessions();

    /// <summary>Column-header click: sort by that column; a second click flips the direction.</summary>
    private void SessSort_Click(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as GridViewColumnHeader)?.Tag is not string prop) return;
        if (_sessSortProp == prop) _sessSortAsc = !_sessSortAsc;
        else { _sessSortProp = prop; _sessSortAsc = false; }   // first click: biggest/newest first
        ApplySessSort();
    }

    private void ApplySessSort()
    {
        if (_sessView is null) return;
        _sessView.SortDescriptions.Clear();
        _sessView.SortDescriptions.Add(new SortDescription(_sessSortProp,
            _sessSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
    }

    private void SessList_Selected(object sender, SelectionChangedEventArgs e)
    {
        SessDetail.Children.Clear();
        if (SessList.SelectedItem is not SessionRecord r)
        {
            SessDetailHint.Visibility = Visibility.Visible;
            SessChart.SetSeries(Array.Empty<Charts.ChartSeries>());
            return;
        }
        SessDetailHint.Visibility = Visibility.Collapsed;
        RenderSessChart(r);

        void Line(string text, double size = 12, bool bold = false, string color = "#C6D2DE")
            => SessDetail.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = Hex(color), Margin = new Thickness(0, 0, 0, 2) });

        Line($"{r.Role} — {r.DateText}  ·  {r.DurationText}", 14, bold: true, color: "#EAF6FF");
        Line($"kills {r.Kills} ({r.KillsPerHour:0.#}/h)  ·  XP ticks {r.XpTicks} ({r.XpRateText})  ·  AA {r.AaPoints} ({r.AaRateText})  ·  actions {r.Actions}  ·  deaths {r.Deaths}", 12.5, color: "#9FB6CC");
        if (r.DmgDealt > 0 || r.DmgTaken > 0)
            Line($"damage dealt {r.DmgDealt:n0} ({r.DpsText} dps)  ·  taken {r.DmgTaken:n0}", 12.5, color: "#9FB6CC");
        if (r.IsFollower)
            Line($"following {SettingVal(r, "leader")}: assists {r.Assists}  ·  in-combat {r.CombatShareText}  ·  re-follows {r.RefollowText}", 12.5, color: "#7FB2D9");
        Line($"zones: {string.Join(", ", r.Trail.Keys.DefaultIfEmpty("—"))}   ·   {r.TrailPoints} trail points", 12, color: "#9FB6CC");
        if (r.Settings.Count > 0)
        {
            Line("Settings this session ran with:", 12.5, bold: true, color: "#7FB2D9");
            foreach ((string k, string v) in r.Settings)
                Line($"  {k}: {v}", 12);
        }

        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var heat = new Button { Content = "View heatmap →", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), IsEnabled = r.TrailPoints > 4 };
        heat.Click += (_, _) => ViewSessionHeat(r);
        var del = new Button { Content = "Delete", Margin = new Thickness(0, 0, 8, 0) };
        del.Click += (_, _) => { SessionStore.Delete(r.Id); RefreshSessions(); SessDetail.Children.Clear(); SessDetailHint.Visibility = Visibility.Visible; };
        row.Children.Add(heat); row.Children.Add(del);
        SessDetail.Children.Add(row);
    }

    private static string SettingVal(SessionRecord r, string key) =>
        r.Settings.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : "leader";

    /// <summary>Draw the per-minute damage timeline for a recorded session (dealt vs taken).</summary>
    private void RenderSessChart(SessionRecord r)
    {
        var dealt = r.DealtPerMinute.Select(v => (double)v).ToList();
        var taken = r.TakenPerMinute.Select(v => (double)v).ToList();
        var series = new List<Charts.ChartSeries>(2);
        if (dealt.Any(v => v > 0)) series.Add(new Charts.ChartSeries("dealt/min", Color.FromRgb(0x4F, 0xC3, 0xF7), dealt, Fill: true));
        if (taken.Any(v => v > 0)) series.Add(new Charts.ChartSeries("taken/min", Color.FromRgb(0xFF, 0x8A, 0x80), taken));
        SessChart.SetSeries(series, "start", r.DurationText,
            series.Count == 0 ? "no combat recorded this session" : "no data yet");
    }

    /// <summary>Replay a session's travel as the Maps heat layer, over the real zone map.</summary>
    private void ViewSessionHeat(SessionRecord r)
    {
        // pick the zone with the most points that we can actually resolve to a map
        var best = r.Trail.OrderByDescending(kv => kv.Value.Count)
                          .Select(kv => (zone: kv.Key, pts: kv.Value, stem: ZoneTable.ShortFor(kv.Key)))
                          .FirstOrDefault(t => t.stem != null && _mapsZoneStems.Contains(t.stem));
        if (best.stem is null)
        {
            MapsStatus.Text = $"No installed map for this session's zones ({string.Join(", ", r.Trail.Keys)}).";
            NavMaps.IsChecked = true;
            return;
        }
        _sessionHeatPts = best.pts
            .Select(p => { (double mx, double my) = EqMapParser.MapFromLoc(ns: p[1], ew: p[0]); return new System.Windows.Point(mx, my); })
            .ToArray();
        _sessionHeatLabel = $"Heatmap: {r.Role} session {r.DateText} — {best.pts.Count} points in {best.zone}. Untick 'heat' to return to live.";
        NavMaps.IsChecked = true;
        LoadMapZone(best.stem);
        MapsHeatBox.IsChecked = true;
        ApplyMapsLayers();
        RefreshMapsHeat();
        MapsView.Fit();
    }

    // ---- role hooks ----------------------------------------------------------------------

    private Dictionary<string, string> SnapshotGrindSettings(bool hunt) => new()
    {
        ["mode"] = hunt ? "Hunt (roam & find mobs)" : "Rotation only",
        ["rotation"] = string.Join(" | ", GrindRotation.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"))),
        ["keys fwd/back/L/R"] = $"{_settings.HuntForwardKey}/{_settings.HuntBackKey}/{_settings.HuntLeftKey}/{_settings.HuntRightKey}",
        ["target / con / loc"] = $"{_settings.HuntTargetKey} / {_settings.HuntConsiderKey} / {(_settings.HuntLocKey.Length == 0 ? "—" : _settings.HuntLocKey)}",
        ["rest s"] = _settings.HuntRestSeconds.ToString(),
        ["max fight s"] = _settings.HuntMaxFightSeconds.ToString(),
        ["run burst ms"] = $"{_settings.HuntRunMsMin}–{_settings.HuntRunMsMax}",
        ["variance %"] = _settings.RandomVariancePercent.ToString("0"),
        ["skip hard cons"] = _settings.HuntSkipHardCons ? "yes" : "no",
        ["cast/sing only"] = _settings.GrindCastOnly ? "yes" : "no",
        ["look around"] = _settings.HuntLookAround ? "yes" : "no",
    };

    private Dictionary<string, string> SnapshotFollowerSettings() => new()
    {
        ["leader"] = _settings.FollowerLeader,
        ["rotation"] = string.Join(" | ", FollowerRotation.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"))),
        ["auto-assist"] = _settings.FollowerAutoAssist ? "yes" : "no",
        ["assist delay ms"] = _settings.FollowerAssistDelayMs.ToString(),
        ["re-follow s"] = _settings.FollowerRefollowSeconds.ToString(),
        ["max fight s"] = _settings.FollowerMaxFightSeconds.ToString(),
        ["linger s"] = _settings.FollowerCombatLingerSeconds.ToString(),
        ["rest s"] = _settings.FollowerRestSeconds.ToString(),
        ["variance %"] = _settings.RandomVariancePercent.ToString("0"),
    };

    /// <summary>Feed one parsed log event into the active session (called from the maps log tap).</summary>
    private void FeedRecorder(EqLog.LogEvent ev)
    {
        if (!Recorder.Active) return;
        switch (ev.Kind)
        {
            case EqLog.LogEventKind.Location when ev.X is double x && ev.Y is double y:
                Recorder.RecordLoc(_heat.Current, x, y);
                break;
            case EqLog.LogEventKind.Experience: Recorder.RecordXp(); break;
            case EqLog.LogEventKind.Kill: Recorder.RecordKill(); break;
            case EqLog.LogEventKind.Death: Recorder.RecordDeath(); break;
            default:
                if (ev.Kind == EqLog.LogEventKind.Other && ev.Text.Contains("ability point", StringComparison.OrdinalIgnoreCase)
                    && ev.Text.Contains("gained", StringComparison.OrdinalIgnoreCase))
                    Recorder.RecordAa();
                break;
        }
    }

    private void EndRoleSession()
    {
        if (!Recorder.Active) return;
        int actions = Recorder.ActiveRole switch
        {
            "Grind" => _grind?.Stats.KeysSent ?? 0,
            "Hunt" => (_hunt?.Stats.MobsConsidered ?? 0) + (_hunt?.Stats.Fights ?? 0),
            "Follower" => (_follower?.Stats.Assists ?? 0) + (_follower?.Stats.Refollows ?? 0),
            _ => 0,
        };
        if (Recorder.ActiveRole == "Follower" && _follower != null)
            Recorder.SetFollowerCounters(_follower.Stats.Assists, _follower.Stats.Refollows);
        Recorder.End(actions);
        if (SessList != null && PanelSessions.Visibility == Visibility.Visible) RefreshSessions();
    }
}
