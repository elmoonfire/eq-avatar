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
            "wp" => "click the map to drop waypoints in order",
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
        MapsView.SetPlanOverlay(wps, type, shape);
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
