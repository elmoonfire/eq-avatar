using System;
using System.Collections.Generic;

namespace EQAvatar.Spike.Map;

// Classic-EQ map-file parser — C# port of EQ Legends Companion's src/main/maps/parseMap.ts
// (github.com/jmoyers/everquest-companion, MIT). Their header documents the format, measured
// against the real 1,900-file corpus:
//
//     L  x1, y1, z1, x2, y2, z2, r, g, b        (9 fields)
//     P  x,  y,  z,  r,  g,  b,  size, label    (8 fields; the label may itself contain commas)
//
// Rules carried over verbatim because they were measured, not guessed:
//   * ~4.5% of P labels contain commas — the tail fields are re-joined, never truncated.
//   * Layer 2 (`zone_2.txt`) is a LEGEND drawn at off-map coordinates: excluded from bounds
//     and z-levels or the real map renders as a speck.
//   * A malformed line is COUNTED (Skipped), never thrown on — one bad line in a user pack
//     must not blank the map.
//   * P `size` is a text-size class 1..3, clamped, never a reason to drop a point.

/// <summary>One labeled point from a map file, coordinates in map space.</summary>
public sealed record MapPoint(double X, double Y, double Z, byte R, byte G, byte B,
                              int Size, string Label, string Display, int Layer);

/// <summary>Extent of the drawn layers (legend excluded).</summary>
public sealed class MapBounds
{
    public double MinX = double.PositiveInfinity, MaxX = double.NegativeInfinity;
    public double MinY = double.PositiveInfinity, MaxY = double.NegativeInfinity;
    public double MinZ = double.PositiveInfinity, MaxZ = double.NegativeInfinity;
    public bool IsEmpty => double.IsInfinity(MinX);
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public void Grow(double x, double y, double z)
    {
        MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x);
        MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
        MinZ = Math.Min(MinZ, z); MaxZ = Math.Max(MaxZ, z);
    }
}

/// <summary>Which pack supplied one layer of a zone (shown as attribution in the UI).</summary>
public sealed record MapSource(int Layer, string PackId, string File);

/// <summary>One parsed layer file: flat segment arrays + its labeled points.</summary>
public sealed class MapParseResult
{
    public int Layer;
    /// <summary>[x1,y1,z1,x2,y2,z2] × Count, flattened.</summary>
    public readonly List<double> Coords = new();
    /// <summary>[r,g,b] × Count, flattened.</summary>
    public readonly List<byte> Rgb = new();
    public int Count;
    public readonly List<MapPoint> Points = new();
    public int Skipped;
}

/// <summary>A whole zone, every layer folded in, renderer-ready.</summary>
public sealed class MapData
{
    public string Zone = "";
    public List<MapSource> Sources = new();
    /// <summary>[x1,y1,z1,x2,y2,z2] × SegmentCount.</summary>
    public double[] Coords = Array.Empty<double>();
    /// <summary>[r,g,b] × SegmentCount.</summary>
    public byte[] SegRgb = Array.Empty<byte>();
    /// <summary>Layer per segment (0..3).</summary>
    public byte[] SegLayer = Array.Empty<byte>();
    public int SegmentCount;
    public List<MapPoint> Points = new();
    public MapBounds Bounds = new();
    /// <summary>Distinct min-z per segment, ascending — the floor stepper's raw input.</summary>
    public double[] ZLevels = Array.Empty<double>();
    /// <summary>Map credits mined from the legend layer (the packs' only attribution signal).</summary>
    public List<string> Credits = new();
    public int Skipped;
}

public static class EqMapParser
{
    public const int LegendLayer = 2;

    private static double? Num(string field)
    {
        string t = field.Trim();
        if (t.Length == 0) return null;   // Number('') is 0 in JS — the same trap exists with Parse
        return double.TryParse(t, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
               && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
    }

    private static byte ByteClamp(double v) => (byte)Math.Min(255, Math.Max(0, Math.Round(v)));
    private static int SizeClass(double v) { int n = (int)Math.Round(v); return n <= 1 ? 1 : n >= 3 ? 3 : 2; }

    /// <summary>Parse one map file's text. Never throws; a bad line increments Skipped.</summary>
    public static MapParseResult ParseMapText(string text, int layer)
    {
        var res = new MapParseResult { Layer = layer };
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            string[] fields = line.Substring(1).Split(',');
            if (line[0] is 'L' or 'l')
            {
                if (fields.Length != 9) { res.Skipped++; continue; }
                var v = new double[9];
                bool ok = true;
                for (int i = 0; i < 9; i++) { double? n = Num(fields[i]); if (n is null) { ok = false; break; } v[i] = n.Value; }
                if (!ok) { res.Skipped++; continue; }
                res.Coords.Add(v[0]); res.Coords.Add(v[1]); res.Coords.Add(v[2]);
                res.Coords.Add(v[3]); res.Coords.Add(v[4]); res.Coords.Add(v[5]);
                res.Rgb.Add(ByteClamp(v[6])); res.Rgb.Add(ByteClamp(v[7])); res.Rgb.Add(ByteClamp(v[8]));
                res.Count++;
            }
            else if (line[0] is 'P' or 'p')
            {
                if (fields.Length < 8) { res.Skipped++; continue; }
                var v = new double[7];
                bool ok = true;
                for (int i = 0; i < 7; i++) { double? n = Num(fields[i]); if (n is null) { ok = false; break; } v[i] = n.Value; }
                if (!ok) { res.Skipped++; continue; }
                // The label may contain commas — re-join the raw tail (the 4.5% fix).
                string label = string.Join(",", fields, 7, fields.Length - 7).Trim();
                res.Points.Add(new MapPoint(v[0], v[1], v[2], ByteClamp(v[3]), ByteClamp(v[4]), ByteClamp(v[5]),
                                            SizeClass(v[6]), label, label.Replace('_', ' '), layer));
            }
            else res.Skipped++;
        }
        return res;
    }

    /// <summary>Fold every layer of one zone into renderer-ready MapData. Layers may arrive in
    /// any order and any may be missing; an empty layer file is a valid empty layer.</summary>
    public static MapData BuildMapData(IReadOnlyList<MapParseResult> parts, string zone, List<MapSource> sources)
    {
        var data = new MapData { Zone = zone, Sources = sources };
        int count = 0;
        foreach (MapParseResult p in parts) count += p.Count;
        data.Coords = new double[count * 6];
        data.SegRgb = new byte[count * 3];
        data.SegLayer = new byte[count];
        data.SegmentCount = count;
        int seg = 0;
        var zSeen = new SortedSet<double>();
        foreach (MapParseResult p in parts)
        {
            p.Coords.CopyTo(data.Coords, seg * 6);
            p.Rgb.CopyTo(data.SegRgb, seg * 3);
            for (int i = 0; i < p.Count; i++) data.SegLayer[seg + i] = (byte)p.Layer;
            seg += p.Count;
            data.Points.AddRange(p.Points);
            data.Skipped += p.Skipped;

            if (p.Layer == LegendLayer) continue;   // legend: geometry yes, extent/z-levels never
            for (int i = 0; i + 5 < p.Coords.Count; i += 6)
            {
                data.Bounds.Grow(p.Coords[i], p.Coords[i + 1], p.Coords[i + 2]);
                data.Bounds.Grow(p.Coords[i + 3], p.Coords[i + 4], p.Coords[i + 5]);
                zSeen.Add(Math.Min(p.Coords[i + 2], p.Coords[i + 5]));
            }
            foreach (MapPoint pt in p.Points) data.Bounds.Grow(pt.X, pt.Y, pt.Z);
        }
        data.ZLevels = new double[zSeen.Count];
        zSeen.CopyTo(data.ZLevels);
        MineCredits(parts, data.Credits);
        return data;
    }

    // Attribution mined from the legend layer — the only credit signal these packs ship.
    private static void MineCredits(IReadOnlyList<MapParseResult> parts, List<string> outCredits)
    {
        var seen = new HashSet<string>();
        foreach (MapParseResult p in parts)
        {
            if (p.Layer != LegendLayer) continue;
            foreach (MapPoint pt in p.Points)
            {
                string d = pt.Display;
                bool credit = d.StartsWith("Original Map:", StringComparison.OrdinalIgnoreCase)
                           || d.StartsWith("Revised Map:", StringComparison.OrdinalIgnoreCase)
                           || d.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                           || d.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                           || d.Contains("www.", StringComparison.OrdinalIgnoreCase);
                if (credit && seen.Add(d)) outCredits.Add(d);
            }
        }
    }

    /// <summary>/loc → map-file coordinates. The Companion measured this against 7,423
    /// wiki-stated coordinates across 119 zones (99.4% landed inside their zone's extent):
    /// mapX = -ew, mapY = -ns. /loc prints NORTH/SOUTH FIRST, then east/west, then elevation.</summary>
    public static (double mapX, double mapY) MapFromLoc(double ns, double ew) => (-ew, -ns);
}
