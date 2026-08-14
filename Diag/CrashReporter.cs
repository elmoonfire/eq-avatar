using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Net;

namespace EQAvatar.Spike.Diag;

/// <summary>
/// Faults, reported to the hub without anybody being asked to describe them.
///
/// EVERY REPORT GOES TO DISK FIRST. A fatal exception is the process on its way out; an async HTTP
/// post started at that moment does not finish, and the report that mattered most is the one that
/// never arrives. So a fault is written to a spool file synchronously — a few hundred bytes, one
/// append — and the network is a separate concern that runs on the next flush, which may be after
/// the next launch. That ordering is the whole design.
///
/// IT DOES NOT REPLACE THE CRASH LOG. <c>App.WriteCrash</c> still writes its full crash-*.txt and
/// still shows its message box; this subscribes to the same two events as an ADDITIONAL handler,
/// so neither knows or cares about the other. If this file were deleted tomorrow, crash logging
/// would be exactly what it was in 0.9.22.
///
/// IT BACKS OFF. The hub answers every report with the running count for that fault. A loop
/// throwing every tick is one bug, not four thousand, and once the hub says it has seen this one
/// often enough the client stops sending it for the rest of the session. The hub dedupes too —
/// belt and braces, because the cheapest report is the one never posted.
/// </summary>
public static class CrashReporter
{
    /// <summary>Stop posting a fault once the hub has this many of it. It is on the queue; saying
    /// it again adds nothing.</summary>
    private const int NoisyAfter = 25;

    /// <summary>A hard ceiling on spooled lines, so a crash loop with no network cannot fill a
    /// disk. Oldest go first — the first occurrence is already recorded, and the newest tell you
    /// what is happening now.</summary>
    private const int MaxSpooled = 200;

    private static readonly object FileLock = new();
    private static readonly ConcurrentDictionary<string, byte> Noisy = new();

    private static AppSettings? _settings;
    private static SupportClient? _client;
    private static int _hooked;
    private static int _installed;
    private static int _flushing;

    private static string SpoolPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EQAvatar", "logs", "pending-errors.jsonl");

    /// <summary>
    /// Catch faults from the earliest possible moment — called from a module initializer, before
    /// <c>Main</c>, before any window exists.
    ///
    /// THE STARTUP CRASH IS THE ONE THAT MATTERS MOST, and it is the one a window's Loaded handler
    /// can never see: 0.9.21 died before it drew anything. Queueing needs no settings and no
    /// network — it is one append to a file — so the hook goes on at assembly load and the sending
    /// waits for <see cref="Install"/> to hand over a hub to send to.
    /// </summary>
    public static void InstallEarly()
    {
        if (Interlocked.Exchange(ref _hooked, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Queue(a.ExceptionObject as Exception, "fatal");
    }

    /// <summary>
    /// Hand over the settings, add the UI-thread hook, and send anything spooled.
    ///
    /// Safe to call twice — the interlock makes a second call free — because it is wired from a
    /// window Loaded handler, and windows can be created more than once.
    /// </summary>
    public static void Install(AppSettings settings)
    {
        _settings = settings;
        _client ??= new SupportClient(settings);

        InstallEarly();
        if (Interlocked.Exchange(ref _installed, 1) == 0 && Application.Current is { } app)
            app.DispatcherUnhandledException += (_, a) => Queue(a.Exception, "ui");

        // Anything left from a previous run — very much including the crash that ended it.
        _ = Task.Run(() => FlushAsync());
    }

    /// <summary>
    /// Record a fault. Returns immediately: this is called from exception handlers, sometimes
    /// while the process is dying, and it must never block, throw, or wait on a network.
    /// </summary>
    public static void Queue(Exception? ex, string where)
    {
        if (ex is null) return;
        try
        {
            string kind = ex.GetType().Name;
            string message = Clip(ex.Message, 400);
            string stack = Clip(ex.StackTrace ?? "", 4000);
            string fp = Fingerprint(kind, message, stack);
            if (Noisy.ContainsKey(fp)) return;          // the hub has plenty of this one already

            var line = new Spooled
            {
                T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Kind = kind,
                Message = message,
                Stack = stack,
                Version = AppSettings.AppVersion,
                Where = where,
            };

            lock (FileLock)
            {
                string path = SpoolPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(line) + Environment.NewLine, Encoding.UTF8);
                TrimLocked(path);
            }
        }
        catch { /* reporting a crash must never cause one */ }
    }

    /// <summary>
    /// Try to post everything spooled, and keep whatever did not go.
    ///
    /// Rewrites the file with the survivors rather than deleting as it goes: if the process dies
    /// mid-flush, the worst case is a report sent twice — which the hub dedupes — rather than a
    /// report lost, which nothing can undo.
    /// </summary>
    public static async Task FlushAsync(CancellationToken ct = default)
    {
        if (_client is null || !_client.Ready) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;

        try
        {
            List<Spooled> pending = ReadSpool();
            if (pending.Count == 0) return;

            var keep = new List<Spooled>();
            foreach (Spooled s in pending)
            {
                if (ct.IsCancellationRequested) { keep.Add(s); continue; }

                string fp = Fingerprint(s.Kind, s.Message, s.Stack);
                if (Noisy.ContainsKey(fp)) continue;                 // drop it, the hub has it

                (bool ok, int count, string message) =
                    await _client.ReportError(s.Kind, s.Message, s.Stack, s.Version, ct);

                if (!ok)
                {
                    // A refusal is not a failure to reach the hub: a report the hub will never
                    // accept would otherwise sit in the spool being retried forever.
                    if (message.Contains("no character name", StringComparison.OrdinalIgnoreCase))
                    {
                        keep.Add(s);                                  // it will have one eventually
                    }
                    else if (message.Length > 0 && !LooksLikeNetwork(message))
                    {
                        BotLog.Log("support", $"error report refused, dropping: {message}");
                    }
                    else
                    {
                        keep.Add(s);
                    }
                    continue;
                }

                if (count >= NoisyAfter) Noisy[fp] = 1;
            }

            WriteSpool(keep);
            if (pending.Count != keep.Count)
                BotLog.Log("support", $"sent {pending.Count - keep.Count} error report(s) to the hub");
        }
        catch (Exception ex) { BotLog.Log("support", "flush failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>How many faults are waiting to go, for the support window to mention.</summary>
    public static int PendingCount()
    {
        try { return ReadSpool().Count; } catch { return 0; }
    }

    /* ------------------------------------------------------------------ the spool */

    private static List<Spooled> ReadSpool()
    {
        lock (FileLock)
        {
            string path = SpoolPath;
            if (!File.Exists(path)) return new List<Spooled>();
            var outp = new List<Spooled>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    Spooled? s = JsonSerializer.Deserialize<Spooled>(line);
                    if (s is not null && s.Message.Length > 0) outp.Add(s);
                }
                catch { /* one unreadable line must not strand the rest */ }
            }
            return outp;
        }
    }

    private static void WriteSpool(List<Spooled> rows)
    {
        lock (FileLock)
        {
            string path = SpoolPath;
            if (rows.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)), Encoding.UTF8);
        }
    }

    /// <summary>Caller holds <see cref="FileLock"/>.</summary>
    private static void TrimLocked(string path)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length <= MaxSpooled) return;
            File.WriteAllLines(path, lines.Skip(lines.Length - MaxSpooled), Encoding.UTF8);
        }
        catch { }
    }

    /* ------------------------------------------------------------------ helpers */

    /// <summary>
    /// The same shape the hub fingerprints on: type, message, first stack frame, version. The
    /// version belongs in it — the same exception before and after a fix are different facts, and
    /// merging them hides whether the fix worked.
    /// </summary>
    private static string Fingerprint(string kind, string message, string stack)
    {
        string top = stack.Split('\n').FirstOrDefault()?.Trim() ?? "";
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{kind}|{message}|{top}|{AppSettings.AppVersion}"));
        return Convert.ToHexString(h)[..32];
    }

    private static bool LooksLikeNetwork(string message) =>
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
        || message.Contains("connection", StringComparison.OrdinalIgnoreCase)
        || message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
        || message.Contains("network", StringComparison.OrdinalIgnoreCase)
        || message.Contains("the hub said 5", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class Spooled
    {
        public long T { get; set; }
        public string Kind { get; set; } = "";
        public string Message { get; set; } = "";
        public string Stack { get; set; } = "";
        public string Version { get; set; } = "";
        public string Where { get; set; } = "";
    }
}
