using System;
using System.IO;
using System.Text;
using System.Threading;

namespace EQAvatar.Spike.Log;

/// <summary>
/// Offset-based, truncation-safe tailer for an EverQuest log file. Polls on a timer,
/// reads only the bytes appended since the last read, and reopens the file with a shared
/// read handle so it never locks the game out. This mirrors the approach EQBuddy describes
/// ("500 ms polls, offset-based, truncation-safe") and is what the real EQ Avatar log
/// module will grow out of.
/// </summary>
public sealed class EqLogWatcher : IDisposable
{
    /// <summary>Ceiling on one poll's read. The live tail moves a few hundred bytes at a time, so
    /// this only matters for Start(fromStart: true) against a log that has been running for weeks —
    /// Hayden's was 84 MB on 08-26 — where reading it in one allocation is a large-object-heap
    /// spike for no reason. The backlog just arrives over the next few polls instead.</summary>
    private const int MaxPollBytes = 1 << 22;   // 4 MB

    /// <summary>How large a half-written line is allowed to get before it is treated as proof the
    /// file has no line endings at all, rather than as a line still being written.</summary>
    private const int MaxFragmentBytes = 1 << 16;   // 64 KB

    private readonly string _path;
    private readonly int _pollMs;
    private Timer? _timer;
    private long _offset;
    private readonly object _gate = new();
    private bool _running;

    public event Action<string>? LineRead;
    public event Action<string>? Info;

    /// <param name="pollMs">
    /// How often the file is checked for new bytes — and therefore the floor on how quickly the app
    /// can know ANYTHING. It was 500, which is most of a second before a line the game wrote is
    /// seen, and that showed up in the field as engagements taking six to eight seconds: the hunt
    /// loop asked for a con, waited, gave up before the answer had even been read off disk, roamed,
    /// and asked again. The con had been sitting in the file the whole time.
    ///
    /// A poll is a seek and a read of whatever is new — a few microseconds against a file the OS has
    /// in cache — so this is cheap enough to do properly. Everything downstream gets faster with it:
    /// kills, deaths, hand-in confirmations, the lot.
    /// </param>
    public EqLogWatcher(string path, int pollMs = 150)
    {
        _path = path;
        _pollMs = pollMs;
    }

    /// <param name="fromStart">
    /// true = replay the whole file (useful to inspect what's in an existing log);
    /// false = start at the current end and only surface new lines (live tail).
    /// </param>
    public void Start(bool fromStart)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            try
            {
                _offset = fromStart ? 0 : new FileInfo(_path).Length;
            }
            catch
            {
                _offset = 0;
            }
            // ONE-SHOT, RE-ARMED AT THE END OF EACH POLL. A periodic Timer queues a fresh callback
            // every period whether or not the last one has finished — and Poll is not fast: it
            // raises LineRead synchronously and every subscriber marshals to the UI thread with a
            // BLOCKING Dispatcher.Invoke, so a busy render or an OCR pass stalls it. Two overlapping
            // polls both read the same offset and deliver every line TWICE, which at the far end is
            // double-counted kills and hand-in credit written into permanent history. At 500 ms that
            // was unlikely; at 150 it would be routine.
            _timer = new Timer(_ => Poll(), null, 0, Timeout.Infinite);
            Info?.Invoke($"Tailing {_path} (poll {_pollMs} ms, from {(fromStart ? "start" : "end")}).");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Belt as well as braces: the one-shot timer above should make overlap impossible, but
    /// a guard costs nothing and the consequence of being wrong is silent duplicate history.</summary>
    private int _polling;

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        // A TAILER MUST NEVER TAKE THE PROCESS WITH IT. PollCore has its own handler, but that
        // handler reports through Info, which marshals to the UI thread with a blocking Invoke —
        // so on shutdown it throws while handling a throw and escapes onto a thread-pool timer
        // thread, where an unhandled exception ends the process. The user would see "EQ Avatar hit
        // a fatal error" for the crime of closing the app, and it would spend the one-shot crash
        // dialog that a real fault needs.
        try { PollCore(); }
        catch { /* nothing a log tailer can hit is worth a process for */ }
        finally
        {
            Volatile.Write(ref _polling, 0);
            // Re-arm only while still running. Stop() disposes the timer under the lock, so a
            // Change() on a disposed one is caught rather than raced.
            try { lock (_gate) _timer?.Change(_pollMs, Timeout.Infinite); } catch { /* stopped */ }
        }
    }

    private void PollCore()
    {
        if (!_running) return;
        try
        {
            if (!File.Exists(_path)) return;

            long length = new FileInfo(_path).Length;
            if (length < _offset)
            {
                // File was truncated or rotated — restart from the top.
                // RESET FIRST, ANNOUNCE SECOND. Info marshals to the UI thread, and a subscriber
                // that throws while the window is closing used to jump out of this method with
                // _offset still past the end of a file that had just been rotated. Every later
                // poll took the same branch, threw in the same place, and the tailer never read
                // another line — silently, because the thing that was failing was the logger.
                _offset = 0;
                Info?.Invoke("Log truncated/rotated — resetting to start.");
            }
            if (length == _offset) return;

            // ── ONLY COMPLETE LINES, AND THE OFFSET ONLY MOVES OVER COMPLETE LINES ──────────
            //
            // The client is WRITING this file while we read it, so a poll lands mid-line roughly
            // whenever a poll lands. The old code used StreamReader.ReadLine() to EOF and then set
            // `_offset = fs.Position`, and both halves of that are wrong at the same moment:
            // ReadLine hands out the half-written line as though it were finished, and Position —
            // which is where the reader's BUFFER ended, not where the last line did — then skips
            // the rest of it for ever. So a line straddling a poll arrived split in two, and the
            // second half never arrived at all.
            //
            // That was survivable while every test in this app was a Contains() over a fragment
            // that usually still held the keyword. It is not survivable now: the combat evidence
            // is ANCHORED at the start of the line, and "…rran `amir has taken 66 damage from
            // your Fufil's" matches nothing. A lost kill line, a lost death, a lost "You are now
            // A.F.K." are all the same bug and always were.
            //
            // So: read bytes, cut at the last newline, publish only what is left of it, and leave
            // the remainder in the file for the next poll to find whole. Splitting on 0x0A is
            // UTF-8-safe — a continuation byte is never 0x0A.
            //
            // AND IT IS HELD FOR EVER IF IT NEVER COMPLETES, deliberately. Replaying a finished
            // log that does not end in a newline therefore drops its last line — EQ writes one per
            // line, so that is a file somebody truncated by hand. The alternative is a timer that
            // gives up and publishes the fragment, and if the writer then finishes the line the
            // remainder arrives as a SECOND line: the split-line bug back again, now with a race
            // in front of it. A line withheld is recoverable; a line published twice, in halves,
            // is what this replaces.
            long want = length - _offset;
            if (want > MaxPollBytes) want = MaxPollBytes;      // a first-run backlog, in slices

            byte[] buf = new byte[want];
            int got;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_offset, SeekOrigin.Begin);
                got = 0;
                while (got < buf.Length)
                {
                    int n = fs.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            if (got <= 0) return;

            int cut = Array.LastIndexOf(buf, (byte)'\n', got - 1);
            if (cut < 0)
            {
                // No complete line yet — wait for the rest.
                //
                // UNLESS the pending fragment has grown past anything a log line can be. That
                // means a file with no newlines in it at all, and holding for a newline that is
                // never coming would re-read and re-allocate the SAME bytes every 150 ms for the
                // life of the process. Bounded at the fragment size rather than at the poll cap:
                // a 3 MB newline-free file is under the cap, so a cap-sized test would never fire
                // and would churn megabyte allocations six times a second for ever.
                if (got >= MaxFragmentBytes)
                {
                    _offset += got;
                    Info?.Invoke($"Skipped {got} bytes with no line ending in them — this file does not look "
                               + "like a game log.");
                }
                return;
            }

            // A byte-order mark only exists at the very top of the file, and reading bytes by hand
            // means nothing strips it for us; left alone it prefixes the first line with U+FEFF and
            // defeats every anchor on it.
            int from = 0;
            if (_offset == 0 && got >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) from = 3;

            string text = Encoding.UTF8.GetString(buf, from, (cut + 1) - from);
            _offset += cut + 1;

            // ONE SUBSCRIBER'S BAD DAY IS NOT THE REST OF THE BATCH'S. The offset has already
            // moved over these lines — it has to, or a later poll re-delivers them — so an
            // exception escaping this loop would take every line after it with it, permanently.
            // Forty lines arrive, a parser throws on the third, and the death and the kill in
            // lines 20 and 31 are simply gone. Each line is handed out on its own.
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                try { LineRead?.Invoke(line); }
                catch (Exception ex) { Info?.Invoke("Line handler failed (continuing): " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Info?.Invoke("Tail error: " + ex.Message);
        }
    }

    /// <summary>Find the most recently modified eqlog_*.txt in a folder, if any.</summary>
    public static string? FindNewestLog(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            string? newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string file in Directory.EnumerateFiles(folder, "eqlog_*.txt"))
            {
                DateTime t = File.GetLastWriteTimeUtc(file);
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = file;
                }
            }
            return newest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// EQ logs are named eqlog_&lt;Character&gt;_&lt;server&gt;.txt — pull the character name and
    /// server straight out of the filename so the app can auto-fill who's checking in.
    /// </summary>
    public static (string name, string server)? CharacterFromLog(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string file = Path.GetFileNameWithoutExtension(path);
        if (!file.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase)) return null;
        string rest = file.Substring("eqlog_".Length);
        int us = rest.LastIndexOf('_');
        string name = us > 0 ? rest.Substring(0, us) : rest;
        string server = us > 0 ? rest.Substring(us + 1) : "";
        if (name.Length == 0) return null;
        // Title-case the server token (rivervale → Rivervale) for display.
        if (server.Length > 0) server = char.ToUpperInvariant(server[0]) + server.Substring(1);
        return (name, server);
    }

    public void Dispose() => Stop();
}
