using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EQAvatar.Spike.Config;

/// <summary>
/// Persisted settings, saved to %AppData%\EQAvatar\settings.json so choices survive between
/// sessions. Holds the global settings plus the chosen launch method. Per-launch-type
/// setting blocks will hang off this as the individual launchers are built.
/// </summary>
public sealed class AppSettings
{
    // Which launch method the user picked (null until they choose on first launch).
    public string? LaunchMethod { get; set; }

    // --- Global settings ---
    /// <summary>+/- percentage jitter applied to every timed action so nothing is metronomic.</summary>
    public double RandomVariancePercent { get; set; } = 15;

    /// <summary>Pause automation when a direct /tell arrives (default on).</summary>
    public bool PauseOnTell { get; set; } = true;
    public int TellPauseMinutes { get; set; } = 5;

    /// <summary>"PauseOnly" | "Preset" | "Ai" — what to do about the tell beyond pausing.</summary>
    public string TellResponseMode { get; set; } = "PauseOnly";
    public int TellResponseDelaySeconds { get; set; } = 15;
    public List<string> TellPresetResponses { get; set; } = new();

    // --- Mouse motion (aesthetic humanized movement) ---
    public double MouseSpeedPxPerSec { get; set; } = 900;   // base glide speed
    public double MouseArc { get; set; } = 0.12;            // sideways curve strength (0 = straight)
    public double MouseAngleJitterDegrees { get; set; } = 6; // random launch-angle wobble

    // --- Client Hub (centralized licensing + usage dashboard) ---
    /// <summary>
    /// App version reported on every check-in (shown on the dashboard), and the version the
    /// in-app updater compares against the release manifest.
    ///
    /// DERIVED FROM THE BUILD — NEVER HAND-EDITED. This was a hardcoded const, and nothing in
    /// the release pipeline touched it: CI builds the tag, publishes the zip, then commits
    /// latest.json with the tag's version. So shipping a release without separately remembering
    /// to edit this one line produced a build that under-reported its own version — it saw a
    /// newer manifest, updated, relaunched still claiming the old number, and updated again,
    /// forever. 0.9.31 shipped exactly that way. CI now passes -p:Version=&lt;tag&gt; to publish
    /// and this reads it back off the assembly, so the two cannot disagree.
    /// </summary>
    public static readonly string AppVersion = ResolveVersion();

    private static string ResolveVersion()
    {
        Assembly asm = typeof(AppSettings).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";

        int plus = v.IndexOf('+');                 // strip SourceLink's "+<commit sha>"
        if (plus > 0) v = v[..plus];
        string[] parts = v.Split('.');             // "0.9.32.0" -> "0.9.32"
        if (parts.Length == 4 && parts[3] == "0") v = string.Join('.', parts, 0, 3);
        return v;
    }

    // --- Auto-update (GitHub) ---
    public const string UpdateOwner = "elmoonfire";
    public const string UpdateRepo = "eq-avatar";
    /// <summary>Raw manifest the updater reads: {"version","url","notes"}. It points at the newest
    /// build zip committed to the repo, so no release-asset upload is needed.</summary>
    public const string UpdateManifestUrl = "https://raw.githubusercontent.com/elmoonfire/eq-avatar/main/latest.json";

    /// <summary>Bring EverQuest to the front when a run is started from the app, instead of making
    /// the user alt-tab into it before the runner will do anything. Applies to every role. Only
    /// ever done at the START of a run — losing focus mid-run is the panic brake, and a bot that
    /// grabbed focus back would be fighting the user for the mouse.</summary>
    public bool FocusGameOnStart { get; set; } = true;

    // --- The per-page consoles (Questing card, Auto Merge) ---
    /// <summary>Height, in pixels, of the in-page activity consoles (Questing, Auto Merge).
    /// Small by default because the card has other things to show — but when you are TRACKING
    /// something, five lines is not a console, it is a peephole. Dragged by the grip under the
    /// console and remembered here, so it doesn't collapse back on the next render.</summary>
    public double ConsoleHeight { get; set; } = 96;

    /// <summary>
    /// How many minutes a grind had been running each time the game window vanished under it.
    ///
    /// Hayden's idea, and a good one: a game that closes after a CONSISTENT interval is a timer —
    /// a power setting, an idle kick, an instance expiring — and one that closes at random is a
    /// crash. Nobody can hold those numbers in their head across a week of overnight runs, and the
    /// app is the only thing present for all of them. Kept to the last dozen; the pattern is in the
    /// spread, not in the history.
    /// </summary>
    public List<double> GameCloseMinutes { get; set; } = new();

    /// <summary>Make the roles narrate the numbers behind their decisions — match distances, click
    /// coordinates, the raw text an OCR read before anything parsed it. Off by default: these lines
    /// are voluminous enough to push the real narration out of the buffer, and they are only worth
    /// reading when something has already gone wrong.</summary>
    public bool ConsoleDetail { get; set; } = false;

    /// <summary>Deliberately the PICKER's own fallback rather than a second 40 written here. Two
    /// constants that must agree, and don't have to, are a defect waiting for someone to change one:
    /// the migration below compares against the old number, and if these ever drifted it would
    /// "upgrade" people onto a size nothing else uses.</summary>
    public const int DefaultIconSwatchPx = Ocr.CompassPickWindow.DefaultSwatch;

    /// <summary>
    /// The side, in real screen pixels, of the fixed square used to pick an inventory icon.
    ///
    /// Remembered so every pick is the SAME size. That is the point of the square: the size of the
    /// reference sets the stride the bot searches with and its contents are what everything is
    /// compared against, so a reference that changes size between picks makes every run a different
    /// experiment. 40 is a starting guess — the picker shows the number and the magnified pixels,
    /// and whatever the user settles on for their UI scale is kept.
    ///
    /// It was 32 through 0.10.34, which field use found too small for even the tightest slot, so
    /// `SwatchRev` below carries a stored 32 up to the new default exactly once. ⚠ Known cost, and
    /// it is not closable: a file written before `SwatchRev` existed carries no record of whether
    /// its 32 was the untouched default or a size the user deliberately settled on, so someone in
    /// the second group gets moved to 40 too. It is visible rather than silent — the picker's
    /// readout states the number and the magnified pixels show the fit — and one wheel notch puts
    /// it back. Any FUTURE default change must not lean on this: the marker now exists, so the
    /// migration for it can key off the revision alone and leave chosen values alone.
    /// </summary>
    public int IconSwatchPx { get; set; } = DefaultIconSwatchPx;

    /// <summary>Which generation of the swatch default this settings file has seen. Bumping a
    /// DEFAULT does nothing for an existing install — the old value is written in the file and wins
    /// forever — so a default worth changing needs a one-time migration, and a migration needs a
    /// marker or it re-runs and stamps on a size the user chose deliberately afterwards.
    ///
    /// It has to DEFAULT to zero: a settings file written before this field existed has no key for
    /// it, and System.Text.Json leaves an absent key at the property's initializer — so any non-zero
    /// default would skip the migration on exactly the files that need it. An install with no
    /// history instead gets the current number stamped on by <see cref="Fresh"/>, because there is
    /// nothing there to carry forward and a size it picks AFTERWARDS is a deliberate choice.</summary>
    public int SwatchRev { get; set; }

    private const int SwatchRevCurrent = 1;

    /// <summary>Where the picker's magnified view sits, as a fraction of the picture area, so it
    /// lands in the same place at any window size. Negative = never moved, park it out of the way.
    /// Kept because the useful position is next to whatever the user is picking, and on an ultrawide
    /// the default corner is most of a metre from the bags.</summary>
    public double LoupeNX { get; set; } = -1;
    public double LoupeNY { get; set; } = -1;

    public bool HubEnabled { get; set; } = true;

    /// <summary>Phone/web remote control: poll the hub for commands, post live status, and sync
    /// session history. Rides the same hub credentials; inert until a username is set.</summary>
    public bool RemoteControlEnabled { get; set; } = true;
    public string HubUrl { get; set; } = "https://eqavatar.ldtlan.com/hub/api.php";
    public string HubApiKey { get; set; } = "eqavatar-ldt-hub-7Yx2Qz";
    /// <summary>Character/account name this install checks in as (blank until the user sets it).</summary>
    public string HubUsername { get; set; } = "";
    /// <summary>Machine label; blank = use the real computer name.</summary>
    public string HubMachine { get; set; } = "";
    /// <summary>Seconds between automatic check-ins while auto check-in is on.</summary>
    public int HubCheckInSeconds { get; set; } = 120;

    /// <summary>Keep auto check-in on across restarts, and check in immediately on launch.</summary>
    public bool HubAutoCheckIn { get; set; } = false;

    // --- Character identity (shown on the web profile page) ---
    public string HubClass { get; set; } = "";
    public int HubLevel { get; set; } = 1;
    public string HubRace { get; set; } = "Human";
    public string HubServer { get; set; } = "Rivervale";

    // --- Grind "Hunt" engine (move → consider → fight → recover) ---
    // Hunt is ON by default now: pressing Start Grind should make the character actually go hunt,
    // not stand still. Uncheck Hunt for a pure stationary key-rotation.
    public bool HuntMode { get; set; } = true;
    public string HuntForwardKey { get; set; } = "W";        // hold to run forward
    public string HuntLeftKey { get; set; } = "A";           // strafe left
    public string HuntRightKey { get; set; } = "D";          // strafe right
    public string HuntBackKey { get; set; } = "S";           // back up (unstick / adjust)
    public string HuntTargetKey { get; set; } = "Tab";       // "target nearest NPC" — Tab by default
    public string HuntConsiderKey { get; set; } = "C";       // consider current target (can be mouse5)
    public string HuntLocKey { get; set; } = "";             // optional: a key bound to a /loc macro, tapped to refresh position
    public int HuntLocEverySeconds { get; set; } = 6;        // how often to fire the /loc key while roaming

    /// <summary>
    /// The key bound in game to AUTO ATTACK, used only as a fallback when nothing has swung.
    ///
    /// Blank by default and deliberately so: this is a TOGGLE in every EverQuest client, so
    /// pressing it when attack is already running turns it OFF. It is fired once per fight, and
    /// only after the log has gone quiet long enough to say that nothing is swinging — never
    /// speculatively.
    /// </summary>
    public string HuntAutoAttackKey { get; set; } = "";

    /// <summary>
    /// How long, at most, between a target being confirmed hostile and the first rotation key.
    ///
    /// The actual wait is random up to this, because a fixed pause between con and attack is the
    /// most machine-like thing a bot does — every engagement identical to the millisecond. Capped
    /// at two seconds: past that it stops being a pause before a fight and starts being a mob
    /// walking away from one.
    /// </summary>
    public int HuntEngageMaxMs { get; set; } = 1200;

    /// <summary>
    /// A thin picked strip of the game window that FLASHES while auto attack is running.
    ///
    /// The first version of this watched the little combat icon and was simply wrong about what it
    /// meant: the green circle and the red cross say whether the character is IN COMBAT, which is
    /// not the same question at all and does not move when attack is toggled. The flashing border
    /// around the unit frame is the only thing on screen that actually tracks auto attack.
    ///
    /// That changes the test from a comparison to an OBSERVATION OVER TIME. A flash cannot be seen
    /// in one snapshot — catch it mid-blink and it looks exactly like a border that is not
    /// flashing at all — so the region is sampled repeatedly for about a second and the question
    /// becomes "did this change while I watched". That needs no stored photograph, and it is immune
    /// to the things a stored photograph is fragile about: the window moving, the UI being rescaled,
    /// a different palette. Only the rectangle is remembered.
    /// </summary>
    public double AttackFlashX { get; set; }
    public double AttackFlashY { get; set; }
    public double AttackFlashW { get; set; }
    public double AttackFlashH { get; set; }

    /// <summary>
    /// How far the strip's REDNESS travelled when it was seen flashing — red minus the average of
    /// the other two channels, because the border is drawn over the moving 3D world and only its
    /// redness distinguishes it from the scenery behind it.
    ///
    /// DISPLAY ONLY. It used to size the run-time jump bar, and that was wrong: an amplitude is a
    /// RANGE across a whole window and the bar is a STEP between two consecutive looks, so a border
    /// that fades rather than snaps had every one of its edges fall just under a bar derived from
    /// its own brightness. Nothing decides anything from this now — it is shown on the Grind page
    /// so a strip that barely moves can be told from one that moves a long way.
    /// </summary>
    public double AttackFlashSeen { get; set; }

    /// <summary>
    /// What the LAST "check with attack ON" counted over a full window. Zero until one has run.
    ///
    /// The verdict that presses a key — "it isn't flashing" — is only sound for a border whose
    /// edges land inside the watch, and a pulse slower than the window is long produces none at all.
    /// Rather than assume the speed, the ON check counts them with attack definitely running.
    ///
    /// THE LAST ONE, not the best one. A running maximum records the luckiest look the strip ever
    /// managed and can never be revised downwards, so a strip that has drifted off the unit frame
    /// stays green for ever. Latest-wins means the readout describes what this strip did the last
    /// time it was checked — a claim the user can re-test in either direction, and the way to
    /// un-trust a stale strip without re-picking it.
    /// </summary>
    public int AttackFlashJumps { get; set; }

    /// <summary>
    /// What the LAST "check with attack OFF" counted on this strip. −1 until one has run.
    ///
    /// The control measurement, and the thing that catches a strip which pulses whatever the
    /// character is doing. Counting pulses with attack ON only ever asks "can this be seen"; it
    /// never asks "does it go quiet". A strip over something that flickers red regardless — a
    /// damage tint, a torch, an animated background — passes that question and then reads
    /// "flashing" for ever, silently disabling the whole feature behind a green tick.
    ///
    /// It has to be exactly zero, and it is latest-wins for the same reason as the other half: a
    /// running minimum would be the most permissive statistic available, so one lucky quiet moment
    /// on a strip that flickers all day would grant this half for ever. Zero rather than "under
    /// three" because the run's silent verdict is literal silence — what the control establishes is
    /// that this strip CAN be literally silent, which is a different claim from "it wasn't flashing
    /// much".
    /// </summary>
    public int AttackFlashQuiet { get; set; } = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AttackFlashSet => AttackFlashW > 0.0005 && AttackFlashH > 0.0005;
    /// <summary>Checked with attack ON and found to flash with room to spare, AND checked with
    /// attack OFF and found completely still. Either half alone proves nothing: a strip that never
    /// pulses cannot be read, and one that always pulses cannot be trusted to fall silent. Both
    /// halves come from checks the USER declared the state for — nothing infers which state a look
    /// was taken in from the look itself, because a weak ON look and a real OFF look are the same
    /// reading and that is the whole problem.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AttackFlashProven => AttackFlashSet
                                     && AttackFlashJumps >= Roles.HuntRole.SetupJumpsWanted
                                     && AttackFlashQuiet == 0;
    /// <summary>Hold right-mouse and pan the camera while running — human-like looking around.</summary>
    public bool HuntLookAround { get; set; } = true;
    public int HuntRunMsMin { get; set; } = 1200;            // forward-burst length range
    public int HuntRunMsMax { get; set; } = 2600;
    public int HuntRestSeconds { get; set; } = 8;            // blind pause between fights, used only when the vitals bars aren't set up
    /// <summary>Rest gating: with the HP/mana bars picked (see <see cref="Ocr.VitalsReader"/>), skip
    /// the rest entirely while both are above their thresholds, and when one isn't, rest until it
    /// recovers instead of burning a fixed timer. Falls back to <see cref="HuntRestSeconds"/> when
    /// no bars are configured.</summary>
    public bool RestGateEnabled { get; set; } = true;
    /// <summary>Rest below this much health (percent).</summary>
    public int RestHpPercent { get; set; } = 80;
    /// <summary>Rest below this much mana (percent). Set to 0 to ignore mana entirely.</summary>
    public int RestManaPercent { get; set; } = 80;
    /// <summary>Safety cap on a need-based rest, so a bad bar read can never park the bot forever.</summary>
    public int RestMaxSeconds { get; set; } = 180;
    /// <summary>With the target window picked, skip the /consider entirely when nothing is
    /// selected instead of conning into thin air every seek pass.</summary>
    public bool TargetGateEnabled { get; set; } = true;
    /// <summary>How much of the target window has to look like it did when a target was up before
    /// we believe one is selected (percent of fingerprint cells). Lower it if she misses real
    /// targets, raise it if she thinks the empty world is a target.</summary>
    public int TargetMatchPercent { get; set; } = 60;
    public int HuntMaxFightSeconds { get; set; } = 25;       // bail on a fight that never ends (keeps it roaming)
    /// <summary>Skip mobs whose consider reads as too hard (flee / challenge).</summary>
    public bool HuntSkipHardCons { get; set; } = true;

    // --- Grind targeting suite (0.9.14) ---
    /// <summary>Pet-style stance: "aggressive" (attack anything it finds), "defensive" (only
    /// fight back when attacked), "directive" (only mobs on the target list below).</summary>
    public string GrindStance { get; set; } = "aggressive";
    /// <summary>Only engage mobs whose /con attitude reads scowls or threateningly.</summary>
    public bool HuntHostileOnly { get; set; } = false;
    /// <summary>Tether the bot to where it started; it turns back when it wanders past the radius.</summary>
    public bool HuntTetherEnabled { get; set; } = false;
    public int HuntTetherRadius { get; set; } = 300;
    /// <summary>Directive target list — one mob name per line (fed from the Game Data page).</summary>
    public string GrindTargetMobs { get; set; } = "";
    /// <summary>Bard melody mode: cast the first rotation line once and let it sing; recast ONLY
    /// when the log shows the melody stopped (stun, fizzled note, song ends).</summary>
    public bool GrindBardMode { get; set; } = false;
    /// <summary>Cast/sing-only mode: the character fights purely with spells and songs, so the Hunt
    /// engine drops every melee correction — it never turns to face the mob and never steps closer
    /// mid-fight. All of that time goes into the rotation instead. When the log says the target is
    /// out of range or out of line of sight the fight is abandoned (see
    /// <see cref="GrindCastGiveUpSeconds"/>) and the seek phase goes and finds a reachable one.</summary>
    public bool GrindCastOnly { get; set; } = false;
    /// <summary>Cast/sing-only: seconds with nothing landing on the target before we give up on it.
    /// Two explicit "out of range / can't see" log lines abandon it sooner.</summary>
    public double GrindCastGiveUpSeconds { get; set; } = 8;
    /// <summary>Mouselook calibration: horizontal pixels of right-mouse drag per degree of turn.
    /// Self-tunes from measured /loc headings while the tether homing runs.</summary>
    public double HuntTurnPxPerDegree { get; set; } = 3.5;

    // --- Navigation aids (0.9.16) ---
    /// <summary>Keep the Levitation buff up: cast on role start and re-cast when the log says
    /// it wore off (or on the timer below). Floating clears pits, water and maze edges.</summary>
    public bool LevEnabled { get; set; } = false;
    /// <summary>Hotkey that casts Levitate (a spell gem or social).</summary>
    public string LevCastKey { get; set; } = "";
    /// <summary>Buff name to watch for in "Your X spell has worn off." lines.</summary>
    public string LevBuffName { get; set; } = "Levitate";
    /// <summary>Safety-net recast interval in minutes (0 = only recast on the worn-off line).</summary>
    public int LevRecastMinutes { get; set; } = 8;

    // --- Hunt modes (0.9.18) ---
    /// <summary>How the hunt moves: "hunt" (roam & destroy), "camp" (hold this spot — barely
    /// moves, kills what spawns), "zone" (stay inside the shape drawn on the Maps page),
    /// "waypoints" (patrol the route drawn on the Maps page).</summary>
    public string GrindMode { get; set; } = "hunt";
    /// <summary>Waypoint order: "sequence" (1→N then back, ping-pong) or "random".</summary>
    public string WaypointOrder { get; set; } = "sequence";
    /// <summary>The combat rotation text ("key,delayMs" lines) — persisted since 0.9.19.</summary>
    public string GrindRotationText { get; set; } = "";

    /// <summary>Remember the window between runs (0 = first launch, use XAML defaults).</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Highest level ever seen on this account — class changes reset the CURRENT level
    /// to 10, so the best-ever is tracked separately and only ever goes up.</summary>
    public int HubMaxLevel { get; set; }
    /// <summary>Licensing panel: keep scanning for the open inventory and read it automatically.</summary>
    public bool OcrAutoScan { get; set; } = false;

    // --- Follower role (second character follows + assists a leader) ---
    /// <summary>Leader character name this instance follows (exact in-game name).</summary>
    public string FollowerLeader { get; set; } = "";
    /// <summary>Re-issue /target + /follow this often while idle, so a lost follow self-heals.</summary>
    public int FollowerRefollowSeconds { get; set; } = 40;
    /// <summary>Join in automatically when the leader's swings/casts appear in the log.</summary>
    public bool FollowerAutoAssist { get; set; } = true;
    /// <summary>Human-ish pause before assisting once the leader engages.</summary>
    public int FollowerAssistDelayMs { get; set; } = 900;
    public int FollowerMaxFightSeconds { get; set; } = 30;
    /// <summary>No combat lines for this long = the fight is over; break off and re-follow.</summary>
    public int FollowerCombatLingerSeconds { get; set; } = 6;
    public int FollowerRestSeconds { get; set; } = 4;

    // --- Maps ---
    /// <summary>The EverQuest install folder (the one containing 'maps'). Blank = derived from LauncherPath.</summary>
    public string EqRootPath { get; set; } = "";

    // --- Launch (Command Center one-click launch) ---
    /// <summary>Path to the EQL launcher/LaunchPad exe. If set, the Launch button starts it before auto-login.</summary>
    public string LauncherPath { get; set; } = @"G:\EQ\LaunchPad.exe";

    /// <summary>Keep the app above other windows (currently including the game — Hayden prefers this
    /// on a two-monitor setup; single-monitor users can minimize or turn it off).</summary>
    public bool AlwaysOnTop { get; set; } = true;

    // --- Appearance ---
    /// <summary>Tooltip opacity (0.5–1.0). Applied to the custom light-blue tooltip.</summary>
    public double TooltipOpacity { get; set; } = 0.92;

    // --- persistence ---
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is null) return Fresh();
                s.Migrate();
                return s;
            }
        }
        catch { /* fall through to defaults */ }
        return Fresh();
    }

    /// <summary>Settings for an install with NO history — no file on disk, or one we couldn't read.
    /// Stamped at the current revision on purpose: every default it holds is already the newest one,
    /// so there is nothing to carry forward, and anything the user changes from here is a DELIBERATE
    /// choice that a migration must never overwrite. Without this a fresh install that picked the
    /// old default by hand would have it silently "upgraded" on the next launch.</summary>
    private static AppSettings Fresh() => new() { SwatchRev = SwatchRevCurrent };

    /// <summary>One-time carries for settings whose DEFAULT changed, for a file written before the
    /// change. Guarded by the revision marker so it runs once, and by the old value so it only
    /// touches a setting still sitting on the number it is replacing — which is NOT the same as
    /// "only touches values the user never chose"; see `IconSwatchPx` for why that one can't be
    /// told apart on a file older than the marker. Not saved here: the next Save writes it, and a
    /// migration that can't reach disk beats one that throws on startup.</summary>
    private void Migrate()
    {
        // A collection property initialised at construction is still NULL if the file on disk names
        // it with a null — a hand edit, a truncated write — because the deserialiser assigns over
        // the initializer. The list is appended to from a UI timer, outside any try, so that would
        // be a null reference on the dispatcher thread rather than a lost setting.
        GameCloseMinutes ??= new List<double>();
        if (SwatchRev < 1)
        {
            if (IconSwatchPx == 32) IconSwatchPx = DefaultIconSwatchPx;   // 32 fitted no slot
            SwatchRev = 1;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal for the spike */ }
    }

    /// <summary>Apply +/- variance to a millisecond duration. rng supplied by the caller.</summary>
    public int Vary(int ms, Random rng)
    {
        double v = RandomVariancePercent / 100.0;
        double factor = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * v;   // [1-v, 1+v]
        return Math.Max(1, (int)Math.Round(ms * factor));
    }
}
