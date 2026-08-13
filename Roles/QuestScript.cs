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
/// One item handed over: the bag slot it is picked up from, and which quest that hand-in belongs
/// to (so the log line that confirms it can be matched by name).
///
/// A cycle is a LIST of these because that is what farming a quest chain actually looks like:
/// on Kerra Isle the Desecrated Kejaar Totem finishes "Something is Wrrrong", which immediately
/// assigns "This Means Warrr", whose Heretic Insurrection Orders go to the SAME NPC and re-open
/// the first quest. One hand-in per script would have meant babysitting every other hand-over.
/// </summary>
public sealed class TurnInStep
{
    public string Item { get; set; } = "";
    public int Qty { get; set; } = 1;
    /// <summary>The quest this hand-in advances — used to match the confirming log line.</summary>
    public string Quest { get; set; } = "";
    /// <summary>The inventory slot this item sat in WHEN PICKED — the fallback when the icon
    /// can't be found. The item itself migrates through the bag as copies are consumed.</summary>
    public ScreenPoint Slot { get; set; } = new();
    /// <summary>6×6 colour signature of the item's icon, learned from the pick frame. This is what
    /// lets the runner find the NEXT copy after this slot empties: an icon is a fixed sprite the UI
    /// stamps into whatever slot holds the item — the one screen element that never changes.</summary>
    public double[]? IconSig { get; set; }

    [JsonIgnore] public bool HasIcon => IconSig is { Length: 108 };

    public TurnInStep Clone() => new()
    { Item = Item, Qty = Qty, Quest = Quest, Slot = new ScreenPoint { X = Slot.X, Y = Slot.Y }, IconSig = IconSig?.ToArray() };
}

/// <summary>
/// The parts of the gesture that are the same for every item in a cycle: the NPC you drop onto,
/// the give window's GIVE button, and an optional confirmation.
///
/// Deliberately points and not templates: an inventory slot's contents change every time an item
/// is consumed, so matching a picture of the slot would fail on the second turn-in. The position
/// of the slot does not change.
/// </summary>
public sealed class TurnInLayout
{
    /// <summary>The NPC's body — where the held item gets dropped to open the give window.</summary>
    public ScreenPoint Npc { get; set; } = new();
    /// <summary>The give window's GIVE button.</summary>
    public ScreenPoint GiveButton { get; set; } = new();
    /// <summary>Optional: a confirmation button, if the server puts one up.</summary>
    public ScreenPoint Confirm { get; set; } = new();

    /// <summary>Legacy (0.10.2): a single item slot, before cycles could hold several items.
    /// Read on load and migrated into the first step; never written again.</summary>
    public ScreenPoint? ItemSlot { get; set; }

    [JsonIgnore] public bool Ready => Npc.Set && GiveButton.Set;
}

/// <summary>
/// One quest chain's automation: which NPC, which items in which order, where the clicks land,
/// and how many times round.
/// </summary>
public sealed class QuestScript
{
    /// <summary>The quest this script was built from — the row it appears under.</summary>
    public string Quest { get; set; } = "";
    public string Npc { get; set; } = "";
    /// <summary>The hand-ins, in the order they are given. One cycle = all of them, once.</summary>
    public List<TurnInStep> Steps { get; set; } = new();
    /// <summary>0 = keep going until the items run out (i.e. until hand-ins stop confirming).</summary>
    public int Repeat { get; set; }
    /// <summary>Hail at the start of each cycle. Some NPCs need waking up — and on Kerra Isle the
    /// hail is what re-assigns the task after a full cycle.</summary>
    public bool HailFirst { get; set; } = true;
    /// <summary>The in-game hail key, pressed with the NPC targeted. EQL binds "h" by default,
    /// which is one keystroke instead of typing "Hail, The Kerran Sha`rr" a thousand times.</summary>
    public string HailKey { get; set; } = "h";
    /// <summary>Issue /target NPC before hailing, rather than trusting the current target.</summary>
    public bool TargetByName { get; set; } = true;
    /// <summary>Extra phrases to say between the hail and the first hand-in (quest dialogue
    /// triggers, e.g. the bracketed words an NPC asks you to repeat back).</summary>
    public List<string> SayPhrases { get; set; } = new();
    public TurnInLayout Layout { get; set; } = new();
    /// <summary>Seconds to wait for the log to confirm a hand-in before calling it a miss.</summary>
    public int ConfirmSeconds { get; set; } = 12;

    // ---- smart find (0.10.8): the picks that MOVE get found, not remembered ----
    /// <summary>Find items by icon and the NPC by nameplate, falling back to the fixed picks.</summary>
    public bool SmartFind { get; set; } = true;
    /// <summary>The bag area the items live in (normalized rect + grid), scanned for icons.</summary>
    public double BagX { get; set; }
    public double BagY { get; set; }
    public double BagW { get; set; }
    public double BagH { get; set; }
    public int BagCols { get; set; } = 2;
    public int BagRows { get; set; } = 5;
    [JsonIgnore] public bool BagSet => BagW > 0.01 && BagH > 0.005 && BagCols > 0 && BagRows > 0;
    /// <summary>Where the NPC's nameplate was when the body point was picked, and the vector from
    /// that nameplate to the picked body point. At run time: find the nameplate, add the vector.</summary>
    public double NpcNameX { get; set; }
    public double NpcNameY { get; set; }
    public double NpcDx { get; set; }
    public double NpcDy { get; set; }
    public bool NpcAnchorLearned { get; set; }
    public DateTime? LastRun { get; set; }
    public int LifetimeCompleted { get; set; }

    // ---- legacy 0.10.2 fields, read once on load then folded into Steps ----
    public string? Item { get; set; }
    public int? PerTurnIn { get; set; }

    [JsonIgnore] public bool Ready => Layout.Ready && Steps.Count > 0 && Steps.All(s => s.Slot.Set);

    public string Missing()
    {
        var gaps = new List<string>();
        if (!Layout.Npc.Set) gaps.Add("the NPC");
        if (!Layout.GiveButton.Set) gaps.Add("the GIVE button");
        if (Steps.Count == 0) gaps.Add("at least one item to hand in");
        foreach (TurnInStep s in Steps)
            if (!s.Slot.Set) gaps.Add($"the bag slot for {(s.Item.Length > 0 ? s.Item : "an item")}");
        return gaps.Count == 0 ? "" : string.Join(", ", gaps);
    }

    /// <summary>Fold the 0.10.2 single-item shape into the cycle shape. Idempotent, and defensive
    /// about every field, because this runs over a file a user could have hand-edited.</summary>
    public void Migrate()
    {
        Quest ??= "";
        Npc ??= "";
        SayPhrases ??= new List<string>();
        Layout ??= new TurnInLayout();
        Layout.Npc ??= new ScreenPoint();
        Layout.GiveButton ??= new ScreenPoint();
        Layout.Confirm ??= new ScreenPoint();
        Steps ??= new List<TurnInStep>();
        Steps.RemoveAll(s => s is null);

        // Fold on EITHER signal. A 0.10.2 script could carry a picked slot with a blank item name
        // (the catalog stores "" when the wiki didn't name the hand-in) and it ran perfectly well;
        // requiring the name would silently throw that pick away.
        bool hadLegacy = !string.IsNullOrWhiteSpace(Item) || Layout.ItemSlot?.Set == true;
        if (Steps.Count == 0 && hadLegacy)
        {
            Steps.Add(new TurnInStep
            {
                Item = Item ?? "",
                Qty = Math.Max(1, PerTurnIn ?? 1),
                Quest = Quest,
                Slot = Layout.ItemSlot ?? new ScreenPoint(),
            });
        }
        Item = null;
        PerTurnIn = null;
        Layout.ItemSlot = null;
        foreach (TurnInStep s in Steps) { s.Item ??= ""; s.Quest ??= ""; s.Slot ??= new ScreenPoint(); }
        if (string.IsNullOrWhiteSpace(HailKey)) HailKey = "h";
        if (BagCols <= 0) BagCols = 2;
        if (BagRows <= 0) BagRows = 5;
    }

    public static QuestScript FromQuest(QuestInfo q)
    {
        var script = new QuestScript
        {
            Quest = q.Name,
            Npc = string.IsNullOrWhiteSpace(q.TurnIns.FirstOrDefault()?.Npc) ? q.EndNpc : q.TurnIns[0].Npc,
        };
        foreach (QuestTurnIn t in q.TurnIns)
            script.Steps.Add(new TurnInStep { Item = t.Item, Qty = Math.Max(1, t.Qty), Quest = q.Name });
        script.SayPhrases = new List<string>(q.SayPhrases);
        return script;
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

    /// <summary>Nulls are OMITTED, not written. The migrated-away fields (Item, PerTurnIn,
    /// Layout.ItemSlot) are nullable so they can be READ from a 0.10.2 file — writing them back as
    /// explicit nulls would hand 0.10.2 a `"PerTurnIn": null` for its non-nullable int, which it
    /// throws on and then swallows, wiping every script the user had built. Rolling a release back
    /// is a thing this project does, so it has to survive it.</summary>
    private static readonly JsonSerializerOptions SaveOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static QuestScriptStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var store = JsonSerializer.Deserialize<QuestScriptStore>(File.ReadAllText(FilePath));
                if (store is not null)
                {
                    store.Scripts ??= new List<QuestScript>();
                    store.Scripts.RemoveAll(s => s is null);
                    // Per-script, so one unreadable entry costs that entry and not the whole file.
                    // The outer catch returns an EMPTY store, and the next Save() would overwrite
                    // everything the user had built with it.
                    var bad = new List<QuestScript>();
                    foreach (QuestScript s in store.Scripts)
                    {
                        try { s.Migrate(); }
                        catch { bad.Add(s); }
                    }
                    foreach (QuestScript s in bad) store.Scripts.Remove(s);
                    return store;
                }
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this, SaveOpts);
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

    /// <summary>How many scripts could actually run right now — the number worth showing, since a
    /// script with a pick still missing is a half-built one, not a built one.</summary>
    public int ReadyCount { get { lock (_gate) return Scripts.Count(s => s.Ready); } }

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

    /// <summary>Run a mutation of a script under the store's lock, then persist. The runner thread
    /// serializes the whole store when it finishes, so a Steps list edited from the UI thread at
    /// that moment throws inside Serialize — and that throw is swallowed, so the save just silently
    /// doesn't happen.</summary>
    public void Edit(Action change)
    {
        lock (_gate) change();
        Save();
    }

    public void Remove(string quest)
    {
        lock (_gate) Scripts.RemoveAll(s => QuestCatalog.Norm(s.Quest) == QuestCatalog.Norm(quest));
        Save();
    }
}
