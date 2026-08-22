using System;
using System.Text.RegularExpressions;

namespace EQAvatar.Spike.Log;

public enum LogEventKind
{
    Other,
    Location,   // the prize we are hunting for: does the log carry player position?
    Zone,
    Combat,
    Loot,
    Experience,
    Kill,
    Death,
    Tell,
    Consider,   // result of a /consider — carries a rough difficulty
    System
}

/// <summary>How a /consider reads, roughly, from the difficulty tail of the con message.</summary>
public enum ConsiderDifficulty { Unknown, Trivial, Easy, Even, Hard, Suicidal }

/// <summary>The mob's ATTITUDE from the front of the con line ("scowls at you…", "regards you
/// indifferently…"). Separate from difficulty: attitude is faction, difficulty is level.</summary>
public enum ConsiderAttitude { Unknown, Scowls, Threatening, Dubious, Apprehensive, Indifferent, Amiable, Kindly, Warmly, Ally }

public sealed record LogEvent(
    DateTime? Stamp,
    LogEventKind Kind,
    string Text,
    double? X = null,
    double? Y = null,
    double? Z = null);

/// <summary>
/// Very small, deliberately loose classifier for EverQuest-style log lines.
/// The point of the spike is to discover what EQL actually emits, so this errs
/// toward flagging anything that *might* be position data rather than being strict.
///
/// Classic EQ /loc output looks like:
///   [Sun Aug 09 12:00:00 2026] Your Location is 1234.56, -789.01, 45.67
/// where the numbers are Y, X, Z. If EQL differs, the raw line is still shown and
/// the "Location" tag will simply not fire — which is itself a useful finding.
/// </summary>
public static class LogEventParser
{
    // [Sun Aug 09 12:00:00 2026] <message>
    private static readonly Regex Prefix = new(
        @"^\[(?<ts>[A-Za-z]{3}\s+[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4})\]\s?(?<msg>.*)$",
        RegexOptions.Compiled);

    /// <summary>The client's own position line. ANCHORED, like the loose one below and for the same
    /// reason: the chat guard catches anything anyone SAYS, but an emote carries no verb at all, and
    /// `/em Your Location is -1200, 400, 12` is one command. Once the timestamp is stripped, the
    /// client always prints this at the start of the line and a person never can.</summary>
    private static readonly Regex Loc = new(
        @"^\s*Your Location is\s+(?<a>-?\d+(?:\.\d+)?),\s*(?<b>-?\d+(?:\.\d+)?),\s*(?<c>-?\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Fallback for clients that word /loc differently: three signed decimals near the word.
    ///
    /// ANCHORED ON WORD BOUNDARIES, and the reason is a field failure that cost a character its
    /// life. The first version was `(?:loc|location|position|coords?)` with no boundaries, so "loc"
    /// matched inside **block**, **clock**, **locate**, **allocation** — and it was applied to every
    /// line of a log that carries the whole server's chat. Anyone typing three numbers in General
    /// gave this bot a new opinion about where its character was standing.
    ///
    /// ANCHORED AT THE START of the message too, which is the structural half of the fix. The
    /// timestamp has already been stripped by then, so the CLIENT's own output begins with the
    /// keyword while every line a person produced begins with a speaker's name. A blacklist of chat
    /// verbs can always be got round — an emote carries no verb at all — and this cannot.
    /// </summary>
    private static readonly Regex LocLoose = new(
        @"^\s*(?:your\s+)?(?:loc|location|position|coords?)\b\D{0,12}(?<a>-?\d+(?:\.\d+)?)[,\s]+(?<b>-?\d+(?:\.\d+)?)[,\s]+(?<c>-?\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Somebody TALKING, as opposed to the client reporting a fact.
    ///
    /// This is the guard that matters. A position is a statement the client makes about your
    /// character; every one of these lines is a statement a PERSON made, and a person may type
    /// anything at all — including three numbers after the word "loc", which is in fact the single
    /// most common thing anyone types in an EverQuest chat channel.
    ///
    /// The field evidence: a character parked in CAMP mode, which never moves, reported itself 42,
    /// then 1215, then 1030, then 883 units from its own anchor, walked "home" each time, fell in
    /// the water and drowned — on a server whose General channel is in the same log file. No amount
    /// of navigation logic survives being told the wrong place; this is where it has to stop.
    /// </summary>
    private static readonly Regex Chatter = new(
        @"(?:\btells\b|\btell\b|\bsays\b|\bsay\b|\bshouts\b|\bshout\b|\bauctions\b|\bauction\b"
        + @"|\btold\b|\bwhispers\b|\bOOC\b|\bout of character\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Zone = new(
        @"You have entered\s+(?<zone>.+?)\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LogEvent Parse(string rawLine)
    {
        string msg = rawLine;
        DateTime? stamp = null;

        Match p = Prefix.Match(rawLine);
        if (p.Success)
        {
            msg = p.Groups["msg"].Value;
            if (DateTime.TryParse(p.Groups["ts"].Value, out DateTime dt))
                stamp = dt;
        }

        // BOTH paths, not just the loose one. The first version guarded only the fallback on the
        // reasoning that "Your Location is" is the client's own phrasing and could not be forged —
        // which is wrong: `Soandso says, 'Your Location is 100, 200, 300'` is one paste away, and
        // someone pasting their loc into a channel to ask for a corpse drag is not a griefer, it is
        // Tuesday.
        bool spoken = Chatter.IsMatch(msg);
        Match loc = spoken ? Match.Empty : Loc.Match(msg);
        if (loc.Success &&
            double.TryParse(loc.Groups["a"].Value, out double a) &&
            double.TryParse(loc.Groups["b"].Value, out double b) &&
            double.TryParse(loc.Groups["c"].Value, out double c))
        {
            // EQ historically reports Y, X, Z in /loc. We keep raw a/b/c mapped as Y,X,Z.
            return new LogEvent(stamp, LogEventKind.Location, msg, X: b, Y: a, Z: c);
        }

        // The loose pattern is only ever tried on lines nobody SAID. The strict "Your Location is"
        // above needs no such guard — it is the client's own words and a player cannot forge the
        // whole phrase into a chat line without the chat markers this catches.
        Match locLoose = spoken ? Match.Empty : LocLoose.Match(msg);
        if (locLoose.Success &&
            double.TryParse(locLoose.Groups["a"].Value, out double la) &&
            double.TryParse(locLoose.Groups["b"].Value, out double lb) &&
            double.TryParse(locLoose.Groups["c"].Value, out double lc))
        {
            return new LogEvent(stamp, LogEventKind.Location, msg, X: lb, Y: la, Z: lc);
        }

        // Private tell only: "Soandso tells you, 'msg'" — exclude group/guild/raid channels.
        if (msg.Contains(" tells you,", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Tell, msg);

        // /consider lines (before combat, since con lines carry no damage words) — either the
        // difficulty tail or the faction-attitude phrasing marks one.
        if (ConsiderReading(msg) != ConsiderDifficulty.Unknown || AttitudeReading(msg) != ConsiderAttitude.Unknown)
            return new LogEvent(stamp, LogEventKind.Consider, msg);

        if (Zone.IsMatch(msg)) return new LogEvent(stamp, LogEventKind.Zone, msg);
        if (msg.Contains("points of damage", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains(" hit ", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains(" slash ", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Combat, msg);
        if (msg.Contains("You gain", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("experience", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Experience, msg);
        if (msg.Contains("--You have looted", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("You receive", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Loot, msg);
        if (msg.Contains("You have been slain", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("You died", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("You have been knocked", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Death, msg);
        if (msg.Contains("You have slain", StringComparison.OrdinalIgnoreCase) ||
            (msg.Contains("has been slain by you", StringComparison.OrdinalIgnoreCase)))
            return new LogEvent(stamp, LogEventKind.Kill, msg);

        return new LogEvent(stamp, LogEventKind.Other, msg);
    }

    /// <summary>
    /// Map the difficulty tail of a /consider line to a rough rating. EQL wording may differ
    /// slightly — the Hunt engine logs the raw con line so these can be tuned in-game.
    /// </summary>
    public static ConsiderDifficulty ConsiderReading(string msg)
    {
        string m = msg.ToLowerInvariant();
        if (m.Contains("flee if i were you") || m.Contains("certain death") || m.Contains("kill you") ||
            m.Contains("quite a challenge") || m.Contains("could be a challenge") || m.Contains("suicide"))
            return ConsiderDifficulty.Suicidal;
        if (m.Contains("take some effort") || m.Contains("fairly even") || m.Contains("would take him"))
            return ConsiderDifficulty.Hard;
        if (m.Contains("even fight")) return ConsiderDifficulty.Even;
        if (m.Contains("probably win") || m.Contains("could win") || m.Contains("would be easy") ||
            m.Contains("your victim"))
            return ConsiderDifficulty.Easy;
        if (m.Contains("looks harmless") || m.Contains("no match for you") || m.Contains("breaking a sweat") ||
            m.Contains("extremely easy") || m.Contains("decide whether to attack you or run"))
            return ConsiderDifficulty.Trivial;
        return ConsiderDifficulty.Unknown;
    }

    /// <summary>
    /// Read the FACTION attitude off the front of a /con line. Classic wording:
    /// "scowls at you, ready to attack" · "glares at you threateningly" · "glowers at you
    /// dubiously" · "looks your way apprehensively" · "regards you indifferently" ·
    /// "judges you amiably" · "kindly considers you" · "looks upon you warmly" ·
    /// "regards you as an ally".
    /// </summary>
    public static ConsiderAttitude AttitudeReading(string msg)
    {
        string m = msg.ToLowerInvariant();
        if (m.Contains("scowls at you") || m.Contains("ready to attack")) return ConsiderAttitude.Scowls;
        if (m.Contains("threateningly")) return ConsiderAttitude.Threatening;
        if (m.Contains("dubious")) return ConsiderAttitude.Dubious;
        if (m.Contains("apprehensive")) return ConsiderAttitude.Apprehensive;
        if (m.Contains("indifferent")) return ConsiderAttitude.Indifferent;
        if (m.Contains("amiabl")) return ConsiderAttitude.Amiable;
        if (m.Contains("kindly")) return ConsiderAttitude.Kindly;
        if (m.Contains("warmly")) return ConsiderAttitude.Warmly;
        if (m.Contains("as an ally")) return ConsiderAttitude.Ally;
        return ConsiderAttitude.Unknown;
    }

    /// <summary>
    /// Did this raw log line say the bard's melody/singing stopped? Used by the bard melody
    /// mode: the rotation fires ONCE and is only re-cast when one of these prints — a stun,
    /// a fizzled note, the song ending, or /melody stopping. Classic wording variants:
    /// "Your song ends abruptly." · "You miss a note, bringing your song to a close!" ·
    /// "You are stunned!" · "Your melody has ended." · "You can no longer sing." ·
    /// "You are too distracted to sing." · "You haven't recovered yet..."
    /// </summary>
    public static bool MelodyStopped(string rawLine)
    {
        return rawLine.Contains("song ends", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("miss a note", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("You are stunned", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("melody has ended", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("no longer sing", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("too distracted to sing", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("stops singing", StringComparison.OrdinalIgnoreCase);
    }
}
