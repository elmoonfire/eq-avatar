using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQAvatar.Spike.Config;

/// <summary>
/// One EverQuest Legends loadout: a race plus a primary / secondary / tertiary class, with its
/// own level and its own inventory. The client calls these Personas — the Loadouts tab carries
/// <c>PersonaInvSlot0…22</c>, a full 23-slot equipment set per loadout, and a swaps-remaining
/// counter for the per-account limit.
/// </summary>
public sealed class Loadout
{
    /// <summary>Classes as the game writes them in the header, e.g. ["PAL","MNK","ENC"].</summary>
    public List<string> Classes { get; set; } = new();
    public int Level { get; set; }
    public string? Race { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>Stable identity for a loadout: its class combination, order-sensitive because
    /// which class is primary is what the loadout IS. PAL/MNK/ENC and MNK/PAL/ENC are two
    /// different loadouts with different abilities, not one loadout listed two ways.</summary>
    public string Key => string.Join("/", Classes).ToUpperInvariant();

    public string Primary => Classes.Count > 0 ? Classes[0] : "";
    public string Display => Classes.Count > 0 ? string.Join("/", Classes) : "no classes";

    /// <summary>
    /// A loadout is only real if it names three classes the game actually has.
    ///
    /// The OCR header read is the only thing that ever creates one, and a read that lost two of
    /// the three codes used to be accepted as a one-class loadout — which then sat in the menu
    /// alongside the real ones, permanently, looking exactly as authoritative. Three valid codes
    /// is the whole test, and everything else is a scan to repeat rather than a fact to keep.
    /// </summary>
    public bool Plausible => Classes.Count == 3 && Classes.TrueForAll(Ocr.HeaderParse.IsClassCode);
}

/// <summary>
/// Every loadout this character has been seen wearing, newest first, persisted next to the app's
/// other settings. The one at the front is what the last inventory read showed; the rest are the
/// history the title-bar menu offers. Swapping loadouts in game and reading again simply moves
/// the new one to the front — nothing is ever discarded, so a loadout that has not been worn for
/// a month still remembers its level.
/// </summary>
public sealed class LoadoutStore
{
    public List<Loadout> Loadouts { get; set; } = new();

    /// <summary>The character as the game names it, not as anyone typed it into a settings box.
    /// These come off the inventory read and are what the title-bar bio is built from.</summary>
    public string? Name { get; set; }
    public string? Server { get; set; }
    public string? Race { get; set; }
    public DateTime? LastScan { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EQAvatar", "loadouts.json");

    public Loadout? Current => Loadouts.Count > 0 ? Loadouts[0] : null;
    public IEnumerable<Loadout> Previous => Loadouts.Skip(1);

    /// <summary>
    /// The level of the CURRENT loadout. In EQ Legends a loadout's level is the level of its
    /// weakest class — swapping in a fresh class drops the whole loadout to that class's level —
    /// so this is the "effective level" the game itself plays you at.
    /// </summary>
    public int EffectiveLevel => Current?.Level ?? 0;

    /// <summary>The best level on the account: the highest any recorded loadout has reached.
    /// It only ever climbs, because levelling a new class never un-levels the old loadout.</summary>
    public int BestLevel => Loadouts.Count == 0 ? 0 : Loadouts.Max(l => l.Level);

    /// <summary>
    /// A class's own level, where you have told us. Empty until then — and that is the honest
    /// state, because nothing on the screen the app reads carries per-class levels; the header
    /// shows the loadout's EFFECTIVE level, which is the lowest of its three.
    /// </summary>
    public Dictionary<string, int> ClassLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lowest level this class can possibly be, deduced from every loadout it appears in.
    ///
    /// This is real information and it is free. In EQ Legends a loadout plays at the level of its
    /// WEAKEST class, so seeing PAL/CLR/SHM at 45 proves all three of those are at least 45 —
    /// there is no way to be in that loadout at 45 with a class below it. Take the highest such
    /// floor across every loadout a class belongs to and you have a lower bound that only ever
    /// tightens as more loadouts are seen.
    /// </summary>
    public int Floor(string cls)
    {
        int best = 0;
        foreach (Loadout l in Loadouts)
            if (l.Level > best && l.Classes.Any(c => string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)))
                best = l.Level;
        return best;
    }

    /// <summary>
    /// This class's level, and whether it is known or merely bounded.
    ///
    /// Exact three ways. You told us; or the class's floor is above this loadout's level, so it is
    /// NOT the one holding the loadout down and only its floor is known; or — the deduction worth
    /// having — every OTHER class in the loadout has a floor strictly above the loadout's level,
    /// which leaves this one as the only possible cause of that level, so it is exactly that.
    /// </summary>
    public (int Level, bool Exact) LevelOf(Loadout l, string cls)
    {
        int floor = Floor(cls);
        // A TYPED LEVEL STILL CANNOT BE BELOW WHAT THE LOADOUTS PROVE. Being in a loadout that
        // played at 45 is proof a class is at least 45 — no typo can make that untrue — and showing
        // "WAR 10" above a footer reading "plays at Lv 30" would be presenting an impossibility as
        // a fact. The floor wins, quietly.
        // …and a corrected value is no longer EXACT. Raising a contradicted typo to the floor gives
        // the right number, but calling it exact would print a bound as though it were a
        // measurement. "At least 30, and what you typed cannot be right" is the honest reading.
        if (ClassLevels.TryGetValue(cls, out int told) && told > 0)
            return told >= floor ? (told, true) : (floor, false);
        if (l.Level <= 0) return (floor, false);
        bool onlyCandidate = l.Classes
            .Where(c => !string.Equals(c, cls, StringComparison.OrdinalIgnoreCase))
            .All(c => Floor(c) > l.Level);
        return onlyCandidate && floor <= l.Level ? (l.Level, true) : (floor, false);
    }

    /// <summary>What this loadout plays at: the lowest level among its three classes. Falls back
    /// to what the header read when nothing better is known.</summary>
    public int PlayedAt(Loadout l)
    {
        int lo = 0;
        foreach (string c in l.Classes)
        {
            (int lv, bool exact) = LevelOf(l, c);
            if (!exact || lv <= 0) return l.Level;      // one unknown and the minimum is unknowable
            if (lo == 0 || lv < lo) lo = lv;
        }
        return lo > 0 ? lo : l.Level;
    }

    public static LoadoutStore Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                LoadoutStore st = JsonSerializer.Deserialize<LoadoutStore>(File.ReadAllText(Path)) ?? new LoadoutStore();
                st.Loadouts ??= new List<Loadout>();
                st.ClassLevels ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // SWEPT ON THE WAY IN. Earlier builds wrote a whole loadout from a header read that
                // recovered only one of its three class codes, so there are files out there with a
                // phantom "MAG" sitting in the menu beside the real loadouts. The parser can't
                // produce those any more; this is what clears the ones already written down.
                List<Loadout> junk = st.Loadouts.Where(l => l is null || !l.Plausible).ToList();
                if (junk.Count > 0)
                {
                    // SAID, because this deletes something a person can see. It is the right
                    // deletion — these are OCR misreads, not loadouts anyone has ever worn — but a
                    // silent one would be indistinguishable from losing real history, and the whole
                    // rule rests on "a loadout is always three classes", which nothing verifies.
                    try
                    {
                        Diag.BotLog.Log("app", "dropped " + junk.Count + " impossible loadout(s) from the history — "
                                             + string.Join(", ", junk.Select(l => l?.Display ?? "null"))
                                             + ". A loadout is three classes; these came from inventory reads that "
                                             + "recovered only part of the header.");
                    }
                    catch { /* never let logging stop the app starting */ }
                    st.Loadouts.RemoveAll(l => l is null || !l.Plausible);
                    st.Save();
                }
                return st;
            }
        }
        catch { /* a corrupt file must never stop the app starting */ }
        return new LoadoutStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Record what the game just showed us. Returns true when this is a different loadout than
    /// the one that was in front, which is the moment the title bar's contents move into the
    /// menu. An existing loadout is updated in place and promoted rather than duplicated, so
    /// levelling one up does not fork it into two entries.
    /// </summary>
    /// <summary>
    /// Does this loadout's class list contain the read codes, in order but not necessarily
    /// adjacent?
    ///
    /// A SUBSEQUENCE, deliberately. The middle code is the one with a separator on both sides and
    /// therefore the likeliest to be lost, so a read of PAL/CLR/SHM that dropped CLR gives
    /// [PAL, SHM] — in order, not contiguous. Requiring adjacency threw away the commonest partial
    /// read there is. What keeps this safe is not adjacency, it is the caller's insistence on
    /// exactly ONE matching loadout: if two could fit, nothing is written.
    /// </summary>
    private static bool Contains(List<string> whole, List<string> part)
    {
        if (part.Count == 0 || part.Count > whole.Count) return false;
        int k = 0;
        foreach (string w in whole)
            if (k < part.Count && string.Equals(w, part[k], StringComparison.OrdinalIgnoreCase)) k++;
        return k == part.Count;
    }

    public bool Record(string? classes, int? level, string? race,
                       string? name = null, string? server = null)
    {
        // Scanned values win over anything configured, but a scan that came back blank must not
        // wipe what an earlier scan found: only non-empty answers are written.
        if (!string.IsNullOrWhiteSpace(name))   Name   = name!.Trim();
        if (!string.IsNullOrWhiteSpace(server)) Server = server!.Trim();
        if (!string.IsNullOrWhiteSpace(race))   Race   = race!.Trim();
        LastScan = DateTime.Now;

        if (string.IsNullOrWhiteSpace(classes)) return false;
        List<string> parts = classes.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(p => p.Trim().ToUpperInvariant())
                                    .Where(p => p.Length is >= 2 and <= 4)
                                    .ToList();
        if (parts.Count == 0) return false;

        // A PARTIAL READ UPDATES, IT DOES NOT CREATE. Three valid codes is a loadout; anything
        // less is two thirds of one that the OCR lost, and if exactly one loadout on record
        // contains those codes in that order then that is plainly the one being worn — so take
        // the level and leave the history alone. If it matches none, or several, say nothing.
        if (parts.Count != 3 || !parts.TrueForAll(Ocr.HeaderParse.IsClassCode))
        {
            List<Loadout> fits = Loadouts.Where(l => Contains(l.Classes, parts)).ToList();
            if (fits.Count == 1 && level is int part && part > 0)
            {
                fits[0].Level = part;
                fits[0].LastSeen = DateTime.Now;
            }
            // SAVED EITHER WAY. The name, server and race assigned at the top of this method are
            // real findings even when the class codes came back unusable, and returning without a
            // save left them in memory only — gone on the next launch, for a scan that had in fact
            // read them correctly.
            Save();
            return false;
        }

        string key = string.Join("/", parts);
        bool changed = Current is null || !string.Equals(Current.Key, key, StringComparison.OrdinalIgnoreCase);

        Loadout? existing = Loadouts.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new Loadout { Classes = parts };
            Loadouts.Add(existing);
        }
        if (level is int lv && lv > 0) existing.Level = lv;
        if (!string.IsNullOrWhiteSpace(race)) existing.Race = race!.Trim();
        existing.LastSeen = DateTime.Now;

        Loadouts.Remove(existing);
        Loadouts.Insert(0, existing);       // most recently read is always the current one
        Save();
        return changed;
    }
}
