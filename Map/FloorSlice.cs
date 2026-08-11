using System;
using System.Collections.Generic;

namespace EQAvatar.Spike.Map;

// Floor clustering for multi-level zones — C# port of EQ Legends Companion's
// src/renderer/src/features/maps/floorSlice.ts (github.com/jmoyers/everquest-companion, MIT).
// Their rules: recursive largest-gap splitting of the distinct z levels, cuts constrained to
// the middle of a run so outliers don't get peeled, stopping at a target band height =
// max(hint, extent/maxBands, 12 units), 12 bands max. The stepper is MANUAL by default ("All
// levels") — but unlike the Companion we DO see live /loc z, so the caller may auto-select.

/// <summary>One clustered floor. Lo/Hi are the real extremes of the z levels inside it.</summary>
public sealed record FloorBand(double Lo, double Hi, int Count);

public static class FloorSlice
{
    public const int MaxBands = 12;
    public const double DefaultBandHeight = 20;   // the in-game client's own 10/10 default
    public const double MinBandHeight = 12;       // thinner than a step is undulation, not a floor
    private const double CentreWindow = 0.6;

    public static List<FloorBand> Bands(IReadOnlyList<double> zLevels, int maxBands = MaxBands)
    {
        var bands = new List<FloorBand>();
        if (zLevels.Count == 0) return bands;
        double extent = zLevels[^1] - zLevels[0];
        double target = Math.Max(Math.Max(DefaultBandHeight, extent / Math.Max(1, maxBands)), MinBandHeight);

        var runs = new List<(int lo, int hi)> { (0, zLevels.Count - 1) };
        while (runs.Count < maxBands)
        {
            // The tallest run still above the target height is the one to split.
            int pick = -1; double tallest = target;
            for (int i = 0; i < runs.Count; i++)
            {
                double h = zLevels[runs[i].hi] - zLevels[runs[i].lo];
                if (h > tallest) { tallest = h; pick = i; }
            }
            if (pick < 0) break;
            (int lo, int hi) = runs[pick];

            // Cut at the largest gap whose midpoint sits inside the run's centre window;
            // fall back to the gap nearest the height midpoint.
            double runLo = zLevels[lo], runHi = zLevels[hi];
            double winLo = runLo + (runHi - runLo) * (1 - CentreWindow) / 2;
            double winHi = runHi - (runHi - runLo) * (1 - CentreWindow) / 2;
            double mid = (runLo + runHi) / 2;
            int cut = -1; double bestGap = 0;
            int near = -1; double nearDist = double.MaxValue;
            for (int i = lo; i < hi; i++)
            {
                double gap = zLevels[i + 1] - zLevels[i];
                double gapMid = (zLevels[i + 1] + zLevels[i]) / 2;
                if (gapMid >= winLo && gapMid <= winHi && gap > bestGap) { bestGap = gap; cut = i; }
                double d = Math.Abs(gapMid - mid);
                if (gap > 0 && d < nearDist) { nearDist = d; near = i; }
            }
            if (cut < 0) cut = near;
            if (cut < 0) break;                       // a run of identical z — nothing to split
            runs.RemoveAt(pick);
            runs.Add((lo, cut));
            runs.Add((cut + 1, hi));
            runs.Sort((a, b) => a.lo.CompareTo(b.lo));
        }

        foreach ((int lo, int hi) in runs)
            bands.Add(new FloorBand(zLevels[lo], zLevels[hi], hi - lo + 1));
        return bands;
    }

    /// <summary>The half-open z range band <paramref name="index"/> claims: midpoints of the
    /// gaps to its neighbours, so a segment between floors joins the nearer one.</summary>
    public static (double lo, double hi) BandRange(IReadOnlyList<FloorBand> bands, int index)
    {
        if (index < 0 || index >= bands.Count) return (double.NegativeInfinity, double.PositiveInfinity);
        FloorBand b = bands[index];
        double lo = index > 0 ? (bands[index - 1].Hi + b.Lo) / 2 : double.NegativeInfinity;
        double hi = index < bands.Count - 1 ? (b.Hi + bands[index + 1].Lo) / 2 : double.PositiveInfinity;
        return (lo, hi);
    }

    /// <summary>Which band a z belongs to, or -1.</summary>
    public static int BandOfZ(IReadOnlyList<FloorBand> bands, double z)
    {
        for (int i = 0; i < bands.Count; i++)
        {
            (double lo, double hi) = BandRange(bands, i);
            if (z >= lo && z <= hi) return i;
        }
        return -1;
    }

    /// <summary>A segment lives on its LOWER endpoint's floor (a sloped segment must not smear).</summary>
    public static double SegmentZ(double z1, double z2) => Math.Min(z1, z2);

    public static string BandLabel(FloorBand b) => $"{Math.Round(b.Lo)} … {Math.Round(b.Hi)}";
}
