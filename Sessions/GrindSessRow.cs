using System;

namespace EQAvatar.Spike.Sessions;

/// <summary>One row of the Grind page's session-history table (bindable, sortable).</summary>
public sealed class GrindSessRow
{
    public DateTime When { get; init; }
    public string Mode { get; init; } = "";
    public string Zone { get; init; } = "";
    public string Duration { get; init; } = "";
    public double Hours { get; init; }
    public int Kills { get; init; }
    public int Xp { get; init; }
    public int Aa { get; init; }
    public int Deaths { get; init; }
    public long Dealt { get; init; }
    public long Taken { get; init; }
}
