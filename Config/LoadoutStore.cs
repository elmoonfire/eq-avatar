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

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EQAvatar", "loadouts.json");

    public Loadout? Current => Loadouts.Count > 0 ? Loadouts[0] : null;
    public IEnumerable<Loadout> Previous => Loadouts.Skip(1);

    public static LoadoutStore Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<LoadoutStore>(File.ReadAllText(Path)) ?? new LoadoutStore();
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
    public bool Record(string? classes, int? level, string? race)
    {
        if (string.IsNullOrWhiteSpace(classes)) return false;
        List<string> parts = classes.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(p => p.Trim().ToUpperInvariant())
                                    .Where(p => p.Length is >= 2 and <= 4)
                                    .ToList();
        if (parts.Count == 0) return false;

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
