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
/// is "Stats and Resists" plus its 13. Every value inside a row has its own fixed
/// X offset and width, which is what lets the reader OCR one number at a time instead of trying
/// to pull a whole line apart. That distinction is the entire bug fix: the old reader needed a
/// label and its value to land on the same OCR line, and the game draws them far enough apart
/// that Windows OCR always split them into separate lines.
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

    /// <summary>One value box inside a row. <paramref name="Index"/> is its position in the
    /// snapshot's number list: 0 = current, 1 = max/softcap, 2 = heroic/evasion.</summary>
    public readonly record struct Field(string Key, int Index, int X, int W);

    /// <summary>One row of the grid. <paramref name="Order"/> is its index in the tile layout,
    /// which decides the column (Order / 14) and the row within it (Order % 14).</summary>
    public readonly record struct Row(int Order, string Label, string Key, Field[] Fields);

    public static readonly Row[] Rows =
    {
        new(0, "Character Vitals", "", new Field[]{  }),
        new(1, "HP", "hp", new Field[]{ new("hp", 0, 58, 55), new("hp", 1, 120, 55) }),
        new(2, "Mana", "mana", new Field[]{ new("mana", 0, 58, 55), new("mana", 1, 120, 55) }),
        new(3, "End", "end", new Field[]{ new("end", 0, 58, 55), new("end", 1, 120, 55) }),
        new(4, "AC", "ac", new Field[]{ new("ac", 0, 30, 55), new("ac", 1, 92, 30), new("ac", 2, 127, 55) }),
        new(5, "Attack", "attack", new Field[]{ new("attack", 0, 65, 55), new("attack", 1, 127, 55) }),
        new(6, "Attack Speed %", "attack speed", new Field[]{ new("attack speed", 0, 95, 65) }),
        new(7, "Velocity", "velocity", new Field[]{ new("velocity", 0, 95, 65) }),
        new(8, "HP Regen", "hp regen", new Field[]{ new("hp regen", 0, 85, 75) }),
        new(9, "Mana Regen", "mana regen", new Field[]{ new("mana regen", 0, 85, 75) }),
        new(10, "End Regen", "end regen", new Field[]{ new("end regen", 0, 85, 75) }),
        new(11, "Primary DPS", "primary dps", new Field[]{  }),
        new(12, "Secondary DPS", "secondary dps", new Field[]{  }),
        new(13, "Ranged DPS", "ranged dps", new Field[]{  }),
        new(14, "Stats and Resists", "", new Field[]{  }),
        new(15, "Strength", "strength", new Field[]{ new("strength", 0, 67, 35), new("strength", 1, 108, 35), new("strength", 2, 135, 30) }),
        new(16, "Stamina", "stamina", new Field[]{ new("stamina", 0, 67, 35), new("stamina", 1, 108, 35), new("stamina", 2, 135, 30) }),
        new(17, "Intelligence", "intelligence", new Field[]{ new("intelligence", 0, 67, 35), new("intelligence", 1, 108, 35), new("intelligence", 2, 135, 30) }),
        new(18, "Wisdom", "wisdom", new Field[]{ new("wisdom", 0, 67, 35), new("wisdom", 1, 108, 35), new("wisdom", 2, 135, 30) }),
        new(19, "Agility", "agility", new Field[]{ new("agility", 0, 67, 35), new("agility", 1, 108, 35), new("agility", 2, 135, 30) }),
        new(20, "Dexterity", "dexterity", new Field[]{ new("dexterity", 0, 67, 35), new("dexterity", 1, 108, 35), new("dexterity", 2, 135, 30) }),
        new(21, "Charisma", "charisma", new Field[]{ new("charisma", 0, 67, 35), new("charisma", 1, 108, 35), new("charisma", 2, 135, 30) }),
        new(22, "SV. Magic", "sv magic", new Field[]{ new("sv magic", 0, 96, 35), new("sv magic", 1, 137, 35) }),
        new(23, "SV. Fire", "sv fire", new Field[]{ new("sv fire", 0, 96, 35), new("sv fire", 1, 137, 35) }),
        new(24, "SV. Cold", "sv cold", new Field[]{ new("sv cold", 0, 96, 35), new("sv cold", 1, 137, 35) }),
        new(25, "SV. Disease", "sv disease", new Field[]{ new("sv disease", 0, 96, 35), new("sv disease", 1, 137, 35) }),
        new(26, "SV. Poison", "sv poison", new Field[]{ new("sv poison", 0, 96, 35), new("sv poison", 1, 137, 35) }),
        new(27, "SV. Void", "sv void", new Field[]{ new("sv void", 0, 96, 35), new("sv void", 1, 137, 35) }),
    };

    public static int ColumnOf(int order) => order / RowsPerColumn;
    public static int RowInColumn(int order) => order % RowsPerColumn;

    /// <summary>The two header rows the reader anchors on, one per column.</summary>
    public const string VitalsAnchor = "character vitals";
    public const string StatsAnchor  = "stats and resists";
}
