using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EQAvatar.Spike.Data;

/// <summary>One thing the bot did, and which part of it did that thing.</summary>
/// <param name="Tag">Which specific thing inside that source spoke — a quest name, say. The
/// Activity Console groups by Source; a page that hosts many of one kind (every quest has its own
/// card) filters by Tag, so one card can never show another's evidence as its own.</param>
/// <param name="Detail">A troubleshooting line: the numbers behind a decision rather than the
/// decision. Only ever RECORDED while the console's detail switch is on — the gate is at the
/// recording end, not the rendering end, deliberately. Filtering at render would mean the buffer
/// held lines the console was hiding and "copy" then had to choose which of two truths to put on
/// the clipboard; gating at record means what you see, what you copy and what BotLog wrote on disk
/// are always the same set of lines.</param>
public sealed record ActivityEntry(DateTime When, string Source, string Text, string Tag = "", bool Detail = false,
                                   long Seq = 0)
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
    /// <summary>A strictly increasing id per entry. The consoles append what they are handed AND
    /// rebuild from a snapshot, and the two can overlap: a line recorded a moment before a console
    /// is created is in the snapshot the constructor reads AND in the dispatcher callback already
    /// queued for it. Timestamps can't settle that — two lines inside one millisecond compare
    /// equal — so each entry carries a number that only ever goes up.</summary>
    private static long _seq;

    /// <summary>Raised for every entry, on the recording thread — handlers must marshal.</summary>
    public static event Action<ActivityEntry>? Added;

    /// <summary>
    /// Whether the roles should narrate the numbers behind their decisions as well as the
    /// decisions themselves.
    ///
    /// Off by default and volatile because it is read from role loops on their own threads and
    /// written from the UI when someone flips the switch mid-run — which is exactly when it is
    /// most wanted: something has just gone wrong and you want the next pass explained.
    ///
    /// Detail lines are VOLUMINOUS by design (a merge sweep narrates every candidate it looked
    /// at). Leaving them on all the time would push the real narration out of a 5,000-line buffer
    /// in an hour, which is why this is a switch rather than a setting nobody would find again.
    /// </summary>
    private static int _detail;
    public static bool DetailEnabled
    {
        get => Volatile.Read(ref _detail) == 1;
        set => Volatile.Write(ref _detail, value ? 1 : 0);
    }

    public static void Record(string source, string text, string tag = "") => Add(source, text, tag, false);

    /// <summary>
    /// A troubleshooting line — match distances, click coordinates, the raw text an OCR read
    /// before anything parsed it. Silently dropped unless <see cref="DetailEnabled"/> is on, so a
    /// role can call this freely in its hot loop without guarding every call site.
    /// </summary>
    public static void Detail(string source, string text, string tag = "")
    {
        if (!DetailEnabled) return;
        Add(source, text, tag, true);
    }

    private static void Add(string source, string text, string tag, bool detail)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        source = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
        ActivityEntry e;
        lock (_gate)
        {
            e = new ActivityEntry(DateTime.Now, source, text.Trim(), (tag ?? "").Trim(), detail, ++_seq);
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

    /// <summary>Nothing at or below this id exists any more. A console is fed one line at a time
    /// through the dispatcher, so a line recorded a moment before Clear can still be sitting in the
    /// queue when the consoles rebuild from an empty log — and it would then be drawn into a console
    /// showing a line that exists nowhere else, with "copy" reporting a different set than the page.</summary>
    public static long ClearedThrough { get { lock (_gate) return _cleared; } }
    private static long _cleared;

    public static void Clear()
    {
        // The source list goes too. Leaving it behind means chips for sources with nothing in them,
        // and a "hide all" that pre-hides names before they have said anything.
        lock (_gate) { _cleared = _seq; _items.Clear(); _sources.Clear(); }
    }
}
