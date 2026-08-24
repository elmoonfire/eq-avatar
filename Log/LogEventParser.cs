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
    Afk,        // the client's own "You are now A.F.K." — the 30-minute warning before an idle kick
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

    /// <summary>
    /// Could this line be the CLIENT telling us where our own character is?
    ///
    /// A position is not like the other things read out of this log. Everything else the parser
    /// produces is advisory — a con, a zone, a tell — and being wrong about one costs a wasted
    /// pass. Being wrong about a POSITION inverts the bot: a camped character told it is a thousand
    /// units from its anchor walks a thousand units to "come back", and in the field it did exactly
    /// that, into water, and drowned while nobody was watching. Whatever anyone types anywhere in
    /// the game must be incapable of moving this character. That is the rule, and it is enforced
    /// HERE, once, rather than in each of the five places that consume a position.
    ///
    /// Three independent tests, and a line has to pass all of them:
    ///
    ///  1. NOTHING BEFORE THE KEYWORD. Enforced by the `^` in both patterns. Once the timestamp is
    ///     stripped the client always prints its position at the start of the line, and anything a
    ///     person produced starts with a speaker — a name, or "You". This one test is what an
    ///     emote cannot get round, and an emote carries no chat verb for a blacklist to find.
    ///  2. NOBODY IS SPEAKING. The chat verbs below. Redundant with (1) for every format known
    ///     today, which is the point: two independent reasons to refuse means one of them can be
    ///     wrong about a client this app has never seen.
    ///  3. NO QUOTE MARKS. Every channel in this game wraps what was said in quotes, and no line
    ///     the client prints about your own position contains one.
    /// </summary>
    public static bool CouldBeOurOwnPosition(string msg) => !SpokenByAPlayer(msg);

    /// <summary>
    /// Did a PERSON produce this line, rather than the client?
    ///
    /// The same question the position gate asks, under the name it should always have had — a log
    /// carries the whole server's chat, and any statement of fact read out of it has to know
    /// whether a human typed it. Every channel wraps speech in quotes and every channel line
    /// carries a verb; between them nothing anyone says reads as something the client said.
    /// </summary>
    public static bool SpokenByAPlayer(string msg)
        => Chatter.IsMatch(msg) || msg.IndexOf('\'') >= 0 || msg.IndexOf('"') >= 0;

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

        bool spoken = !CouldBeOurOwnPosition(msg);
        Match loc = spoken ? Match.Empty : Loc.Match(msg);
        if (loc.Success &&
            double.TryParse(loc.Groups["a"].Value, out double a) &&
            double.TryParse(loc.Groups["b"].Value, out double b) &&
            double.TryParse(loc.Groups["c"].Value, out double c))
        {
            // EQ historically reports Y, X, Z in /loc. We keep raw a/b/c mapped as Y,X,Z.
            return new LogEvent(stamp, LogEventKind.Location, msg, X: b, Y: a, Z: c);
        }

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

        // EVERYTHING BELOW IS A STATEMENT OF FACT, and the position parser's rule applies to all
        // of them: a fact read out of a log that carries the whole server's chat has to know a
        // human didn't type it. The field case that forced this outward from Location: somebody
        // said "thank you kindly" in General and the con reader announced a Kindly mob — twice,
        // on two different nights, from two different strangers being polite. A con can skip a
        // mob; the same hole under Death or Kill would stop a role or credit a kill on a stranger's
        // chat, so the gate goes HERE, over the lot, not into the classifier that happened to fire.
        if (spoken) return new LogEvent(stamp, LogEventKind.Other, msg);

        // The client's own A.F.K. flag — printed at the start of the line, like every fact the
        // client states about you. In the field this fired exactly 31.6 minutes before the server
        // dropped the connection and the client exited with END_GAME: it is not a status line, it
        // is the half-time whistle on an idle kick, and the unattended guard treats it as one.
        if (msg.StartsWith("You are now A.F.K.", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("You are no longer A.F.K.", StringComparison.OrdinalIgnoreCase))
            return new LogEvent(stamp, LogEventKind.Afk, msg);

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
