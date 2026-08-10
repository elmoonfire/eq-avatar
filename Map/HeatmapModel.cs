using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using EQAvatar.Spike.Log;

namespace EQAvatar.Spike.Map;

/// <summary>
/// Accumulates /loc points per zone for the session. Keyed by zone name so continent- and
/// world-level roll-ups can layer on later with a zone→continent metadata table.
/// </summary>
public sealed class HeatmapModel
{
    private readonly Dictionary<string, List<Point>> _zones = new();
    private string _current = "Unknown";

    private static readonly Regex ZoneRe =
        new(@"You have entered\s+(?:the\s+)?(?<z>.+?)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyCollection<string> Zones => _zones.Keys;
    public string Current => _current;

    public IReadOnlyList<Point> PointsFor(string? zone) =>
        zone != null && _zones.TryGetValue(zone, out List<Point>? l) ? l : new List<Point>();

    /// <summary>
    /// Observed movement bounds for a zone (min/max X/Y over every /loc seen). The Hunt engine
    /// uses this to keep the character inside the area you've actually explored, and the map
    /// uses it to size the backdrop. Null until at least two points exist.
    /// </summary>
    public (double minX, double maxX, double minY, double maxY)? BoundsFor(string? zone)
    {
        if (zone == null || !_zones.TryGetValue(zone, out List<Point>? pts) || pts.Count < 2) return null;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (Point p in pts)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        return (minX, maxX, minY, maxY);
    }

    public void Feed(LogEvent ev)
    {
        if (ev.Kind == LogEventKind.Zone)
        {
            Match m = ZoneRe.Match(ev.Text);
            if (m.Success)
            {
                _current = m.Groups["z"].Value.Trim();
                if (!_zones.ContainsKey(_current)) _zones[_current] = new List<Point>();
            }
        }
        else if (ev.Kind == LogEventKind.Location && ev.X is double x && ev.Y is double y)
        {
            if (!_zones.ContainsKey(_current)) _zones[_current] = new List<Point>();
            _zones[_current].Add(new Point(x, y));
        }
    }

    public void Clear() { _zones.Clear(); _current = "Unknown"; }
}
