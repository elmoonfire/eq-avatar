using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQAvatar.Spike.Input;

/// <summary>One in-game key binding: an action name and the key(s) the game has bound to it.</summary>
public sealed class KeyBind
{
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string Primary { get; set; } = "";
    public string Alternate { get; set; } = "";
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

    /// <summary>Merge one OCR pass (or manual adds): match by action name (ci), update keys that
    /// came back non-empty, append the rest. Returns (new, updated) counts and stamps the store.</summary>
    public (int added, int updated) Merge(IEnumerable<KeyBind> found, bool stamp = true)
    {
        int added = 0, updated = 0;
        foreach (var f in found)
        {
            if (string.IsNullOrWhiteSpace(f.Action)) continue;
            var existing = Binds.FirstOrDefault(b => string.Equals(b.Action, f.Action, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Binds.Add(f);
                added++;
            }
            else
            {
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
}
