using System;
using System.Collections.Generic;
using System.IO;

namespace EQAvatar.Spike.Login;

/// <summary>Why the EverQuest client stopped running, read from its OWN diagnostic log.</summary>
public enum CloseKind
{
    /// <summary>Nothing conclusive in the log — treat as a crash for recovery purposes, but say so.</summary>
    Unknown,
    /// <summary>The SERVER ended the session while the client was healthy: a graceful drop to
    /// character select followed by the world terminating the connection. This is what a patch or
    /// a scheduled restart looks like from the client's side.</summary>
    ServerShutdown,
    /// <summary>The idle kick: the connection died and the client then failed to re-authenticate
    /// ("Rejected By World" ×N → END_GAME). Measured on 08-24 following an A.F.K. flag.</summary>
    AuthKick,
    /// <summary>The user camped out or quit deliberately.</summary>
    UserQuit,
    /// <summary>The process vanished with no orderly shutdown in the log at all.</summary>
    Crash,
}

/// <summary>
/// Reads the tail of the client's own `dbg.txt` and says WHY the game closed.
///
/// WHY THIS EXISTS. Three overnight closes had three different causes — an idle kick after a
/// death, an idle kick after a stolen focus, and a scheduled server patch — and the app called
/// all three "the client itself exited or crashed", which is true of all of them and useful for
/// none. The recovery policy differs sharply: a patch means the world is DOWN and relaunching
/// immediately just burns login attempts against a server that isn't there, while a crash or a
/// kick can be retried in a minute. So the wait is chosen from the reason, and the reason is
/// read rather than assumed.
///
/// EVERY SIGNATURE HERE CAME OUT OF A REAL LOG, not from documentation:
///   • patch (08-25 05:00) — "Starting char select" with no user input, then
///     `connection terminated [client:DisconnectReasonOtherSideTerminated,server:DisconnectReasonApplication]`
///     then `YOU HAVE BEEN DISCONNECTED.` The tell is that the client was mid-fight one second
///     earlier and shut down in an orderly way: the world went away, not the client.
///   • idle kick (08-24 05:54) — ten × `Rejected By World. retrying` then
///     `*** ERROR: ProcessGame could not authenticate to world.  Bailing with END_GAME.`
/// </summary>
public static class CloseReason
{
    /// <summary>How much of the tail to read. The interesting window is the last few seconds of
    /// the client's life, which is a few dozen lines; 400 is slack for a chatty shutdown.</summary>
    private const int TailLines = 400;

    public sealed record Verdict(CloseKind Kind, string Evidence)
    {
        /// <summary>One clause for the console, in the user's terms.</summary>
        public string Say => Kind switch
        {
            CloseKind.ServerShutdown => "the SERVER ended the session (a patch or a scheduled restart) — the client shut down cleanly, so nothing here was broken",
            CloseKind.AuthKick => "the server dropped the session and the client could not log back in — the idle-kick signature",
            CloseKind.UserQuit => "someone camped out deliberately",
            CloseKind.Crash => "the client died without an orderly shutdown — a crash",
            _ => "the client's log says nothing conclusive about why",
        };

        /// <summary>How long to wait before the FIRST relaunch attempt. A world that is patching
        /// is not going to answer in sixty seconds, and hammering it is both useless and rude;
        /// a crash, by contrast, can be retried straight away.</summary>
        public TimeSpan FirstWait => Kind switch
        {
            CloseKind.ServerShutdown => TimeSpan.FromMinutes(10),
            CloseKind.AuthKick => TimeSpan.FromMinutes(2),
            CloseKind.UserQuit => TimeSpan.MaxValue,      // deliberate: never auto-relaunch
            _ => TimeSpan.FromMinutes(1),
        };
    }

    /// <summary>Classify from a log folder (finds dbg.txt itself). Never throws.</summary>
    public static Verdict FromLogFolder(string? logFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logFolder)) return new(CloseKind.Unknown, "no log folder set");
            // BOTH PLACES. On this install the client writes dbg.txt beside the eqlogs, but the
            // classic layout puts it in the install root one level up, and a verdict that silently
            // degrades to "no idea" on someone else's folder layout is the failure this whole
            // class exists to stop.
            string? dbg = FirstExisting(
                Path.Combine(logFolder, "dbg.txt"),
                Path.Combine(Path.GetDirectoryName(logFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? logFolder, "dbg.txt"));
            if (dbg is null) return new(CloseKind.Unknown, "no dbg.txt beside or above " + logFolder);
            return FromLines(Tail(dbg, TailLines));
        }
        catch (Exception ex) { return new(CloseKind.Unknown, "couldn't read dbg.txt: " + ex.Message); }
    }

    /// <summary>The decision itself, over already-read lines. Separated so it can be tested
    /// against captured logs without a filesystem.</summary>
    public static Verdict FromLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return new(CloseKind.Unknown, "dbg.txt was empty");

        // SCANNED BACKWARDS, NEWEST FIRST, and the first DECISIVE marker wins. A forward scan of
        // flags was wrong twice over: dbg.txt accumulates within a session, so an earlier event
        // outvoted a later one — and worse, "Rejected By World. retrying" is what a perfectly
        // HEALTHY login prints when it needs a second go, so one retry at 8pm would have
        // classified a midnight server patch as an idle kick and retried it two minutes later
        // against a world that was down for an hour.
        //
        // "Decisive" therefore means the TERMINAL state, never a step on the way to one.
        bool sawDisconnect = false, sawOrderlyExit = false;
        int newerSeq = int.MaxValue;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string l = lines[i];

            // SESSION BOUNDARY. dbg.txt can carry more than one launch, and every line is stamped
            // with a counter the client resets on startup ("]01650:"). Walking backwards those
            // numbers only ever DECREASE within one session, so the first increase is the top of
            // this session — beyond it lies the previous run, whose ending has nothing to do with
            // this one. Without this, a crash that wrote no marker inherits the verdict of the
            // launch before it.
            int seq = SeqOf(l);
            if (seq >= 0)
            {
                if (seq > newerSeq) break;
                newerSeq = seq;
            }

            // The kick's terminal state — the client giving up on logging back in. Note this is
            // NOT the bare "Rejected By World", which is a retry line and proves nothing.
            if (Has(l, "could not authenticate to world") || Has(l, "Bailing with END_GAME")
                || Has(l, "Rejected By World. abort"))
                return new(CloseKind.AuthKick, Trim(l));

            // THE WORLD ENDED IT — and this reads WHICH SIDE, which the previous version only
            // claimed to. The captured line is
            //     [client:DisconnectReasonOtherSideTerminated,server:DisconnectReasonApplication]
            // i.e. BOTH tokens appear, one per side, so any test that merely asks whether the
            // line contains them says the same thing about a patch and about a quit-to-desktop
            // (which writes the mirror image). Only the `client:` half is the client's own
            // account of who hung up, so only the `client:` half is read.
            if (Has(l, "client:DisconnectReasonOtherSideTerminated"))
                return new(CloseKind.ServerShutdown, Trim(l));
            if (Has(l, "client:DisconnectReasonApplication") || Has(l, "client:DisconnectReasonUserRequest"))
                return new(CloseKind.UserQuit, Trim(l));

            if (Has(l, "Camping") || Has(l, "camp out"))
                return new(CloseKind.UserQuit, Trim(l));

            // TWO DIFFERENT FACTS, and merging them was an inversion: "YOU HAVE BEEN
            // DISCONNECTED" is the SESSION being lost, while SETD/Cleanup is the PROCESS winding
            // down tidily. Counting the disconnect as evidence of a tidy hand-close meant every
            // plain network drop — the one thing most worth recovering from — was read as
            // "the user quit" and deliberately left closed.
            if (Has(l, "YOU HAVE BEEN DISCONNECTED")) sawDisconnect = true;
            // ANCHORED. A bare 4-character "SETD" can appear inside a path, a zone name or a
            // hex dump; the client writes it as its own field after the line counter, and a
            // false hit here turns a crash into "the user quit" and silently costs the night.
            else if (Has(l, ":SETD") || Has(l, "Cleanup 11")) sawOrderlyExit = true;
        }

        // A session that was lost with no side recorded is still a lost session, and the useful
        // assumption is that the world went away — that is the recoverable case, and waiting ten
        // minutes to find out costs nothing but ten minutes.
        if (sawDisconnect)
            return new(CloseKind.ServerShutdown, "the session was disconnected, with no reason code recorded");
        // Tidy shutdown, never disconnected: nobody took this session away, so somebody closed it.
        if (sawOrderlyExit)
            return new(CloseKind.UserQuit, "an orderly shutdown with no disconnect at all — closed by hand");
        return new(CloseKind.Crash, $"no disconnect and no orderly shutdown in the last {lines.Count} lines");
    }

    private static string? FirstExisting(params string[] paths)
    {
        foreach (string p in paths) { try { if (File.Exists(p)) return p; } catch { } }
        return null;
    }

    /// <summary>The client's own line counter from a "…]01650:…" prefix, or -1 when absent.</summary>
    private static int SeqOf(string line)
    {
        int close = line.IndexOf(']');
        if (close < 0 || close + 1 >= line.Length) return -1;
        int i = close + 1, start = i;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == start || i >= line.Length || line[i] != ':') return -1;
        return int.TryParse(line.AsSpan(start, i - start), out int v) ? v : -1;
    }

    private static bool Has(string line, string needle) =>
        line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Trim(string l)
    {
        string s = l.Trim();
        return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
    }

    /// <summary>
    /// Last N lines, tolerant of a file the game still holds open.
    ///
    /// Seeks from the END rather than reading the file through: this runs on the UI thread from
    /// the close verdict, and the previous version shifted a 400-element list once per line for
    /// the whole file — quadratic in the one place where the answer is wanted promptly.
    /// </summary>
    private static List<string> Tail(string path, int n)
    {
        const int WindowBytes = 256 * 1024;      // ~2000 lines of this log; far past any shutdown
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - WindowBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        if (start > 0) sr.ReadLine();            // the seek almost certainly landed mid-line
        var all = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null) all.Add(line);
        return all.Count <= n ? all : all.GetRange(all.Count - n, n);
    }
}
