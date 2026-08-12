using System;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// The 23 equipment slots of the default_modern Inventory window, transcribed from the client's
/// own <c>EQUI_InventoryWindow.xml</c> — the same source as <see cref="InventoryLayout"/>.
///
/// WHERE THE GRID SITS. The containers nest
/// <c>InventoryWindow → IW_InvPage (0,22) → IW_Equipment (8,7)</c>, so a slot's window-relative
/// origin is <c>(8 + X, 29 + Y)</c>. The stat grid is <c>IW_InvPage (0,22) → IW_Stats (8,188)</c>,
/// i.e. window-relative <c>(8, 210)</c>. That shared parentage is what lets the equipment grid be
/// located from the anchor the stat reader ALREADY solves: the "Character Vitals" caption sits at
/// x=0 of row 0 of column 0, so the two x=8 offsets cancel and a slot lands at
/// <c>(anchorX + X·s, anchorY + (Y − 183)·s)</c>, size <c>40·s</c>. No second search, no
/// assumption about where the window is or how big it is.
///
/// The 183 is <c>210 − 29 + 2</c>: the stat grid's offset from the equipment grid, plus two units
/// for the inset of the OCR ink inside its 14-unit row (the anchor's bounding box starts at the
/// top of the glyphs, not the top of the row). It is the one estimated number here, and it is
/// worth two units out of a forty-unit slot.
///
/// SLOT NAMES are the standard EverQuest slot IDs, confirmed twice against a live screenshot:
/// the "Neck" caption appears over InvSlot5 (x=150) and "Ammo" over InvSlot22 (x=216), exactly
/// where those IDs predict.
/// </summary>
public static class EquipmentLayout
{
    /// <summary>Slot side, in UI units. Every equipment slot is 40x40.</summary>
    public const int SlotSize = 40;

    /// <summary>
    /// Y offset from the stat-grid anchor back to the equipment grid's origin, in UI units.
    /// (IW_Stats 210) − (IW_Equipment 29) + (~2 units of glyph inset) = 183.
    /// </summary>
    public const int AnchorToEquipmentY = 183;

    /// <summary>The animated class emblem: window-relative (11, 40), 75x142. Its origin relative
    /// to the same anchor is (3, 40 − 183 + 29) — kept here so the profile page can place it.</summary>
    public const int ClassAnimX = 3, ClassAnimY = 11, ClassAnimW = 75, ClassAnimH = 142;

    /// <param name="Id">EverQuest slot id, matching <c>InvSlotN</c> and <c>PersonaInvSlotN</c>.</param>
    /// <param name="Name">Human name for the slot.</param>
    /// <param name="X">X inside IW_Equipment, in UI units.</param>
    /// <param name="Y">Y inside IW_Equipment, in UI units.</param>
    public readonly record struct Slot(int Id, string Name, int X, int Y);

    /// <summary>In slot-id order, so <c>Slots[n].Id == n</c>.</summary>
    public static readonly Slot[] Slots =
    {
        new(0,  "Charm",        259, 130),
        new(1,  "Left Ear",     107,   1),
        new(2,  "Head",         236,   1),
        new(3,  "Face",         193,   1),
        new(4,  "Right Ear",    279,   1),
        new(5,  "Neck",         150,   1),
        new(6,  "Shoulders",     87,  87),
        new(7,  "Arms",         173,  44),
        new(8,  "Back",         173,  87),
        new(9,  "Left Wrist",   130,  44),
        new(10, "Right Wrist",  259,  44),
        new(11, "Range",        173, 130),
        new(12, "Hands",        216,  44),
        new(13, "Primary",       87, 130),
        new(14, "Secondary",    130, 130),
        new(15, "Left Finger",   87,  44),
        new(16, "Right Finger", 302,  44),
        new(17, "Chest",        130,  87),
        new(18, "Legs",         259,  87),
        new(19, "Feet",         302,  87),
        new(20, "Waist",        216,  87),
        new(21, "Power Source", 302, 130),
        new(22, "Ammo",         216, 130),
    };

    // ---------------------------------------------------------------- icon sheets
    //
    // Item icons live in dragitem1..379.dds. EQUI_DragItems.xml declares them as ONE grid
    // animation — Grid=true, Vertical=true, CellWidth=40, CellHeight=40 — with each 256x256
    // sheet as a frame. So a sheet holds 6x6 = 36 cells, and Vertical=true means they are
    // indexed COLUMN-major: down the first column, then the next.
    //
    // The wiki's lucy_img_ID is offset by EverQuest's classic icon base of 500. Verified against
    // the art: "Refugee Shroud" (id 664, SHOULDERS) resolves to a hooded cloak under −500 and to
    // a trident under a 1-based reading, and six unrelated "10 Dose …" potions all resolve to
    // vials. Some sheets are raw BGRA and some are DXT5; both decode to 256x256.

    public const int IconBase = 500;
    public const int IconsPerSheet = 36;
    public const int IconGridCols = 6;

    /// <summary>Sheet number (1-based), and the cell's column and row inside it, for a wiki icon id.</summary>
    public static (int Sheet, int Col, int Row) IconCell(int lucyImgId)
    {
        int idx = Math.Max(0, lucyImgId - IconBase);
        int cell = idx % IconsPerSheet;
        return (idx / IconsPerSheet + 1, cell / IconGridCols, cell % IconGridCols);
    }

    /// <summary>File name of the sheet holding a wiki icon id.</summary>
    public static string IconSheetFile(int lucyImgId) => $"dragitem{IconCell(lucyImgId).Sheet}.dds";
}
