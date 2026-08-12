using System;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// The default_modern Inventory window's stat grid, transcribed verbatim from the game's own
/// <c>uifiles/default_modern/EQUI_InventoryWindow.xml</c>. This is not a guess about where the
/// numbers sit — it is the same table the client itself lays the window out from.
///
/// The <c>IW_Stats</c> TileLayoutBox is 350x230 and holds 28 row Screens, each 175x14, with
/// <c>HorizontalFirst=false</c> (column-major), <c>Spacing=2</c> down and
/// <c>SecondarySpacing=6</c> across. So the row pitch is 16 and the column stride is 181, and
/// (230+2)/16 = 14 rows per column: column 0 is "Character Vitals" plus its 13 rows, column 1
/// is "Stats and Resists" plus its 13.
///
/// CLIP BOUNDS MATTER MORE THAN THE DECLARED WIDTHS. A label's Size is the box it may draw in,
/// not the space it has to itself, and neighbours overlap: <c>IWS_CurrentStrength</c> is 35 wide
/// at x=67 but its "/" divider starts at x=103, one single unit past its right edge.
/// <c>IWS_MaxStrength</c> (108..143) runs underneath <c>IWS_StrengthPlus</c> at 135, and
/// <c>IWS_HeroicStrength</c> starts at 135 directly beneath that same "+". Reading a box at its
/// declared width therefore captures the neighbouring glyph — and a clipped "/" OCRs as a "1",
/// silently turning 257 into 2571. Every field below carries the exact span it can be read from
/// without touching anything else.
///
/// A marker only blocks a read if it actually DRAWS something. <c>IWS_HastePercent</c> is a
/// 15-wide label with EMPTY text sitting at x=145, exactly on top of where the right-aligned
/// haste value is drawn in its 95..160 box (the "%" the user sees is part of the row's own
/// "Attack Speed %" caption). Treating it as a divider clipped the value away entirely and
/// Attack Speed read as nothing at all. It is the only empty-text marker in the grid.
/// </summary>
public static class InventoryLayout
{
    public const int RowHeight = 14;
    public const int RowSpacing = 2;
    public const int RowPitch = RowHeight + RowSpacing;   // 16
    public const int ColumnPitch = 175;                   // one IWS_*Screen wide
    public const int ColumnSpacing = 6;                   // IW_Stats SecondarySpacing
    public const int ColumnStride = ColumnPitch + ColumnSpacing;   // 181 — column 0 x to column 1 x
    public const int RowsPerColumn = 14;                  // (230 + 2) / 16, floored

    /// <summary>
    /// One value box inside a row. <paramref name="Index"/> is its position in the snapshot's
    /// number list: 0 = current, 1 = max/softcap, 2 = heroic/evasion. <paramref name="X"/> and
    /// <paramref name="W"/> are what the skin declares; <paramref name="ClipL"/> and
    /// <paramref name="ClipR"/> are the span that is actually safe to OCR.
    /// </summary>
    public readonly record struct Field(string Key, int Index, int X, int W, int ClipL, int ClipR);

    /// <summary>One row of the grid. <paramref name="Order"/> is its index in the tile layout,
    /// which decides the column (Order / 14) and the row within it (Order % 14).</summary>
    public readonly record struct Row(int Order, string Label, string Key, Field[] Fields);

    public static readonly Row[] Rows =
    {
        new(0, "Character Vitals", "", new Field[]{  }),
        new(1, "HP", "hp", new Field[]{ new("hp", 0, 58, 55, 58, 113), new("hp", 1, 120, 55, 120, 175) }),
        new(2, "Mana", "mana", new Field[]{ new("mana", 0, 58, 55, 58, 113), new("mana", 1, 120, 55, 120, 175) }),
        new(3, "End", "end", new Field[]{ new("end", 0, 58, 55, 58, 113), new("end", 1, 120, 55, 120, 175) }),
        new(4, "AC", "ac", new Field[]{ new("ac", 0, 30, 55, 30, 85), new("ac", 1, 92, 30, 92, 122), new("ac", 2, 127, 55, 127, 182) }),
        new(5, "Attack", "attack", new Field[]{ new("attack", 0, 65, 55, 65, 120), new("attack", 1, 127, 55, 127, 182) }),
        new(6, "Attack Speed %", "attack speed", new Field[]{ new("attack speed", 0, 95, 65, 95, 160) }),
        new(7, "Velocity", "velocity", new Field[]{ new("velocity", 0, 95, 65, 95, 160) }),
        new(8, "HP Regen", "hp regen", new Field[]{ new("hp regen", 0, 85, 75, 85, 160) }),
        new(9, "Mana Regen", "mana regen", new Field[]{ new("mana regen", 0, 85, 75, 85, 160) }),
        new(10, "End Regen", "end regen", new Field[]{ new("end regen", 0, 85, 75, 85, 160) }),
        new(11, "Primary DPS", "primary dps", new Field[]{ new("primary dps", 0, 85, 75, 85, 160) }),
        new(12, "Secondary DPS", "secondary dps", new Field[]{ new("secondary dps", 0, 85, 75, 85, 160) }),
        new(13, "Ranged DPS", "ranged dps", new Field[]{ new("ranged dps", 0, 85, 75, 85, 160) }),
        new(14, "Stats and Resists", "", new Field[]{  }),
        new(15, "Strength", "strength", new Field[]{ new("strength", 0, 67, 35, 67, 102), new("strength", 1, 108, 35, 108, 135), new("strength", 2, 135, 30, 145, 165) }),
        new(16, "Stamina", "stamina", new Field[]{ new("stamina", 0, 67, 35, 67, 102), new("stamina", 1, 108, 35, 108, 135), new("stamina", 2, 135, 30, 145, 165) }),
        new(17, "Intelligence", "intelligence", new Field[]{ new("intelligence", 0, 67, 35, 67, 102), new("intelligence", 1, 108, 35, 108, 135), new("intelligence", 2, 135, 30, 145, 165) }),
        new(18, "Wisdom", "wisdom", new Field[]{ new("wisdom", 0, 67, 35, 67, 102), new("wisdom", 1, 108, 35, 108, 135), new("wisdom", 2, 135, 30, 145, 165) }),
        new(19, "Agility", "agility", new Field[]{ new("agility", 0, 67, 35, 67, 102), new("agility", 1, 108, 35, 108, 135), new("agility", 2, 135, 30, 145, 165) }),
        new(20, "Dexterity", "dexterity", new Field[]{ new("dexterity", 0, 67, 35, 67, 102), new("dexterity", 1, 108, 35, 108, 135), new("dexterity", 2, 135, 30, 145, 165) }),
        new(21, "Charisma", "charisma", new Field[]{ new("charisma", 0, 67, 35, 67, 102), new("charisma", 1, 108, 35, 108, 135), new("charisma", 2, 135, 30, 145, 165) }),
        new(22, "SV. Magic", "sv magic", new Field[]{ new("sv magic", 0, 96, 35, 96, 131), new("sv magic", 1, 137, 35, 137, 172) }),
        new(23, "SV. Fire", "sv fire", new Field[]{ new("sv fire", 0, 96, 35, 96, 131), new("sv fire", 1, 137, 35, 137, 172) }),
        new(24, "SV. Cold", "sv cold", new Field[]{ new("sv cold", 0, 96, 35, 96, 131), new("sv cold", 1, 137, 35, 137, 172) }),
        new(25, "SV. Disease", "sv disease", new Field[]{ new("sv disease", 0, 96, 35, 96, 131), new("sv disease", 1, 137, 35, 137, 172) }),
        new(26, "SV. Poison", "sv poison", new Field[]{ new("sv poison", 0, 96, 35, 96, 131), new("sv poison", 1, 137, 35, 137, 172) }),
        new(27, "SV. Void", "sv void", new Field[]{ new("sv void", 0, 96, 35, 96, 131), new("sv void", 1, 137, 35, 137, 172) }),
    };

    public static int ColumnOf(int order) => order / RowsPerColumn;
    public static int RowInColumn(int order) => order % RowsPerColumn;

    /// <summary>The two header rows the reader anchors on, one per column.</summary>
    public const string VitalsAnchor = "character vitals";
    public const string StatsAnchor  = "stats and resists";

    // ---------------------------------------------------------------- right-anchored fields
    //
    // Weight is not in the stat grid. Its labels hang off the window's RIGHT edge
    // (LeftAnchorToLeft=false), so their offsets are measured backwards from that edge and the
    // window is resizable. Rather than guess the window width, the reader finds the "Weight"
    // caption itself: the caption is left-aligned at WeightCaptionFromRight units in from the
    // right edge, so one OCR hit fixes the edge, and the value boxes follow from it.

    /// <summary><c>IW_Weight</c> LeftAnchorOffset — the caption's distance in from the right edge.</summary>
    public const int WeightCaptionFromRight = 110;
    /// <summary><c>IW_CurrentWeight</c>: right-aligned, spans 70..33 units in from the right edge.</summary>
    public const int WeightCurFromRight = 70, WeightCurToRight = 33;
    /// <summary><c>IW_MaxWeight</c>: left-aligned, spans 25..0 units in from the right edge.</summary>
    public const int WeightMaxFromRight = 25, WeightMaxToRight = 0;
    /// <summary><c>IW_WornWeightNumber</c>: right-aligned, spans 24..4 units in from the right edge.</summary>
    public const int WornWeightFromRight = 24, WornWeightToRight = 4;
}
