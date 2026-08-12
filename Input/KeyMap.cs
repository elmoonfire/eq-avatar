using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQAvatar.Spike.Input;

/// <summary>One in-game key binding: an action name and the key(s) the game has bound to it.
/// <see cref="Locked"/> protects a bind from being overwritten when the user ingests someone
/// else's mappings — captures from your OWN game always win, locks only guard against imports.</summary>
public sealed class KeyBind
{
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string Primary { get; set; } = "";
    public string Alternate { get; set; } = "";
    public bool Locked { get; set; }

    public KeyBind Clone() => new()
    { Category = Category, Action = Action, Primary = Primary, Alternate = Alternate, Locked = Locked };

    /// <summary>Do these two describe the same keys? (Used by the ingest diff.)</summary>
    public bool SameKeys(KeyBind other) =>
        string.Equals(Primary.Trim(), other.Primary.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Alternate.Trim(), other.Alternate.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The bot's copy of the game's Controls → Key binds screen, filled by OCR captures on the
/// Key Mappings page and by hand. Sequencer ACTION pills resolve through this. Persisted at
/// %AppData%\EQAvatar\keymaps.json with the last-refreshed stamp the page shows.
/// </summary>
public sealed class KeyMapStore
{
    public List<KeyBind> Binds { get; set; } = new();
    public DateTime? LastRefreshed { get; set; }
    /// <summary>Set when this set was pulled from another member's shared page (who + when).</summary>
    public string? IngestedFrom { get; set; }
    public DateTime? IngestedAt { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "keymaps.json");

    private static KeyMapStore? _current;
    public static KeyMapStore Current => _current ??= Load();

    public static KeyMapStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<KeyMapStore>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
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

    public KeyBind? Find(string action) =>
        Binds.FirstOrDefault(b => string.Equals(b.Action, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>Merge one OCR pass (or manual adds): match by action name (ci), update keys that
    /// came back non-empty, append the rest. <paramref name="respectLocks"/> makes locked rows
    /// untouchable — that's how an ingest keeps the keys you pinned. Returns (new, updated).</summary>
    public (int added, int updated) Merge(IEnumerable<KeyBind> found, bool stamp = true, bool respectLocks = false)
    {
        int added = 0, updated = 0;
        foreach (var f in found)
        {
            if (string.IsNullOrWhiteSpace(f.Action)) continue;
            var existing = Find(f.Action);
            if (existing is null)
            {
                Binds.Add(f.Clone());
                added++;
            }
            else
            {
                if (respectLocks && existing.Locked) continue;
                bool ch = false;
                if (f.Primary.Length > 0 && f.Primary != existing.Primary) { existing.Primary = f.Primary; ch = true; }
                if (f.Alternate.Length > 0 && f.Alternate != existing.Alternate) { existing.Alternate = f.Alternate; ch = true; }
                if (f.Category.Length > 0 && existing.Category.Length == 0) { existing.Category = f.Category; ch = true; }
                if (ch) updated++;
            }
        }
        if (stamp) LastRefreshed = DateTime.Now;
        Save();
        return (added, updated);
    }

    /// <summary>What the app publishes to the member hub (locks are personal — they don't travel).</summary>
    public string ToShareJson(string username) => JsonSerializer.Serialize(new
    {
        username,
        captured = (LastRefreshed ?? DateTime.Now).ToString("o"),
        version = AppVersionTag,
        binds = Binds.Select(b => new { cat = b.Category, action = b.Action, primary = b.Primary, alt = b.Alternate }),
    });

    [JsonIgnore] public static string AppVersionTag => Config.AppSettings.AppVersion;
}
