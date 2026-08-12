using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EQAvatar.Spike.Map;
using EQAvatar.Spike.Sessions;

namespace EQAvatar.Spike;

/// <summary>
/// Zone plans + grind modes + session history table (partial class).
/// The Maps page gets drawing tools (waypoint routes + hunting-zone shapes, saved per zone via
/// <see cref="ZonePlan"/>); the Grind page gets the mode selector that consumes them and a
/// sortable/filterable table of past grind sessions with a tally row.
/// </summary>
public partial class MainWindow
{
    private bool _planHooked;
    private readonly List<double[]> _shapeDraft = new();     // in-progress shape clicks (loc coords)

    private void HookPlanEditor()
    {
        if (_planHooked) return;
        _planHooked = true;
        MapsView.MapClicked += OnPlanClick;
        MapsView.MapRightClicked += OnPlanRightClick;
    }

    /// <summary>Right-click while editing = "not that one". Removes the waypoint under the cursor
    /// (or the last in-progress shape corner), so a mis-click costs one click instead of clearing
    /// the whole route and starting over. Everything after it renumbers automatically, because the
    /// numbers are just list positions.</summary>
    private void OnPlanRightClick(double mapX, double mapY)
    {
        if (_mapZone is not { } zone) return;

        // Mid-draw on a shape: drop the last corner placed.
        if (MapsView.EditMode is "poly" or "circle" or "rect" && _shapeDraft.Count > 0)
        {
            _shapeDraft.RemoveAt(_shapeDraft.Count - 1);
            MapsEditHint.Text = _shapeDraft.Count == 0 ? "corner removed — start again" : $"{_shapeDraft.Count} corner(s) left";
            RefreshPlanOverlay();
            return;
        }

        ZonePlan plan = PlanForCurrentZone();
        if (plan.Waypoints.Count == 0) { MapsEditHint.Text = "no waypoints to remove"; return; }

        // Hit test in MAP space, with a radius that's a constant ~14 px on screen at any zoom.
        double ew = -mapX, ns = -mapY;
        double radius = 14 / Math.Max(0.0005, MapsView.PixelsPerUnit);
        int best = -1;
        double bestD2 = radius * radius;
        for (int i = 0; i < plan.Waypoints.Count; i++)
        {
            double dx = plan.Waypoints[i][0] - ew, dy = plan.Waypoints[i][1] - ns;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestD2) { bestD2 = d2; best = i; }
        }
        if (best < 0) { MapsEditHint.Text = "right-click ON a waypoint to remove it"; return; }

        plan.Waypoints.RemoveAt(best);
        plan.Save(zone);
        MapsEditHint.Text = $"waypoint {best + 1} removed — {plan.Waypoints.Count} left";
        Diag.BotLog.Log("plan", $"{zone}: waypoint {best + 1} removed ({plan.Waypoints.Count} left)");
        RefreshPlanOverlay();
    }

    /// <summary>Undo the most recently placed waypoint — the keyboard-free version of the same
    /// thing, for when the pin is under another pin.</summary>
    private void MapsUndoWp_Click(object sender, RoutedEventArgs e)
    {
        if (_mapZone is not { } zone) return;
        ZonePlan plan = PlanForCurrentZone();
        if (plan.Waypoints.Count == 0) { MapsEditHint.Text = "no waypoints to undo"; return; }
        plan.Waypoints.RemoveAt(plan.Waypoints.Count - 1);
        plan.Save(zone);
        MapsEditHint.Text = $"last waypoint removed — {plan.Waypoints.Count} left";
        RefreshPlanOverlay();
    }

    private ZonePlan PlanForCurrentZone()
        => (_mapZone is { } z ? ZonePlan.Load(z) : null) ?? new ZonePlan();

    // ---------------- edit-mode toggles ----------------

    private void MapsEdit_Changed(object sender, RoutedEventArgs e)
    {
        if (MapsView is null || EditWpBtn is null) return;
        HookPlanEditor();
        // radio-like: the toggle that changed wins, the others clear
        if (sender is ToggleButton tb && tb.IsChecked == true)
            foreach (ToggleButton other in new[] { EditWpBtn, EditCircleBtn, EditRectBtn, EditPolyBtn })
                if (!ReferenceEquals(other, tb)) other.IsChecked = false;

        _shapeDraft.Clear();
        MapsView.EditMode =
            EditWpBtn.IsChecked == true ? "wp"
            : EditCircleBtn.IsChecked == true ? "circle"
            : EditRectBtn.IsChecked == true ? "rect"
            : EditPolyBtn.IsChecked == true ? "poly" : "";
        EditPolyCloseBtn.Visibility = MapsView.EditMode == "poly" ? Visibility.Visible : Visibility.Collapsed;
        MapsEditHint.Text = MapsView.EditMode switch
        {
            "wp" => "click to drop waypoints in order · right-click one to remove it",
            "circle" => "click the CENTER, then the EDGE",
            "rect" => "click two opposite corners",
            "poly" => "click each corner, then press 'close'",
            _ => "",
        };
        RefreshPlanOverlay();
    }

    private void OnPlanClick(double mapX, double mapY)
    {
        if (_mapZone is not { } zone) return;
        double ew = -mapX, ns = -mapY;                       // map space → loc space
        ZonePlan plan = PlanForCurrentZone();
        switch (MapsView.EditMode)
        {
            case "wp":
                plan.Waypoints.Add(new[] { ew, ns });
                plan.Save(zone);
                MapsEditHint.Text = $"waypoint {plan.Waypoints.Count} placed";
                Diag.BotLog.Log("plan", $"{zone}: waypoint {plan.Waypoints.Count} at {ns:0},{ew:0}");
                break;

            case "circle":
            case "rect":
                _shapeDraft.Add(new[] { ew, ns });
                if (_shapeDraft.Count >= 2)
                {
                    plan.ShapeType = MapsView.EditMode;
                    plan.ShapePts = new List<double[]>(_shapeDraft);
                    plan.Save(zone);
                    _shapeDraft.Clear();
                    EditCircleBtn.IsChecked = false; EditRectBtn.IsChecked = false;
                    MapsView.EditMode = "";
                    MapsEditHint.Text = $"hunting zone saved ({plan.ShapeType})";
                    Diag.BotLog.Log("plan", $"{zone}: {plan.ShapeType} hunting zone saved");
                }
                else MapsEditHint.Text = MapsView.EditMode == "circle" ? "now click the EDGE" : "now click the opposite corner";
                break;

            case "poly":
                _shapeDraft.Add(new[] { ew, ns });
                MapsEditHint.Text = $"{_shapeDraft.Count} corner(s) — press 'close' when done";
                break;
        }
        RefreshPlanOverlay();
    }

    private void MapsPolyClose_Click(object sender, RoutedEventArgs e)
    {
        if (_mapZone is not { } zone) return;
        if (_shapeDraft.Count < 3) { MapsEditHint.Text = "a polygon needs at least 3 corners"; return; }
        ZonePlan plan = PlanForCurrentZone();
        plan.ShapeType = "poly";
        plan.ShapePts = new List<double[]>(_shapeDraft);
        plan.Save(zone);
        _shapeDraft.Clear();
        EditPolyBtn.IsChecked = false;
        MapsView.EditMode = "";
        EditPolyCloseBtn.Visibility = Visibility.Collapsed;
        MapsEditHint.Text = "hunting zone saved (polygon)";
        Diag.BotLog.Log("plan", $"{zone}: polygon hunting zone saved ({plan.ShapePts.Count} corners)");
        RefreshPlanOverlay();
    }

    private void MapsClearWp_Click(object sender, RoutedEventArgs e)
    {
        if (_mapZone is not { } zone) return;
        ZonePlan plan = PlanForCurrentZone();
        plan.Waypoints.Clear();
        plan.Save(zone);
        MapsEditHint.Text = "route cleared";
        RefreshPlanOverlay();
    }

    private void MapsClearShape_Click(object sender, RoutedEventArgs e)
    {
        if (_mapZone is not { } zone) return;
        ZonePlan plan = PlanForCurrentZone();
        plan.ShapeType = ""; plan.ShapePts.Clear();
        plan.Save(zone);
        _shapeDraft.Clear();
        MapsEditHint.Text = "hunting zone cleared";
        RefreshPlanOverlay();
    }

    /// <summary>Push the saved plan (and any in-progress shape draft) onto the map canvas.</summary>
    private void RefreshPlanOverlay()
    {
        if (MapsView is null) return;
        ZonePlan plan = PlanForCurrentZone();
        var wps = plan.Waypoints.Select(p => new Point(-p[0], -p[1])).ToList();
        string type = plan.ShapeType;
        List<Point> shape = plan.ShapePts.Select(p => new Point(-p[0], -p[1])).ToList();
        if (_shapeDraft.Count > 0)                            // live preview while drawing
        {
            type = MapsView.EditMode is "circle" or "rect" or "poly" ? MapsView.EditMode : type;
            shape = _shapeDraft.Select(p => new Point(-p[0], -p[1])).ToList();
        }
        MapsView.SetPlanOverlay(wps, type, shape, loop: WaypointOrderBox?.SelectedIndex == 2);
    }

    // ---------------- grind mode selector ----------------

    private void GrindMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GrindModeBox is null || WaypointOrderBox is null || HuntBox is null) return;
        int i = GrindModeBox.SelectedIndex;
        HuntBox.IsChecked = i != 4;                           // 4 = rotation only
        WaypointOrderBox.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        GrindPlanBtn.Visibility = i is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        if (GrindPlanBtn.Visibility == Visibility.Visible)
            GrindPlanBtn.Content = i == 2 ? "Draw the zone on the map →" : "Draw the route on the map →";
    }

    private void GrindPlan_Click(object sender, RoutedEventArgs e)
    {
        NavMaps.IsChecked = true;
        HookPlanEditor();
        if (GrindModeBox.SelectedIndex == 3) EditWpBtn.IsChecked = true;
        else EditRectBtn.IsChecked = true;
        MapsEdit_Changed(GrindModeBox.SelectedIndex == 3 ? EditWpBtn : EditRectBtn, new RoutedEventArgs());
    }

    /// <summary>Say up front whether the mode about to run actually has what it needs.
    ///
    /// Waypoints and hunting zones both depend on two things the user can silently be missing: a
    /// saved plan for the zone the character is really in, and a live position fix. Without either
    /// the engine degrades to plain roaming, which looks exactly like "it ignored my route" — so
    /// this prints the truth at the moment Start is pressed rather than leaving it to be guessed.</summary>
    private void ReportPlanReadiness()
    {
        int i = GrindModeBox.SelectedIndex;
        if (i is not (2 or 3)) return;                        // only zone + waypoint modes need a plan
        string mode = i == 3 ? "Waypoints" : "Hunting Zone";

        string? stem = _charZoneStem ?? _mapZone;
        if (stem is null)
        { GrindLogLine($"{mode} mode: no zone identified yet. Open the Maps page and load the zone you're standing in, or zone once so the log names it."); return; }

        ZonePlan? plan = ZonePlan.Load(stem);
        string zoneName = ZoneTable.NameFor(stem);
        if (_charZoneStem != null && _mapZone != null && _charZoneStem != _mapZone)
            GrindLogLine($"Heads up: you're in {ZoneTable.NameFor(_charZoneStem)} but the map is showing {ZoneTable.NameFor(_mapZone)} — plans are saved per zone, so she'll use {ZoneTable.NameFor(_charZoneStem)}'s.");

        if (i == 3)
        {
            int n = plan?.Waypoints.Count ?? 0;
            GrindLogLine(n >= 2
                ? $"Waypoints: {n} loaded for {zoneName} ({(WaypointOrderBox.SelectedIndex == 2 ? "looping" : WaypointOrderBox.SelectedIndex == 1 ? "random order" : "in sequence, ping-pong")})."
                : $"Waypoints mode, but {zoneName} has {n} saved waypoint(s) — she needs at least 2. Draw the route on the Maps page with that zone loaded, or she'll just roam.");
        }
        else
        {
            GrindLogLine(plan is { HasShape: true }
                ? $"Hunting zone: a {plan.ShapeType} is loaded for {zoneName}."
                : $"Hunting Zone mode, but no shape is saved for {zoneName} — draw one on the Maps page, or she'll just roam.");
        }

        if (string.IsNullOrWhiteSpace(_settings.HuntLocKey))
            GrindLogLine($"⚠ {mode} mode steers by your position, and no /loc key is set — set one in the Grind keybinds (or keep a repeating /loc macro running in-game), otherwise she can't navigate and will walk blind.");
    }

    /// <summary>Route order changed — the map overlay draws (or drops) the closing leg to match.</summary>
    private void WaypointOrder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_planHooked) RefreshPlanOverlay();
    }

    private string GrindModeSetting() => GrindModeBox.SelectedIndex switch
    { 1 => "camp", 2 => "zone", 3 => "waypoints", _ => "hunt" };

    private string GrindModeLabel() => HuntBox.IsChecked != true ? "Grind" : GrindModeBox.SelectedIndex switch
    { 1 => "Camp", 2 => "Zone", 3 => "Waypoints", _ => "Hunt" };

    // ---------------- previous grind sessions table ----------------

    private void GrindSess_Expanded(object sender, RoutedEventArgs e) => RefreshGrindSessions();
    private void GrindSessFilter_Changed(object sender, TextChangedEventArgs e) => RefreshGrindSessions();

    private void RefreshGrindSessions()
    {
        if (GrindSessGrid is null) return;
        string filter = (GrindSessFilter?.Text ?? "").Trim();
        List<GrindSessRow> rows = SessionStore.LoadAll()
            .Where(s => !string.Equals(s.Role, "Follower", StringComparison.OrdinalIgnoreCase))
            .Select(s => new GrindSessRow
            {
                When = s.StartedAt,
                Mode = s.Role,
                Zone = s.Trail.Keys.FirstOrDefault() ?? "",
                Hours = Math.Max(0, (s.EndedAt - s.StartedAt).TotalHours),
                Duration = (s.EndedAt - s.StartedAt) is { TotalMinutes: >= 0 } d ? $"{(int)d.TotalHours}:{d.Minutes:00}" : "0:00",
                Kills = s.Kills, Xp = s.XpTicks, Aa = s.AaPoints, Deaths = s.Deaths,
                Dealt = s.DmgDealt, Taken = s.DmgTaken,
            })
            .Where(r => filter.Length == 0
                        || r.Mode.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Zone.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.When)
            .ToList();
        GrindSessGrid.ItemsSource = rows;
        double hours = rows.Sum(r => r.Hours);
        GrindSessTally.Text = rows.Count == 0
            ? "no sessions match"
            : $"Σ {rows.Count} sessions · {(int)hours}:{(int)((hours % 1) * 60):00} played · "
              + $"{rows.Sum(r => r.Kills):n0} kills · {rows.Sum(r => r.Xp):n0} xp ticks · {rows.Sum(r => r.Aa):n0} AA · "
              + $"{rows.Sum(r => r.Deaths)} deaths · {rows.Sum(r => r.Dealt):n0} dealt · {rows.Sum(r => r.Taken):n0} taken";
    }
}
