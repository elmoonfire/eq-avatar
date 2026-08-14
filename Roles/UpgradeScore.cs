using System;

namespace EQAvatar.Spike.Roles;

/// <summary>
/// The upgrade ladder as the GAME actually models it: one internal score out of 1024.
///
/// This replaces an assumption that was nearly right and quietly wrong at the edges. The app used
/// to think in tiers with a progress bar hanging off each one, and merged items as though each one
/// bought a step. Hayden's account of the mechanic (confirmed against his own +9) is simpler and
/// stronger:
///
///   • every item carries a SCORE, 1…1024. A fresh drop is 1.
///   • its tier is the highest power of two the score has reached: score 518 → +9, because
///     512 ≤ 518 &lt; 1024.
///   • the counter the game shows you is the remainder over the cost of the NEXT step:
///     518 − 512 = 6, out of 512. Hence "6 / 512".
///   • MERGING ADDS SCORES. A 510 folded into an 8 is a 518 — the same +9. Nothing is lost, and
///     the ORDER YOU MERGE IN CANNOT MATTER, because addition is addition.
///
/// That last point is why this type exists rather than a pile of tier arithmetic. Every "how far
/// does this get me" question becomes one addition and one log2, and a forecast that is exact
/// beats a simulation that is merely careful. It also gives the sweep a better witness: a merge
/// must RAISE THE SCORE, which is one comparison that survives a level-up (where the displayed
/// numerator drops and the denominator doubles — two changes that used to need special-casing).
/// </summary>
public static class UpgradeScore
{
    /// <summary>A +10 — the top of the ladder, and the point where merging more is waste.</summary>
    public const int Max = 1024;
    public const int MaxTier = 10;

    /// <summary>The tier a score has reached. 0 for anything under 2.</summary>
    public static int TierFor(long score)
    {
        if (score < 2) return 0;
        int t = 0;
        while (t < MaxTier && score >= (1L << (t + 1))) t++;
        return t;
    }

    /// <summary>What the game's own counter reads for a score: remainder over the next step's cost.</summary>
    public static (int Have, int Need) CounterFor(long score)
    {
        int tier = TierFor(score);
        int step = 1 << tier;
        return ((int)Math.Max(0, score - step), step);
    }

    /// <summary>
    /// The score behind a counter reading. Null when the pair isn't one this ladder can produce —
    /// the denominator must be a power of two and the numerator must sit inside its own step, so a
    /// misread like "4/1000" or "700/512" is rejected rather than turned into a confident number.
    /// </summary>
    public static long? ScoreFrom(int have, int need)
    {
        if (need <= 0 || need > Max || (need & (need - 1)) != 0) return null;
        if (have < 0) return null;
        // The top step first. "6/1024" is not a score of 1030 — there is no such score — it is a
        // misread, and treating it as one told the sweep the item was already finished and aborted
        // the run on a single bad frame.
        if (need == Max) return have == 0 ? Max : (long?)null;
        // A full bar means the step is paid for: the game shows the remainder over the NEXT cost,
        // so "512/512" is a score of 1024. (Which representation it actually prints on a finished
        // item is unverified — this way both readings land on the same answer instead of one of
        // them refusing to draw anything.)
        if (have == need) return Math.Min(Max, (long)need * 2);
        if (have > need) return null;
        return need + have;
    }

    /// <summary>Base drops (score 1 each) still needed to reach a target tier.</summary>
    public static long ToReach(long score, int tier)
    {
        long want = 1L << Math.Clamp(tier, 0, MaxTier);
        return Math.Max(0, want - Math.Max(0, score));
    }

    /// <summary>Score after folding in <paramref name="drops"/> base copies, capped at a +10.</summary>
    public static long Plus(long score, long drops) => Math.Min(Max, Math.Max(0, score) + Math.Max(0, drops));
}
