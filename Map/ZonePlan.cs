using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQAvatar.Spike.Map;

/// <summary>
/// A zone's hunting plan, drawn on the Maps page and saved per zone stem: a WAYPOINT route the
/// bot can patrol (sequence/ping-pong or random) and/or a HUNTING ZONE shape (circle, rectangle,
/// or polygon) it stays inside. Coordinates are LOC space ([ew, ns] pairs) so they mean the same
/// thing to the hunt math and the map (map draws at -ew, -ns).
/// </summary>
public sealed class ZonePlan
{
    public List<double[]> Waypoints { get; set; } = new();
    /// <summary>"" | "circle" (pts: center, edge) | "rect" (pts: two corners) | "poly" (pts: vertices)</summary>
    public string ShapeType { get; set; } = "";
    public List<double[]> ShapePts { get; set; } = new();

    public bool HasShape => ShapeType switch
    {
        "circle" or "rect" => ShapePts.Count >= 2,
        "poly" => ShapePts.Count >= 3,
        _ => false,
    };

    public bool Contains(double ew, double ns)
    {
        if (!HasShape) return true;
        switch (ShapeType)
        {
            case "circle":
            {
                double r2 = Sq(ShapePts[1][0] - ShapePts[0][0]) + Sq(ShapePts[1][1] - ShapePts[0][1]);
                return Sq(ew - ShapePts[0][0]) + Sq(ns - ShapePts[0][1]) <= r2;
            }
            case "rect":
            {
                double x0 = Math.Min(ShapePts[0][0], ShapePts[1][0]), x1 = Math.Max(ShapePts[0][0], ShapePts[1][0]);
                double y0 = Math.Min(ShapePts[0][1], ShapePts[1][1]), y1 = Math.Max(ShapePts[0][1], ShapePts[1][1]);
                return ew >= x0 && ew <= x1 && ns >= y0 && ns <= y1;
            }
            case "poly":
            {
                bool inside = false;                                  // ray cast
                for (int i = 0, j = ShapePts.Count - 1; i < ShapePts.Count; j = i++)
                {
                    double xi = ShapePts[i][0], yi = ShapePts[i][1], xj = ShapePts[j][0], yj = ShapePts[j][1];
                    if (yi > ns != yj > ns && ew < (xj - xi) * (ns - yi) / (yj - yi) + xi) inside = !inside;
                }
                return inside;
            }
            default: return true;
        }
    }

    public (double ew, double ns) Center()
    {
        if (ShapeType == "circle" && ShapePts.Count >= 1) return (ShapePts[0][0], ShapePts[0][1]);
        if (ShapePts.Count > 0) return (ShapePts.Average(p => p[0]), ShapePts.Average(p => p[1]));
        if (Waypoints.Count > 0) return (Waypoints.Average(p => p[0]), Waypoints.Average(p => p[1]));
        return (0, 0);
    }

    private static double Sq(double v) => v * v;

    // ---------------- persistence (per zone stem) ----------------

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "areas");

    private static string PathFor(string zoneStem) => Path.Combine(Dir, zoneStem + ".json");

    public static ZonePlan? Load(string zoneStem)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zoneStem) || !File.Exists(PathFor(zoneStem))) return null;
            return JsonSerializer.Deserialize<ZonePlan>(File.ReadAllText(PathFor(zoneStem)));
        }
        catch { return null; }
    }

    public void Save(string zoneStem)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(zoneStem), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
