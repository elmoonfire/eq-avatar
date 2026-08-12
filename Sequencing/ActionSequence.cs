using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQAvatar.Spike.Sequencing;

/// <summary>
/// One pill in a sequence cell. Kind decides how the engine will execute it:
///   action    — a general keybind action (jump, target nearest NPC, open inventory…)
///   stance    — physical stance for this part (one per part)
///   invocation— invocation for this part (one per part)
///   spell     — cast this spell
///   memspell  — memorize this spell first
///   spellset  — swap the whole set: /memspellset 'name'
///   ability   — activated ability (runs after spells, e.g. Quick Buff)
///   revert    — (multi-part) restore an aspect to its pre-sequence value
/// </summary>
public sealed class SeqChip
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";

    public SeqChip() { }
    public SeqChip(string kind, string value) { Kind = kind; Value = value; }
    public SeqChip Clone() => new(Kind, Value);

    public string Label => Kind switch
    {
        "memspell" => "mem · " + Value,
        "spellset" => "set · " + Value,
        "invocation" => Value + " ⁂",
        "revert" => "revert " + Value,
        _ => Value,
    };
}

/// <summary>One part of a sequence. Most sequences have a single part; multi-part sequences
/// chain more (part 2 can revert stances/invocation/spells to their pre-sequence values).</summary>
public sealed class SeqSegment
{
    public List<SeqChip> Actions { get; set; } = new();
    public List<SeqChip> Stances { get; set; } = new();
    public List<SeqChip> Spells { get; set; } = new();
    public List<SeqChip> Abilities { get; set; } = new();

    public List<SeqChip> Cell(string col) => col switch
    {
        "action" => Actions,
        "stance" => Stances,
        "spell" => Spells,
        _ => Abilities,
    };

    public bool IsEmpty => Actions.Count == 0 && Stances.Count == 0 && Spells.Count == 0 && Abilities.Count == 0;
}

/// <summary>A saved sequence. The backend Id never changes; the DISPLAY id is purely the
/// position in the stored list (1..N) — reordering renumbers everything, and other pages
/// reference sequences by that number.</summary>
public sealed class ActionSequence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<SeqSegment> Parts { get; set; } = new() { new SeqSegment() };
    public bool Enabled { get; set; } = true;

    public SeqSegment Main
    {
        get
        {
            if (Parts.Count == 0) Parts.Add(new SeqSegment());
            return Parts[0];
        }
    }
}

/// <summary>Loads/saves the ordered sequence list at %AppData%\EQAvatar\sequences.json.</summary>
public static class SequenceStore
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar");
    private static string FilePath => Path.Combine(Dir, "sequences.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<ActionSequence> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var list = JsonSerializer.Deserialize<List<ActionSequence>>(File.ReadAllText(FilePath)) ?? new();
            foreach (var s in list) if (s.Parts.Count == 0) s.Parts.Add(new SeqSegment());
            return list;
        }
        catch { return new(); }
    }

    public static void Save(List<ActionSequence> list)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Opts));
        }
        catch { /* never let persistence kill the UI */ }
    }
}

/// <summary>
/// The option lists behind every filter popup. Seeded with sensible defaults and extended by
/// whatever the user types (Enter in a popup adds it) — later the Key Mappings page and the
/// spellbook OCR will pour the real in-game lists in here. Persisted next to the sequences.
/// </summary>
public sealed class SeqCatalog
{
    public List<string> Actions { get; set; } = new();
    public List<string> Stances { get; set; } = new();
    public List<string> Invocations { get; set; } = new();
    public List<string> Spells { get; set; } = new();
    public List<string> SpellSets { get; set; } = new();
    public List<string> Abilities { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "seq-catalog.json");

    private static readonly string[] SeedActions =
    {
        "Jump", "Sit", "Stand", "Duck", "Target nearest NPC", "Target self",
        "Open inventory", "Auto-attack on", "Auto-attack off", "Interact / use",
    };
    private static readonly string[] SeedStances = { "Aggressive", "Defensive", "Balanced", "Evasive" };
    private static readonly string[] SeedAbilities = { "Quick Buff", "Kick", "Taunt", "Hide", "Sneak", "Forage" };

    public static SeqCatalog Load()
    {
        SeqCatalog cat;
        try
        {
            cat = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SeqCatalog>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch { cat = new(); }
        MergeSeeds(cat.Actions, SeedActions);
        MergeSeeds(cat.Stances, SeedStances);
        MergeSeeds(cat.Abilities, SeedAbilities);
        return cat;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<string> ListFor(string col, string spellKind = "spell") => col switch
    {
        "action" => Actions,
        "stance" => Stances,          // (invocations listed separately by the popup)
        "spell" => spellKind == "spellset" ? SpellSets : Spells,
        _ => Abilities,
    };

    /// <summary>Add a user-typed option to the right list (case-insensitive dedupe). True if new.</summary>
    public bool Remember(List<string> list, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return false;
        if (list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) return false;
        list.Add(value);
        Save();
        return true;
    }

    private static void MergeSeeds(List<string> list, string[] seeds)
    {
        foreach (string s in seeds)
            if (!list.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
                list.Add(s);
    }
}
