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
    /// <summary>The dragged icon box's size (normalized) — the sliding search compares windows of
    /// EXACTLY this size, so a match is same-region-to-same-region and scores razor-sharp.</summary>
    public double IconW { get; set; }
    public double IconH { get; set; }
    /// <summary>
    /// The icon's ACTUAL PIXELS, at the size the game draws them.
    ///
    /// This is what Auto Merge learned the hard way and the Quest Runner spent four field tests
    /// without. The 6×6 colour signature above throws away almost all of the picture and then
    /// measures what's left with a ruler that puts a DIFFERENT icon in the same palette closer to
    /// the reference than the RIGHT icon three pixels out of alignment — so no threshold can keep
    /// one and drop the other. Hayden's log is that arithmetic in the field: his Orders matched at
    /// 33 against a bar of 35 on a good pass and 40 on a bad one, while a Bone-clasped Girdle
    /// scored 35, all three inside the same few points.
    ///
    /// Normalized cross-correlation over these pixels, with an alignment search, separates them by
    /// half the scale: the right icon scores over 0.97 even when it's brighter or highlighted, and
    /// a different icon in the same colours scores about 0.44.
    /// </summary>
    public QuestFind.IconPatch? IconPixels { get; set; }

    /// <summary>The picture the signature was learned from, so you can SEE what she matches
    /// against instead of trusting 108 numbers in a file.</summary>
    public PickShot? Shot { get; set; }

    [JsonIgnore] public bool HasIcon => IconSig is { Length: 108 };
    [JsonIgnore] public bool HasIconSize => IconW > 0.002 && IconH > 0.002;
    [JsonIgnore] public bool HasPixels => IconPixels is { Ok: true };

    public TurnInStep Clone() => new()
    { Item = Item, Qty = Qty, Quest = Quest, Slot = new ScreenPoint { X = Slot.X, Y = Slot.Y },
      IconSig = IconSig?.ToArray(), IconW = IconW, IconH = IconH, Shot = Shot,
      IconPixels = IconPixels };
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
    /// <summary>
    /// Hail at the start of each cycle. OFF by default since 0.10.20.
    ///
    /// The hail was assumed to be what assigns the task. It isn't — the SAY-PHRASE is. Hayden
    /// tested it directly: walk up to the Sha`rr with no prior interaction at all, say "explorrre
    /// the island", and the task is assigned; the totem and then the orders are both accepted.
    /// The hail's real job is to make the NPC TELL you the bracketed words in the first place, and
    /// once you know them it is two seconds of ceremony per cycle for nothing.
    /// </summary>
    public bool HailFirst { get; set; }
    /// <summary>The in-game hail key, pressed with the NPC targeted. EQL binds "h" by default,
    /// which is one keystroke instead of typing "Hail, The Kerran Sha`rr" a thousand times.</summary>
    public string HailKey { get; set; } = "h";
    /// <summary>
    /// Issue /target NPC before the cycle. OFF by default since 0.10.20: a say-phrase is spoken
    /// aloud to everyone in range and needs no target, and the hail it used to serve is gone.
    ///
    /// The trade is worth knowing rather than discovering: the NPC's nameplate is only drawn when
    /// he is TARGETED, so with this off the nameplate anchor can never fire and the fixed NPC pick
    /// carries every click. The runner says so at the start of a run instead of quietly degrading.
    /// </summary>
    public bool TargetByName { get; set; }
    /// <summary>The key (chord allowed, e.g. "alt+b") bound in game to OPEN ALL BAGS. Pressed once
    /// at the start of a run and again whenever an item scan finds nothing, because "the bags are
    /// shut" and "I'm out of totems" look identical to a screen reader — and only one of them is
    /// worth stopping for. Blank = never pressed, which is the safe default for anyone who hasn't
    /// bound the command: a TOGGLE pressed on a hunch would close the bags it meant to open.</summary>
    public string OpenBagsKey { get; set; } = "";
    /// <summary>Snapshots of the non-item picks, keyed "npc"/"give"/"confirm"/"bag" — the same
    /// "show me what you actually learned" answer for the spots that aren't matched by icon.</summary>
    public Dictionary<string, PickShot> Shots { get; set; } = new();
    /// <summary>Extra phrases to say between the hail and the first hand-in (quest dialogue
    /// triggers, e.g. the bracketed words an NPC asks you to repeat back).</summary>
    public List<string> SayPhrases { get; set; } = new();
    public TurnInLayout Layout { get; set; } = new();
    /// <summary>Seconds to wait for the log to confirm a hand-in before calling it a miss.</summary>
    public int ConfirmSeconds { get; set; } = 6;

    /// <summary>
    /// Wait for the server to acknowledge each hand-in, or hand over and move straight on.
    ///
    /// Waiting is what makes a hand-in COUNTED rather than assumed, and it is the only reason the
    /// run can ever say "you've run out" instead of clicking at an empty bag all night. But the
    /// wait is also the whole cost of a cycle: on Hayden's Kerra loop the clicking takes about five
    /// seconds and a confirmation that doesn't land costs twice that again, per item.
    ///
    /// So it is a switch, not a policy. Off, the runner offers, waits a beat, counts it and goes —
    /// which is right when you are watching, and wrong when you are not, because a run that assumes
    /// success can never notice it has stopped succeeding.
    /// </summary>
    public bool WaitForConfirm { get; set; } = true;

    /// <summary>
    /// Extra lines that count as "that hand-in worked", one per line, matched anywhere in a log
    /// line and case-insensitively.
    ///
    /// The definitive acknowledgement is "You offered 1 &lt;item&gt; to &lt;npc&gt;", and when it
    /// arrives nothing else is needed. But EQ Legends prints a quest-specific consequence right
    /// after it — "You validated the Kerran Sha`rr's concerns…", "You've dealt a blow to the
    /// Heretics…" — and those are per turn-in, unambiguous, and impossible to know in advance from
    /// a wiki scrape. So there is a box for them: paste the line your own quest prints and the
    /// runner stops waiting the moment it sees it.
    /// </summary>
    public List<string> SuccessLines { get; set; } = new();

    /// <summary>
    /// How close an icon has to look before the sweep believes it — lower is stricter.
    ///
    /// 35 was one number for every item, and Hayden's log shows why that can't hold: his real
    /// totem scores 13–20, his Orders score 26–33, and a Bone-clasped Girdle sitting somewhere
    /// else entirely scores 35. One threshold cannot be loose enough for the second item and tight
    /// enough to reject the third. Per script, and on the card, so a run that starts grabbing the
    /// wrong thing has a dial rather than a rebuild.
    /// </summary>
    public double IconTolerance { get; set; } = 35;
    /// <summary>How long to wait after the hail for the server to say the task has been assigned,
    /// before offering the first item. The hail is what re-assigns the task each cycle, and an
    /// offer that beats the assignment is refused — which cost one wasted offer and one full
    /// confirm timeout per cycle until this existed.</summary>
    public int AssignWaitSeconds { get; set; } = 3;

    /// <summary>
    /// Milliseconds to wait after dropping the item on the NPC, before pressing GIVE.
    ///
    /// This is the gap the give window has to appear in. It used to be a hard-coded 620 ms, and
    /// every field log shows the same shape: the FIRST offer of an item goes unanswered and the
    /// retry works — at identical, or even faster, click speed. The one thing that differs between
    /// them is that by the retry the trade window is already open, so GIVE lands on a real button
    /// instead of on nothing. Slower is nearly free (a second per hand-in) and a missed hand-in
    /// costs twelve, so the default errs long. Tune it on the card if your machine is quicker.
    /// </summary>
    public int GiveSettleMs { get; set; } = 1100;
    /// <summary>Set once the script has been moved to the say-only flow, so a user who turns the
    /// hail back on deliberately is never overruled by the migration a second time.</summary>
    public bool FastFlowApplied { get; set; }
    /// <summary>Whether the one-time drop of the old 8-second assignment wait has been applied to
    /// this script. Remembered so a deliberately longer wait is never overruled twice.</summary>
    public bool AssignWaitTrimmed { get; set; }
    /// <summary>Whether the one-time drop of the old 12-second confirm wait has been applied.</summary>
    public bool ConfirmTrimmed { get; set; }

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
        Npc = (Npc ?? "").Trim();      // hand-edited files carry stray spaces; the offer matcher compares on it
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
        // ONE-TIME move to the say-only flow. A script that already knows the words does not need
        // the hail that taught them, and the /target existed to give the hail something to act on.
        // Applied once and remembered, so turning the hail back on by hand sticks.
        if (!FastFlowApplied && SayPhrases.Count > 0)
        {
            FastFlowApplied = true;
            HailFirst = false;
            TargetByName = false;
        }
        // ONE-TIME drop of the old 8-second default. 8 was what every existing script stored, and
        // the first attempt at this clamped only values ABOVE 8 — so the stored 8 survived and the
        // log went on saying "within 8s" after the fix had supposedly shipped. Widening it to >= 8
        // fixed that and broke something else: this runs on EVERY load, so it would also have
        // silently overruled anyone who set a longer wait on purpose, every launch, forever. Hence
        // a remembered one-shot, the same shape as the fast-flow migration above.
        if (!AssignWaitTrimmed)
        {
            AssignWaitTrimmed = true;
            if (AssignWaitSeconds >= 8) AssignWaitSeconds = 3;
        }
        if (AssignWaitSeconds <= 0 || AssignWaitSeconds > 30) AssignWaitSeconds = 3;
        if (GiveSettleMs < 200 || GiveSettleMs > 4000) GiveSettleMs = 1100;
        if (ConfirmSeconds < 2 || ConfirmSeconds > 60) ConfirmSeconds = 6;
        // ONE-TIME drop of the old 12-second default, the same shape as the other migrations: a
        // number nobody chose shouldn't sit there costing a quarter of every cycle, and a number
        // somebody DID choose shouldn't be overwritten every launch.
        // `== 12`, not `>= 12`: 12 was the old default, and the runner's own failure advice tells
        // people to RAISE this when the server is slow. Catching everything above the default would
        // have silently undone that advice on the first launch after the upgrade.
        if (!ConfirmTrimmed) { ConfirmTrimmed = true; if (ConfirmSeconds == 12) ConfirmSeconds = 6; }
        SuccessLines ??= new List<string>();
        if (!(IconTolerance >= 8 && IconTolerance <= 60)) IconTolerance = 35;
        OpenBagsKey = (OpenBagsKey ?? "").Trim();       // null from an older file, or a hand edit
        Shots ??= new Dictionary<string, PickShot>();
        if (BagCols <= 0) BagCols = 2;
        if (BagRows <= 0) BagRows = 5;
    }

    public static QuestScript FromQuest(QuestInfo q)
    {
        var script = new QuestScript
        {
            Quest = q.Name,
            Npc = string.IsNullOrWhiteSpace(q.TurnIns.FirstOrDefault()?.Npc) ? q.EndNpc : q.TurnIns[0].Npc,
            // Born on the new defaults, so the one-shot migrations have nothing to correct. Marking
            // them applied here stops a hand-edit made before the first reload from being eaten by
            // a migration that was only ever meant for scripts written by an older build.
            FastFlowApplied = true,
            AssignWaitTrimmed = true,
            ConfirmTrimmed = true,
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
            // Write beside it, then swap. A half-written file here is not a lost setting, it is a
            // lost SCRIPT: Load()'s catch turns unparseable JSON into an empty store, and the next
            // edit writes that emptiness back over everything the user ever built. The file grew
            // teeth when picks started carrying snapshots, so the window for a torn write did too.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
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
