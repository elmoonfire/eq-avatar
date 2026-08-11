using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>App version reported on every check-in (shown on the dashboard). Also the version
    /// the in-app updater compares against the newest GitHub release tag.</summary>
    public const string AppVersion = "0.9.16";

    // --- Auto-update (GitHub) ---
    public const string UpdateOwner = "elmoonfire";
    public const string UpdateRepo = "eq-avatar";
    /// <summary>Raw manifest the updater reads: {"version","url","notes"}. It points at the newest
    /// build zip committed to the repo, so no release-asset upload is needed.</summary>
    public const string UpdateManifestUrl = "https://raw.githubusercontent.com/elmoonfire/eq-avatar/main/latest.json";

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
    /// <summary>Hold right-mouse and pan the camera while running — human-like looking around.</summary>
    public bool HuntLookAround { get; set; } = true;
    public int HuntRunMsMin { get; set; } = 1200;            // forward-burst length range
    public int HuntRunMsMax { get; set; } = 2600;
    public int HuntRestSeconds { get; set; } = 8;            // pause between fights (see note: HP/mana isn't in the log)
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
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
