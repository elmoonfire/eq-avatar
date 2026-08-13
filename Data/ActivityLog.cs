using System;
using System.Collections.Generic;
using System.Linq;

namespace EQAvatar.Spike.Data;

/// <summary>One thing the bot did, and which part of it did that thing.</summary>
/// <param name="Tag">Which specific thing inside that source spoke — a quest name, say. The
/// Activity Console groups by Source; a page that hosts many of one kind (every quest has its own
/// card) filters by Tag, so one card can never show another's evidence as its own.</param>
public sealed record ActivityEntry(DateTime When, string Source, string Text, string Tag = "")
{
    /// <summary>Steps she is DOING ("· clicking the NPC") read differently from verdicts
    /// ("✖ nothing came back"), and the console colours them apart.</summary>
    public bool IsBad => Text.StartsWith("✖") || Text.StartsWith("⚠")
                      || Text.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase)
                      || Text.StartsWith("Can't", StringComparison.OrdinalIgnoreCase);
    public bool IsGood => Text.StartsWith("✔") || Text.StartsWith("✓");
    public bool IsStep => Text.StartsWith("·");
}

/// <summary>
/// One place every role's narration goes.
///
/// It used to go to the Grind console, whatever role produced it, because the Grind console was
/// the only console there was. That made the grind history unreadable the moment a quest ran, and
/// it made the QUEST history impossible to find — you were reading someone else's log with your
/// own lines mixed in. So each role records under its own name here, and each page reads back the
/// slice it cares about: the Grind console shows Grind, the Questing card shows Quest, and the
/// Activity Console shows everything with the sources you choose.
///
/// Thread-safe because roles narrate from their own loops: the store is locked, and the change
/// event is raised OUTSIDE the lock so a UI handler that re-enters (a render that logs) can't
/// deadlock the runner that was only trying to say what it was doing.
/// </summary>
public static class ActivityLog
{
    /// <summary>Kept in memory, not on disk (Diag.BotLog already writes the durable copy). Deep
    /// enough to scroll back through a whole night's farming at ~6 lines a cycle.</summary>
    public const int Cap = 5000;

    private static readonly object _gate = new();
    private static readonly List<ActivityEntry> _items = new();
    private static readonly List<string> _sources = new();

    /// <summary>Raised for every entry, on the recording thread — handlers must marshal.</summary>
    public static event Action<ActivityEntry>? Added;

    public static void Record(string source, string text, string tag = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        source = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
        var e = new ActivityEntry(DateTime.Now, source, text.Trim(), (tag ?? "").Trim());
        lock (_gate)
        {
            _items.Add(e);
            if (_items.Count > Cap) _items.RemoveRange(0, _items.Count - Cap);
            if (!_sources.Contains(e.Source)) _sources.Add(e.Source);
        }
        try { Diag.BotLog.Log(source.ToLowerInvariant(), text); } catch { /* never let logging break a run */ }
        Added?.Invoke(e);
    }

    /// <summary>Newest last. <paramref name="max"/> 0 = everything kept.</summary>
    public static List<ActivityEntry> Snapshot(Func<ActivityEntry, bool>? include = null, int max = 0)
    {
        lock (_gate)
        {
            IEnumerable<ActivityEntry> q = _items;
            if (include is not null) q = q.Where(include);
            List<ActivityEntry> list = q.ToList();
            if (max > 0 && list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }
    }

    /// <summary>The most recent line from a source — "what is she doing right now".</summary>
    public static ActivityEntry? Latest(string source)
    {
        lock (_gate)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
                if (string.Equals(_items[i].Source, source, StringComparison.OrdinalIgnoreCase))
                    return _items[i];
            return null;
        }
    }

    /// <summary>Sources seen this session, in the order they first spoke.</summary>
    public static List<string> Sources()
    {
        lock (_gate) return _sources.ToList();
    }

    public static int Count { get { lock (_gate) return _items.Count; } }

    public static void Clear()
    {
        // The source list goes too. Leaving it behind means chips for sources with nothing in them,
        // and a "hide all" that pre-hides names before they have said anything.
        lock (_gate) { _items.Clear(); _sources.Clear(); }
    }
}
