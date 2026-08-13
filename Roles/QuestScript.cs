using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQAvatar.Spike.Data;

namespace EQAvatar.Spike.Roles;

/// <summary>
/// Where on screen one step of a turn-in happens, stored NORMALIZED to the game window
/// (0–1 on each axis) so it survives the window being moved or resized — the same convention
/// <see cref="Ocr.VitalsReader"/> uses for the HP bar and target window.
/// </summary>
public sealed class ScreenPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    [JsonIgnore] public bool Set => X > 0 && Y > 0 && X < 1 && Y < 1;
}

/// <summary>
/// The four gestures a hand-in is made of. EQ Legends has no addon API and the log says nothing
/// until AFTER an item has been offered, so where to click cannot be discovered — it is picked
/// once, by hand, off a real frame of the game, exactly like the HP bar and the target window.
///
/// Deliberately points and not templates: an inventory slot's contents change every time an item
/// is consumed, so matching a picture of the slot would fail on the second turn-in. The position
/// of the slot does not change.
/// </summary>
public sealed class TurnInLayout
{
    /// <summary>The inventory slot the hand-in item sits in.</summary>
    public ScreenPoint ItemSlot { get; set; } = new();
    /// <summary>The NPC's body — where the held item gets dropped to open the give window.</summary>
    public ScreenPoint Npc { get; set; } = new();
    /// <summary>The give window's GIVE button.</summary>
    public ScreenPoint GiveButton { get; set; } = new();
    /// <summary>Optional: a confirmation button, if the server puts one up.</summary>
    public ScreenPoint Confirm { get; set; } = new();

    [JsonIgnore] public bool Ready => ItemSlot.Set && Npc.Set && GiveButton.Set;

    public string Missing()
    {
        var gaps = new List<string>();
        if (!ItemSlot.Set) gaps.Add("the item's inventory slot");
        if (!Npc.Set) gaps.Add("the NPC");
        if (!GiveButton.Set) gaps.Add("the GIVE button");
        return gaps.Count == 0 ? "" : string.Join(", ", gaps);
    }
}

/// <summary>
/// One quest's automation: which NPC to hand what to, where the clicks land, and how many times
/// to go round. A script is only ever built for a quest the catalog says has a turn-in — the
/// hand-in is the one part of a quest that is a fixed, repeatable gesture, and it is where the
/// tedium actually is when a quest is farmed.
/// </summary>
public sealed class QuestScript
{
    public string Quest { get; set; } = "";
    public string Npc { get; set; } = "";
    public string Item { get; set; } = "";
    /// <summary>How many of the item go in one hand-in (the wiki's turn-in quantity).</summary>
    public int PerTurnIn { get; set; } = 1;
    /// <summary>0 = keep going until the item runs out (i.e. until hand-ins stop confirming).</summary>
    public int Repeat { get; set; }
    /// <summary>Say "Hail, NPC" before the first hand-in of each loop. Some NPCs need waking up.</summary>
    public bool HailFirst { get; set; } = true;
    /// <summary>Issue /target NPC before hailing, rather than trusting the current target.</summary>
    public bool TargetByName { get; set; } = true;
    /// <summary>Extra phrases to say between the hail and the hand-in (quest dialogue triggers,
    /// e.g. the bracketed words an NPC asks you to repeat back).</summary>
    public List<string> SayPhrases { get; set; } = new();
    public TurnInLayout Layout { get; set; } = new();
    /// <summary>Seconds to wait for the log to confirm a hand-in before calling it a miss.</summary>
    public int ConfirmSeconds { get; set; } = 12;
    public DateTime? LastRun { get; set; }
    public int LifetimeCompleted { get; set; }

    public static QuestScript FromQuest(QuestInfo q)
    {
        QuestTurnIn? t = q.TurnIns.FirstOrDefault();
        return new QuestScript
        {
            Quest = q.Name,
            Npc = string.IsNullOrWhiteSpace(t?.Npc) ? q.EndNpc : t!.Npc,
            Item = t?.Item ?? "",
            PerTurnIn = Math.Max(1, t?.Qty ?? 1),
            SayPhrases = new List<string>(),
        };
    }
}

/// <summary>Every quest script this install has, at %AppData%\EQAvatar\questscripts.json.</summary>
public sealed class QuestScriptStore
{
    public List<QuestScript> Scripts { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "questscripts.json");

    private static QuestScriptStore? _current;
    public static QuestScriptStore Current => _current ??= Load();

    /// <summary>The runner finishes on a background thread and writes its lifetime counter there,
    /// while the UI thread can be adding a script for a row the user just expanded. Serializing a
    /// List while another thread appends to it throws, and the throw would be swallowed — so every
    /// mutation and every save goes through this lock.</summary>
    private readonly object _gate = new();

    public static QuestScriptStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<QuestScriptStore>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public QuestScript? Find(string quest)
    {
        lock (_gate)
            return Scripts.FirstOrDefault(s => QuestCatalog.Norm(s.Quest) == QuestCatalog.Norm(quest));
    }

    public int Count { get { lock (_gate) return Scripts.Count; } }

    /// <summary>Adopt a script the UI has been editing in memory. Called the first time the user
    /// actually changes something — NOT when a row is merely expanded, so browsing the catalog
    /// doesn't leave a trail of empty "built" automations behind it.</summary>
    public void Adopt(QuestScript script)
    {
        lock (_gate)
        {
            if (!Scripts.Any(s => ReferenceEquals(s, script))
                && !Scripts.Any(s => QuestCatalog.Norm(s.Quest) == QuestCatalog.Norm(script.Quest)))
                Scripts.Add(script);
        }
        Save();
    }

    public void Remove(string quest)
    {
        lock (_gate) Scripts.RemoveAll(s => QuestCatalog.Norm(s.Quest) == QuestCatalog.Norm(quest));
        Save();
    }
}
