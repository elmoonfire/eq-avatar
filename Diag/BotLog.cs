using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace EQAvatar.Spike.Diag;

/// <summary>
/// Persistent bot debug log. EVERY decision line the roles emit (and the hub/licensing chatter)
/// lands here with a timestamp and source tag, in daily files under %AppData%\EQAvatar\logs —
/// so when the bot does something dumb in-game, the trace of WHY survives the session and can
/// be engineered against. Cheap by design: lock-free enqueue, one background flush per second,
/// files pruned after 14 days. A ring of recent lines is kept for in-app viewing.
/// </summary>
public static class BotLog
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly object FlushLock = new();
    private static Timer? _flush;
    private static string? _dir;
    private static readonly LinkedList<string> Recent = new();
    private const int RecentMax = 3000;

    public static string Dir => _dir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "logs");

    public static void Init(string appVersion)
    {
        try
        {
            _dir = Dir;
            Directory.CreateDirectory(_dir);
            _flush ??= new Timer(_ => Flush(), null, 1000, 1000);
            Log("app", $"—— EQ Avatar {appVersion} started ——");
            Prune();
        }
        catch { /* logging must never hurt the app */ }
    }

    /// <summary>Append one line. Safe from any thread, never throws.</summary>
    public static void Log(string source, string message)
    {
        try
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{source}] {message}";
            Pending.Enqueue(line);
            lock (Recent)
            {
                Recent.AddLast(line);
                while (Recent.Count > RecentMax) Recent.RemoveFirst();
            }
        }
        catch { }
    }

    /// <summary>Recent lines for the in-app console (newest last).</summary>
    public static string[] Tail(int count = 400)
    {
        lock (Recent) return Recent.Skip(Math.Max(0, Recent.Count - count)).ToArray();
    }

    public static void Flush()
    {
        if (Pending.IsEmpty || _dir is null) return;
        lock (FlushLock)
        {
            try
            {
                var lines = new List<string>();
                while (Pending.TryDequeue(out string? l)) lines.Add(l);
                if (lines.Count == 0) return;
                File.AppendAllLines(Path.Combine(_dir, $"bot-{DateTime.Now:yyyyMMdd}.log"), lines);
            }
            catch { }
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (string f in Directory.GetFiles(_dir!, "bot-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }
}
