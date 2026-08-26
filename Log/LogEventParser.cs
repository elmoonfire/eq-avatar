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

    /// <summary>
    /// Did a PERSON produce this line — asked WITHOUT the apostrophe test.
    ///
    /// <see cref="SpokenByAPlayer"/> refuses any line containing an apostrophe. That is deliberate
    /// over a POSITION, where the client's own wording never has one and the cost of believing a
    /// forgery is a drowned character. It is wrong everywhere the client's own wording DOES have
    /// one — every spell in this game is named after somebody, and half the mobs in Norrath carry
    /// an apostrophe in their name — so combat evidence uses this instead and leans on an ANCHOR
    /// for the part the apostrophe test was covering: anything anyone says begins with a speaker.
    /// </summary>
    public static bool SpokenAloud(string msg)
        => Chatter.IsMatch(msg) || msg.IndexOf('"') >= 0;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // OUR DAMAGE LANDING — and why it cannot use SpokenByAPlayer.
    //
    // SpokenByAPlayer treats ANY apostrophe as speech. That is right for a position (the client's
    // own "Your Location is …" never contains one, so the test is free paranoia over the input
    // that can drown the character) and it is CATASTROPHIC here, because every spell this game
    // owns is named after somebody:
    //
    //     A kerran `amir has taken 66 damage from your Fufil's Curtailing Chant V.
    //     A kerran `amir has taken 98 damage from your Tuyen's Chant of Frost V.
    //     A kerran `amir has taken 56 damage from your Denon's Disruptive Discord III.
    //
    // Three field lines from 08-26, three apostrophes. Gating those on SpokenByAPlayer refuses
    // 100% of them — the fix reads as if it works, ships, and the field report is "still 10-12
    // seconds a kill". It would also have DEMOTED the two patterns that were working, since a
    // resist line names the spell too.
    //
    // So the guard here is STRUCTURAL, not punctuational, and there are three independent parts:
    //
    //  1. ANCHORED. `^` — once the timestamp is stripped the client prints the victim first and a
    //     person cannot, because anything anyone says starts with a speaker.
    //  2. NO COMMA IN THE VICTIM. `[^,]` — every channel in this game prints "<name> says, '…'",
    //     so the comma sits between the speaker and anything they typed and the grammar below
    //     physically cannot span it. This is the chat guard, and unlike a verb blacklist it can
    //     never be tripped by a spell whose NAME contains a chat word.
    //  3. THE VICTIM IS NOT US. `(?!You\b|Your\b)` — "You were hit by non-melee for 100 points of
    //     damage" is a mob nuking US, and the old ungated Contains("hit by non-melee") counted
    //     that as our output: being nuked read as "our cast landed" and refreshed the give-up
    //     window. Fixed here as a side effect of asking the question properly.
    //
    // KNOWN AND ACCEPTED HOLE: an emote carries no speaker prefix and no comma, so
    // `/em has taken 66 damage from your song` prints a line this accepts. The cost is one
    // phantom increment — the bot believes a cast connected — which is the mildest thing on the
    // list of what a poisoned log can do here, and strictly better than the ungated Contains this
    // replaces. It is written down rather than hidden.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"A kerran `amir has taken 66 damage from your Fufil's Curtailing Chant V."</summary>
    private static readonly Regex SpellDamageOut = new(
        @"^\s*(?!You\b|Your\b)[^,]{1,64}? has taken \d+ damage from your\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"A rat was hit by non-melee for 42 points of damage." — victim-attributed, so the
    /// melee "did one of OUR lines print?" test never sees a caster's or a bard's output.</summary>
    private static readonly Regex NonMeleeOut = new(
        @"^\s*(?!You\b|Your\b)[^,]{1,64}? (?:was|is|were) hit by non-melee for \d+\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A resist still proves the cast REACHED the mob, which is exactly what the facing
    /// and reach logic wants to know — so it counts as our output landing.</summary>
    private static readonly Regex ResistOut = new(
        @"^\s*(?:Your target resisted\b|(?!You\b|Your\b)[^,]{1,64}? resisted your\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "You hit a rat for 42 points of magic damage by Shock of Blades." — a direct nuke, worded
    /// as a HIT with a damage TYPE and a "by &lt;spell&gt;" tail. It is our output, but it is not a
    /// weapon swing, and the "points of damage" test below deliberately misses it (this says
    /// "points of MAGIC damage"), so without this line a pure nuker was as invisible as the bard.
    /// Grammar copied from <c>CombatTracker.SpellRe</c> rather than invented.
    /// </summary>
    private static readonly Regex NukeOut = new(
        @"^\s*You hits? .{1,64}? for \d+ points of [\w-]+ damage by ",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "A kerran `amir is struck by YOUR Tuyen's Chant of Frost V for 98 points of non-melee
    /// damage." — the OTHER non-melee wording (<c>CombatTracker.DsRe</c>), which shares not one
    /// distinguishing phrase with "was hit by non-melee for". "by YOUR" is required literally so
    /// that "by Soandso's Thorns" — somebody else's damage shield on the same mob — cannot count.
    /// </summary>
    private static readonly Regex StruckByOurs = new(
        @"^\s*(?!You\b|Your\b)[^,]{1,64}? is \w+ by YOUR .{1,64}? for \d+ points? of non-melee damage",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Did OUR spell or song just do something to a mob? Give it the message with the timestamp
    /// stripped (<see cref="StripStamp"/>) — every pattern is anchored and a "[Wed Aug 26 …] "
    /// prefix defeats all of them.
    /// </summary>
    public static bool OurSpellLanded(string msg)
        => msg.IndexOf('"') < 0
           && (SpellDamageOut.IsMatch(msg) || NonMeleeOut.IsMatch(msg) || ResistOut.IsMatch(msg)
               || NukeOut.IsMatch(msg) || StruckByOurs.IsMatch(msg));

    /// <summary>
    /// Something happening TO us, in the client's passive voice. Every one of these starts with
    /// "You " and carries "points of damage", so the weapon test below would otherwise count being
    /// NUKED as swinging — and then suppress the auto-attack nudge in exactly the situation the
    /// nudge exists for: taking damage while dealing none.
    /// </summary>
    private static readonly Regex DamageToUs = new(
        @"^You (?:have taken|take |took |were |are |get |feel )",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Did OUR WEAPON move — hit, miss, dodge, parry, riposte, block, any weapon skill?
    ///
    /// Read from the line before anything classifies it, because the Combat classifier needs
    /// "points of damage" or the word "hit" or "slash" — so a MISS only registers for a slashing
    /// weapon. A monk punching, a paladin with a mace, a rogue piercing: auto attack running
    /// perfectly, every swing in the first exchange missing, not one line reaching the counter,
    /// and then the caller taps a TOGGLE and switches off the attack it was checking on.
    /// "You try to …" is the one phrase every miss outcome shares, for every weapon skill there is.
    ///
    /// Give it the STRIPPED message: the anchor is the whole chat guard here.
    /// </summary>
    public static bool OurWeaponMoved(string msg)
        => !SpokenAloud(msg)
           && msg.StartsWith("You ", StringComparison.Ordinal)
           && !DamageToUs.IsMatch(msg)
           && (msg.Contains("points of damage", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("You try to ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The verbs a PLAYER'S continuous attack prints: 1H/2H Slashing says "slash", 1H/2H Blunt
    /// says "crush", Piercing says "pierce", Hand to Hand says "hit", and ranged says "shoot" —
    /// a ranger on auto-fire is auto-attacking, and leaving archery out meant the fallback below
    /// fired on every one of their fights and blind-pressed the toggle at a bow that was working.
    ///
    /// EVERYTHING ELSE IN THE WEAPON LIST IS A HOTKEY — kick, bash, backstab, slam, strike, punch,
    /// claw. That distinction is the whole reason this exists. <see cref="OurWeaponMoved"/> counts
    /// them all, correctly, because a landed kick proves facing and reach as well as a swing does.
    /// But the auto-attack fallback asks a different question — "did the rotation fail to ENGAGE
    /// continuous attack?" — and a rotation that fires kick every fight answers that question with
    /// a kick. Counting it says attack is running, suppresses the fallback for the whole run, logs
    /// nothing, and the user grinds all night at hotkey-only damage behind a feature that looks on.
    ///
    /// A class whose auto-attack verb is not one of these four falls through to the border check,
    /// which is the existing safety net for exactly that: it looks at the attack indicator itself
    /// rather than inferring from silence, and it cancels the press when it can see attack is on.
    /// </summary>
    private static readonly Regex AutoSwing = new(
        @"^\s*You (?:try to )?(?:slash|crush|pierce|hit|shoot)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Did our CONTINUOUS attack swing — as opposed to any weapon moving at all? Give it
    /// the stripped message. See <see cref="AutoSwing"/> for why the two are not the same question.</summary>
    public static bool AutoAttackSwung(string msg) => OurWeaponMoved(msg) && AutoSwing.IsMatch(msg);

    /// <summary>
    /// Take the client's "[Wed Aug 26 09:04:33 2026] " off the front, if it is there.
    ///
    /// EVERY ANCHORED TEST OUTSIDE THIS FILE NEEDS THIS, and one of them didn't have it: HuntRole
    /// counted melee swings with raw.StartsWith("You ") against the line the watcher hands out,
    /// which is the file's line, stamp and all. Field log, 08-26:
    ///
    ///     [Wed Aug 26 08:46:54 2026] You slash kerran tiger spahi for 152 points of damage.
    ///
    /// It starts with '['. The melee counter had therefore never incremented once, on any client,
    /// for any user — so the auto-attack fallback saw "nothing has swung" in every fight it was
    /// enabled for and went to the border check every time.
    /// </summary>
    public static string StripStamp(string rawLine)
    {
        Match p = Prefix.Match(rawLine);
        // TrimStart, because Prefix eats `\]\s?` — at MOST ONE space. A client that writes two,
        // or a line indented for any reason, would leave every anchored test below failing
        // silently, which is the exact failure mode this whole commit exists to remove.
        return (p.Success ? p.Groups["msg"].Value : rawLine).TrimStart();
    }

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
