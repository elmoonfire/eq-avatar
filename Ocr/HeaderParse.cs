using System;
using System.Collections.Generic;
using System.Linq;

namespace EQAvatar.Spike.Ocr;

/// <summary>
/// The character header of the Inventory window — name, level, and the loadout's classes.
///
/// WHERE IT IS. Not near the stats. The window's right-hand column carries, top to bottom:
/// the character name, then "50 WAR/DRU/BRD", then NEXT LEVEL / NEXT AA, the weight pair, the
/// Destroy slot and the bags. So the name and the loadout are usually TWO separate OCR lines,
/// stacked, not one line to be regexed.
///
/// WHY THIS IS ITS OWN FILE. Two bugs lived in the old inline version and both were the kind that
/// only a test catches:
///
///  1. The name fell back to "any single capitalised word in the search box". The box contains
///     the window's own labels — "Weight", "Mana", "Worn" — so the character was named after
///     whichever label the OCR happened to emit first, and it ALTERNATED between reads. Hayden saw
///     his character called Weight, then Mana.
///  2. The loadout was matched with <c>\b(\d{1,2})\s+([A-Z]{2,4}(?:/[A-Z]{2,4}){0,2})\b</c>, which
///     needs the slashes to survive OCR. At this text size they routinely do not: "WAR/DRU/BRD"
///     comes back as "WAR1DRU1BRD", "WAR|ORU|BRO", or split into separate words. One mangled
///     character and the whole line was thrown away — which is why the app still thought a level-50
///     WAR/DRU/BRD was a level-1 Warrior.
///
/// The fix for both is the same idea that fixed the stat grid: stop pattern-matching free text and
/// use what is KNOWN. There are exactly sixteen class codes, so a token only has to be closer to
/// one of those than to anything else; and a name is only a name if it is not one of the window's
/// own labels.
/// </summary>
public static class HeaderParse
{
    /// <summary>EverQuest's sixteen class codes, as the loadout header writes them.</summary>
    public static readonly string[] ClassCodes =
    {
        "WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD",
        "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER",
    };

    /// <summary>
    /// The window's own furniture. A character called "Weight" is a bug; a character genuinely
    /// called "Mana" will have to live with being read from the hub username instead.
    /// </summary>
    private static readonly HashSet<string> Furniture = new(StringComparer.OrdinalIgnoreCase)
    {
        "WEIGHT", "WORN", "COIN", "COINS", "MANA", "DESTROY", "INVENTORY", "EQUIPMENT", "PET",
        "LOADOUTS", "STORAGE", "STATS", "RESISTS", "VITALS", "CHARACTER", "ATTACK", "VELOCITY",
        "REGEN", "PRIMARY", "SECONDARY", "RANGED", "SKILLS", "ACHIEV", "APPEAR", "DONE", "NEXT",
        "LEVEL", "SPEED", "STRENGTH", "STAMINA", "INTELLIGENCE", "WISDOM", "AGILITY", "DEXTERITY",
        "CHARISMA", "MAGIC", "FIRE", "COLD", "DISEASE", "POISON", "VOID", "TOTAL", "BANK",
        "PLATINUM", "GOLD", "SILVER", "COPPER", "CURSOR", "GENERAL", "OPTIONS", "HELP",
    };

    /// <summary>
    /// Characters the OCR swaps at this text size, folded to the letter the class codes actually
    /// use. Digits are the common case: a zero for an O, a one for an I, an eight for a B.
    /// </summary>
    private static char Fold(char c) => char.ToUpperInvariant(c) switch
    {
        '0' => 'O', '1' => 'I', '5' => 'S', '8' => 'B', '6' => 'G', '2' => 'Z', '4' => 'A',
        '|' => 'I', '!' => 'I',
        var u => u,
    };

    private static string FoldAll(string s) => new(s.Where(char.IsLetterOrDigit).Select(Fold).ToArray());

    /// <summary>The same swaps read the other way, for a token that is supposed to be a NUMBER.
    /// The level is the one place "5O" has to come back as 50.</summary>
    private static char FoldDigit(char c) => char.ToUpperInvariant(c) switch
    {
        'O' => '0', 'Q' => '0', 'D' => '0', 'I' => '1', 'L' => '1', 'S' => '5',
        'B' => '8', 'G' => '6', 'Z' => '2', '|' => '1',
        var u => u,
    };

    /// <summary>
    /// Split a run-together token into class codes. "WAR/DRU/BRD" loses its slashes to the OCR and
    /// arrives as one word — usually with the separator surviving as a stray I or 1, which is why
    /// a leftover character between two codes is allowed to be skipped.
    ///
    /// The whole token must be consumed. That is what keeps this from finding codes inside words
    /// that merely contain three plausible letters.
    /// </summary>
    private static List<string>? Segment(string token, int max = 3)
    {
        string f = FoldAll(token);
        if (f.Length < 3) return null;

        var found = new List<string>();
        int i = 0;
        while (i < f.Length && found.Count < max)
        {
            if (i + 3 <= f.Length && MatchClassCode(f.Substring(i, 3)) is { } code)
            {
                found.Add(code);
                i += 3;
                continue;
            }
            // A separator that survived as a letter: only ever BETWEEN codes, never leading.
            if (found.Count > 0 && "IL/T".IndexOf(f[i]) >= 0) { i++; continue; }
            break;
        }
        return i == f.Length && found.Count > 0 ? found : null;
    }

    /// <summary>Is this token exactly one of the game's class codes? Exact, not fuzzy — the fuzzy
    /// matcher exists to rescue an OCR read, and this exists to check what has already been
    /// written down.</summary>
    public static bool IsClassCode(string? token)
        => token is not null && Array.IndexOf(ClassCodes, token.Trim().ToUpperInvariant()) >= 0;

    /// <summary>Edit distance, capped — we only ever care whether it is 0, 1, or more.</summary>
    private static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (Math.Abs(a.Length - b.Length) > 1) return 2;

        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// One token to a class code, or null. An exact fold wins outright; otherwise a single
    /// character may be wrong, and only if exactly ONE code is that close — "BRO" is BRD and
    /// nothing else, but an ambiguous token is dropped rather than guessed.
    /// </summary>
    public static string? MatchClassCode(string token)
    {
        string t = FoldAll(token);
        if (t.Length is < 2 or > 4) return null;

        foreach (string c in ClassCodes) if (t == c) return c;

        string? best = null;
        foreach (string c in ClassCodes)
        {
            if (Distance(t, c) != 1) continue;
            if (best is not null) return null;              // ambiguous — refuse to guess
            best = c;
        }
        return best;
    }

    /// <summary>
    /// "50 WAR/DRU/BRD" out of a line, however the OCR mangled the separators. Returns the level
    /// and the canonical codes joined with "/", or false.
    ///
    /// A loadout is one, two or three classes: a new character has two until level 10 and only
    /// picks a third later, so nothing here requires three.
    /// </summary>
    public static bool TryParseLoadout(string text, out int level, out string classes)
    {
        level = 0;
        classes = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Split on whitespace AND on every character that might be, or might have been, the
        // separator. "WAR1DRU1BRD" survives this because the 1s fold to I and the pieces still
        // land within one edit of their codes... but splitting first is what makes the common
        // "WAR/DRU/BRD" and "WAR DRU BRD" shapes identical to this parser.
        string[] tokens = text.Split(new[] { ' ', '\t', '/', '\\', '|', ',', '.', '-', '·' },
                                     StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            // The level, read through the digit fold so "5O" is fifty. Bounded to two characters
            // so a stat value can never be mistaken for a level.
            string raw = tokens[i];
            if (raw.Length is < 1 or > 2) continue;
            string lvlTok = new(raw.Select(FoldDigit).ToArray());
            if (!lvlTok.All(char.IsDigit)) continue;
            if (!int.TryParse(lvlTok, out int lv) || lv is < 1 or > 99) continue;

            var codes = new List<string>();
            for (int j = i + 1; j < tokens.Length && codes.Count < 3; j++)
            {
                if (MatchClassCode(tokens[j]) is { } one) { codes.Add(one); continue; }
                if (Segment(tokens[j], 3 - codes.Count) is { } many) { codes.AddRange(many); continue; }
                break;
            }
            // ALL THREE, OR THIS WASN'T THE HEADER.
            //
            // A loadout in EQ Legends is three classes; the game never shows one. So a read that
            // recovered a single code did not find a one-class loadout, it found two thirds of a
            // three-class one and lost the rest to the OCR — and accepting it wrote a phantom
            // "just a MAG" into the character's permanent loadout history, where it sat in the
            // menu next to the real ones looking exactly as authoritative. Refusing costs a scan
            // that has to be repeated; accepting costs a wrong answer that never goes away.
            if (codes.Count < 3) continue;

            level = lv;
            classes = string.Join("/", codes);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Could this line be the character's name? Names are one word of letters. The window's own
    /// labels are one word of letters too, which is exactly how "Weight" became a character name,
    /// so they are excluded by name.
    /// </summary>
    public static bool LooksLikeName(string text)
    {
        string t = text.Trim();
        if (t.Length is < 3 or > 14) return false;
        if (!t.All(char.IsLetter)) return false;
        if (!char.IsUpper(t[0])) return false;
        return !Furniture.Contains(t);
    }
}
