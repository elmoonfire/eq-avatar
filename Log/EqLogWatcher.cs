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
                Info?.Invoke("Log truncated/rotated — resetting to start.");
                _offset = 0;
            }
            if (length == _offset) return;

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > 0)
                    LineRead?.Invoke(line);
            }
            _offset = fs.Position;
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
