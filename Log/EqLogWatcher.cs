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

    public EqLogWatcher(string path, int pollMs = 500)
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
            _timer = new Timer(_ => Poll(), null, 0, _pollMs);
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

    private void Poll()
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
