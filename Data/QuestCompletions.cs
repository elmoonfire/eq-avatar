using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQAvatar.Spike.Data;

public sealed class QuestCompletion
{
    public string Quest { get; set; } = "";
    /// <summary>Local machine time of the FIRST confirmed completion — the timestamp the
    /// Completed column shows and sorts by.</summary>
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// The record of quests actually completed on this machine, at
/// <c>%AppData%\EQAvatar\questcompletions.json</c>.
///
/// A completion is only ever recorded off a CONFIRMED hand-in — the same log evidence the Quest
/// Runner refuses to run without — so the numbers here mean "the server said so", not "the bot
/// clicked". The first timestamp is kept forever and the count grows, because for a farmed quest
/// like Something is Wrrrong the interesting facts are "when did I start" and "how many times":
/// with a 1,024-item ladder, count-per-day IS the progress rate.
///
/// Written from the runner's background thread and read from the UI's render pass, so every
/// access goes through the lock.
/// </summary>
public static class QuestCompletions
{
    private static readonly object Gate = new();
    private static Dictionary<string, QuestCompletion>? _byQuest;   // key: QuestCatalog.Norm

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "questcompletions.json");

    private static Dictionary<string, QuestCompletion> Store()
    {
        if (_byQuest is not null) return _byQuest;
        var map = new Dictionary<string, QuestCompletion>();
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<QuestCompletion>>(File.ReadAllText(FilePath));
                foreach (QuestCompletion c in list ?? new())
                {
                    if (c?.Quest is not { Length: > 0 }) continue;
                    map[QuestCatalog.Norm(c.Quest)] = c;
                }
            }
        }
        catch { /* an unreadable history is an empty history, not a crash */ }
        return _byQuest = map;
    }

    /// <summary>Record one confirmed completion, stamped with the machine's local clock.</summary>
    public static void Record(string quest)
    {
        if (string.IsNullOrWhiteSpace(quest)) return;
        lock (Gate)
        {
            Dictionary<string, QuestCompletion> map = Store();
            string key = QuestCatalog.Norm(quest);
            if (!map.TryGetValue(key, out QuestCompletion? c))
                map[key] = c = new QuestCompletion { Quest = quest, First = DateTime.Now };
            c.Count++;
            c.Last = DateTime.Now;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(
                    map.Values.OrderBy(v => v.First).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>A snapshot copy, or null when the quest has never been completed here.</summary>
    public static QuestCompletion? Get(string quest)
    {
        lock (Gate)
        {
            return Store().TryGetValue(QuestCatalog.Norm(quest), out QuestCompletion? c)
                ? new QuestCompletion { Quest = c.Quest, First = c.First, Last = c.Last, Count = c.Count }
                : null;
        }
    }

    public static int CompletedQuestCount { get { lock (Gate) return Store().Count; } }
}
