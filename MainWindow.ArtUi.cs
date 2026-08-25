using System;
using System.Windows;
using System.Windows.Controls;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The art-driven Grind page (partial class): binds every mascot scene, keeps the selectable
/// tiles in sync with the hidden legacy controls (so all existing code paths keep working),
/// swaps the tether face with the leash length, and hosts the ⓘ info popup that replaced the
/// wall of on-page text.
/// </summary>
public partial class MainWindow
{
    private bool _artInit;
    private bool _modeSync;
    private int _facePrev = -1;

    private void InitArtUi()
    {
        if (_artInit) return;
        _artInit = true;
        ArtCache.Bind(ArtStart, "ui-start.jpg");
        ArtCache.Bind(ArtStop, "ui-stop.jpg");
        ArtCache.Bind(ArtSave, "ui-save.jpg");
        ArtCache.Bind(ArtModes, "ui-modes-banner.jpg");
        ArtCache.Bind(ArtModeHunt, "ui-mode-hunt.jpg");
        ArtCache.Bind(ArtModeCamp, "ui-mode-camp.jpg");
        ArtCache.Bind(ArtModeZone, "ui-mode-zone.jpg");
        ArtCache.Bind(ArtModeWp, "ui-mode-waypoints.jpg");
        ArtCache.Bind(ArtTargeting, "ui-targeting.jpg");
        ArtCache.Bind(ArtStanceA, "ui-stance-aggressive.jpg");
        ArtCache.Bind(ArtStanceD, "ui-stance-defensive.jpg");
        ArtCache.Bind(ArtStanceDir, "ui-stance-directive.jpg");
        ArtCache.Bind(ArtTether, "ui-tether.jpg");
        ArtCache.Bind(ArtSettings, "ui-settings.jpg");
        ArtCache.Bind(ArtCompass, "ui-compass.jpg");
        ArtCache.Bind(ArtRotation, "ui-rotation.jpg");
        ArtCache.Bind(ArtVision, "ui-vision.jpg");           // the vitals + target-window panel
        ArtCache.Bind(ArtGhostLogo, "ghost-logo.png");       // the floating title-bar ghost
        TetherRope.ValueChanged += OnTetherRopeChanged;
        SyncModeTiles();
        SyncStanceUi();
        UpdateTetherFace();
        RotationText_Changed(this, null!);
    }

    // ---------------- mode tiles ↔ hidden GrindModeBox ----------------

    private void SyncModeTiles()
    {
        _modeSync = true;
        int i = GrindModeBox.SelectedIndex;
        RotOnlyBox.IsChecked = i == 4;
        (i switch { 1 => ModeTileCamp, 2 => ModeTileZone, 3 => ModeTileWp, _ => ModeTileHunt }).IsChecked = true;
        _modeSync = false;
    }

    private void ModeTile_Checked(object sender, RoutedEventArgs e)
    {
        if (_modeSync || GrindModeBox is null || RotOnlyBox is null) return;
        RotOnlyBox.IsChecked = false;
        GrindModeBox.SelectedIndex =
            ModeTileCamp.IsChecked == true ? 1
            : ModeTileZone.IsChecked == true ? 2
            : ModeTileWp.IsChecked == true ? 3 : 0;
    }

    private void RotOnly_Click(object sender, RoutedEventArgs e)
    {
        GrindModeBox.SelectedIndex = RotOnlyBox.IsChecked == true ? 4
            : ModeTileCamp.IsChecked == true ? 1
            : ModeTileZone.IsChecked == true ? 2
            : ModeTileWp.IsChecked == true ? 3 : 0;
    }

    /// <summary>Cast/sing-only is orthogonal to the mode tiles — it only changes what happens
    /// once a fight starts, so all this does is keep the rotation caption honest.</summary>
    private void CastOnly_Click(object sender, RoutedEventArgs e) => RotationText_Changed(this, null!);

    // ---------------- stances ----------------

    private void Stance_Checked(object sender, RoutedEventArgs e)
        => SyncStanceUi(navigate: _artInit && ReferenceEquals(sender, StanceDir));

    private void SyncStanceUi(bool navigate = false)
    {
        if (DirectiveRow is null || HostileSelBox is null) return;
        bool dir = StanceDir.IsChecked == true;
        DirectiveRow.Visibility = dir ? Visibility.Visible : Visibility.Collapsed;
        HostileSelBox.Visibility = dir ? Visibility.Collapsed : Visibility.Visible;
        if (navigate && string.IsNullOrWhiteSpace(TargetMobsBox.Text))
        {
            NavData.IsChecked = true;
            ShowToast("Pick mobs with '☠ Target with Grind'");
        }
    }

    // ---------------- tether rope + faces ----------------

    private void OnTetherRopeChanged()
    {
        TetherLabel.Text = $"{(int)TetherRope.Value} units";
        _settings.HuntTetherRadius = (int)TetherRope.Value;
        UpdateTetherFace();
        PushTetherToMaps();
    }

    /// <summary>10 expressions across the slider's travel: a very short leash worries her; at
    /// full length she looks like she just won the lottery.</summary>
    private void UpdateTetherFace()
    {
        if (ArtTetherFace is null) return;
        double v = TetherRope.Value;
        double pos = v <= 50 ? (v - 10) / 40.0 * 0.18 : 0.18 + (v - 50) / 1450.0 * 0.82;
        int idx = Math.Clamp((int)(pos * 10), 0, 9);
        if (idx == _facePrev) return;
        _facePrev = idx;
        ArtCache.Bind(ArtTetherFace, $"ui-tether-face-0{idx}.jpg");
    }

    // ---------------- rotation placeholder + summary ----------------

    private void RotationText_Changed(object sender, TextChangedEventArgs e)
    {
        if (RotationHint is null || RotationSummary is null) return;
        bool empty = string.IsNullOrWhiteSpace(GrindRotation.Text);
        RotationHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) { RotationSummary.Text = "empty — click to set up"; return; }
        int lines = 0;
        foreach (string l in GrindRotation.Text.Split('\n'))
            if (l.Trim() is { Length: > 0 } t && !t.StartsWith("#")) lines++;
        RotationSummary.Text = $"{lines} line(s)" + (BardBox?.IsChecked == true ? " · bard melody" : "")
                             + (CastOnlyBox?.IsChecked == true ? " · cast/sing only" : "");
    }

    private void GrindData_Click(object sender, RoutedEventArgs e) => NavData.IsChecked = true;

    // ---------------- header: auto target + info ----------------

    /// <summary>Find the game window without a click — runs whenever the Grind page opens.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);

    /// <summary>True when the targeted game window has DIED since it was detected — which also
    /// clears the stale handle, so every "is the game targeted?" check downstream asks a real
    /// question again. A window handle is a claim about the past: the game detected at launch
    /// stays "detected" forever unless someone actually asks Windows whether it still exists,
    /// and nothing did — so a closed game left the app insisting it was open, refusing to help
    /// launch it again until the app itself was restarted.</summary>
    private bool GameWindowDied()
    {
        if (_grindTarget == IntPtr.Zero) return false;
        if (IsWindow(_grindTarget))
        {
            // REMEMBERED WHILE IT IS STILL ANSWERABLE. Once the handle is dead there is no route
            // from it back to the process, so the one question worth asking about a vanished game
            // window — did the client die, or did it just rebuild its window? — can only be asked
            // if the process id was written down beforehand.
            if (_pidFor != _grindTarget) { _pidFor = _grindTarget; _gamePid = WindowFinder.OwnerPid(_grindTarget); }
            return false;
        }
        // A SENTINEL, not zero. _gamePid is only filled in on a tick where the window was alive,
        // so a window that dies within one heartbeat of being targeted leaves it at 0 — and the
        // poll below would then silently skip a verdict the line above has already promised.
        // CAPTURED HERE, not at the one call site that happens to be the UI tick. This method is
        // a ONE-SHOT consumed by whichever caller reaches it first — the tick, AutoTargetEq, a
        // page navigation, a Tools button — and only this line is on every one of those paths.
        // Recording the running role from the tick alone left a stale answer behind: a window
        // REBUILD (which EQ does on every death loading screen) recorded "hunt", nothing cleared
        // it, and hours later a game the user closed by hand was recovered as though a run had
        // been going.
        RememberRunningRoleForRecovery();
        _diedPid = _gamePid > 0 ? _gamePid : -1;
        _diedAt = DateTime.UtcNow;
        _grindTarget = IntPtr.Zero;
        _pidFor = IntPtr.Zero;
        _gamePid = 0;
        return true;
    }

    private IntPtr _pidFor = IntPtr.Zero;
    private int _gamePid, _diedPid;
    private DateTime _diedAt;
    /// <summary>When the current grind/hunt started, or null. Only for the close-time pattern.</summary>
    private DateTime? _runStartedAt;
    /// <summary>The last moment the GAME was the foreground window. Hayden's theory is that the
    /// client is being closed by something that happens while it is not focused and the character
    /// is therefore idle — this is the measurement that tests it, and it costs one tick check.</summary>
    private DateTime _gameFocusedAt = DateTime.UtcNow;

    /// <summary>
    /// Which of the two things just happened, in words — ASKED A FEW SECONDS LATE, on purpose.
    ///
    /// "The game window closed" is a symptom with two completely different causes and two
    /// completely different fixes, and saying only the symptom sends someone hunting through logs
    /// for an answer the app already has. If the PROCESS is gone the client exited or crashed. If
    /// it is still running, nothing closed: the client destroyed and rebuilt its window, which EQ
    /// does on a resolution change, a full-screen toggle, and the loading screen after a death.
    ///
    /// THE DELAY IS THE WHOLE POINT. Windows destroys a process's windows BEFORE the process
    /// terminates, and the watchdog notices within one 300 ms tick — so a client that is crashing
    /// right now still reports HasExited == false, and asking immediately gives the confident wrong
    /// answer in exactly the case this was written for: "nothing crashed, I'll re-attach shortly",
    /// about a game that is gone and a re-attach that will never come. Waiting past the teardown
    /// costs one line arriving a few seconds later and turns a guess into an answer.
    /// </summary>
    private const int DeathVerdictMs = 3000;

    private void PollGameDeathVerdict()
    {
        if (_diedPid == 0) return;
        if ((DateTime.UtcNow - _diedAt).TotalMilliseconds < DeathVerdictMs) return;
        int pid = _diedPid;
        _diedPid = 0;                                  // once, and never with a stale id
        if (pid < 0)
        {
            string lost = "That window closed before I'd noted which process owned it, so I can't tell you whether "
                        + "the game itself is gone or only rebuilt its window.";
            GrindLogLine(lost); LoginLogLine(lost);
            return;
        }
        bool alive = WindowFinder.ProcessAlive(pid);
        string note = CloseTimingNote(alive) + " " + (alive
            ? $"The game's process (pid {pid}) is still running, so the client did NOT close — it destroyed and "
              + "rebuilt its window, which EQ does on a resolution or full-screen change and on the loading screen "
              + "after a death. I'll re-attach on my own as soon as the new window appears."
            // THE CLIENT'S OWN LOG ANSWERS THIS, so stop telling the user to go and read Event
            // Viewer. Three closes read as "exited or crashed" under the old wording and had
            // three different causes; dbg.txt distinguished all three on the first try.
            : $"The game's process (pid {pid}) is gone as well. Reading its own dbg.txt: "
              + Login.CloseReason.FromLogFolder(LogFolderBox.Text.Trim()).Say + ".");
        GrindLogLine(note.Trim());
        LoginLogLine(note.Trim());
        if (alive) ForgetRecoveryRole();     // a rebuilt window is not a close; nothing to recover from
        else ConsiderRecovery();
    }

    /// <summary>
    /// How long the run had been going, how long the game had been unfocused, and whether this
    /// close looks like a TIMER or like a crash.
    ///
    /// A person cannot hold a week of overnight run-lengths in their head, and the app is the only
    /// thing awake for all of them. Three closes within a few minutes of each other is a schedule —
    /// a sleep setting, an idle kick, an instance expiring — and closes scattered across an hour
    /// are a crash. Those two have nothing in common except the symptom, so guessing between them
    /// is exactly the wrong thing to do when the numbers are free.
    /// </summary>
    private string CloseTimingNote(bool stillAlive)
    {
        // Belt and braces: the clock AND a run that is genuinely still going. The clock alone has
        // several ways to be stale, and each new one would be discovered as a nonsense number in
        // the very dataset this exists to keep clean.
        if (_runStartedAt is not DateTime started
            || !(_grind is { Running: true } || _hunt is { Running: true })) return "";
        double mins = (DateTime.UtcNow - started).TotalMinutes;
        double idle = (DateTime.UtcNow - _gameFocusedAt).TotalMinutes;

        // A REBUILT window is not a close, and must not go in the history. The very next sentence
        // this method's caller prints tells the user a rebuild is nothing to worry about; recording
        // it as a close would poison the one dataset that answers whether the real closes follow a
        // schedule. The narrative half still prints — "that was 40 minutes in" is true either way.
        if (stillAlive)
            // The clock is deliberately LEFT RUNNING here. A rebuild is not the end of anything —
            // the grind is still going and the real close, when it comes, is the one worth timing.
            // Clearing it above the return meant a resolution change or a death loading screen
            // silently swallowed the measurement for that whole run.
            return $"That was {mins:0} minutes into the grind"
                 + (idle > 1 ? $", and the game had not been the focused window for {idle:0} of them." : ".");

        _runStartedAt = null;
        List<double> hist = _settings.GameCloseMinutes;
        hist.Add(Math.Round(mins, 1));
        if (hist.Count > 12) hist.RemoveRange(0, hist.Count - 12);
        // The WHY beside the minutes. 08-24 proved the minutes alone mislead: a close 217 minutes
        // into the grind was really a close ~62 minutes after input stopped — a cause tag is what
        // keeps a crash-after-death from being averaged with a genuine timer.
        // SCOPED TO THIS RUN, and that is a correction of 0.10.55. The guard's window was three
        // hours, so the 08-25 close — a server patch — was reported as "death 165m before", from a
        // death in an entirely different session earlier that evening. A cause that predates the
        // run cannot be the cause of the run ending.
        // ONE call, so the clause and the age it is checked against describe the same event.
        // `mins` is the run length; _runStartedAt has already been cleared two lines above, which
        // is exactly the kind of ordering that made the original bug easy to write.
        (string cause, double? causeAge) = GuardCloseCauseWithAge();
        if (causeAge is double age && age > mins)
            cause = "no death or afk this run";
        List<string> causes = _settings.GameCloseCauses;
        if (!string.IsNullOrEmpty(cause))
        {
            causes.Add(cause);
            if (causes.Count > 12) causes.RemoveRange(0, causes.Count - 12);
        }
        try { _settings.Save(); } catch { /* a diagnostic must never break a run */ }

        string s = $"That was {mins:0} minutes into the grind";
        if (idle > 1) s += $", and the game had not been the focused window for {idle:0} of them";
        s += ".";
        if (!string.IsNullOrEmpty(cause) && !cause.StartsWith("no death or afk", StringComparison.Ordinal))
            s += $" Before the close I saw: {cause} — that, not the run length, is the number that "
               + "matters, because the measured kill chain is input-stops → A.F.K. → ~30 min → kick → exit.";
        if (hist.Count >= 3)
        {
            double lo = double.MaxValue, hi = 0, sum = 0;
            foreach (double v in hist) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v; }
            double avg = sum / hist.Count;
            s += $" The last {hist.Count} closes came at {string.Join(", ", hist.ConvertAll(v => $"{v:0}"))} minutes"
               + (hi - lo <= Math.Max(3, avg * 0.15)
                   // A tight spread is the interesting answer, and it points somewhere a crash does
                   // not: nothing crashes on a schedule.
                   ? $" — that is CONSISTENT, around {avg:0} minutes every time, which is a timer rather than a "
                     + "crash. Worth checking Windows' sleep and USB-suspend settings, and whether the zone or "
                     + "instance has a lifetime."
                   : ", which is scattered rather than regular — more like a crash than a timer.");
        }
        else s += $" I'll keep the times; after {3 - hist.Count} more I can say whether they're regular.";
        return s;
    }

    private void AutoTargetEq()
    {
        if (GameWindowDied())
            GrindLogLine("The game window closed — it'll be re-detected when it's back. Checking in a moment "
                       + "whether the game itself is gone or has only rebuilt its window.");
        if (_grindTarget != IntPtr.Zero)
        {
            if (GrindTargetLabel.Text is "—" or "") GrindTargetLabel.Text = "game targeted";
            return;
        }
        // IsGameWindow, same as AutoLogin's own guard, because GuessEverQuest's title fallback
        // matches the LaunchPad too — its window also names the game. Without this, the 3-second
        // seek latches onto the LAUNCHER during every app-driven launch (the exact window where
        // nothing is targeted yet), announces "game detected" over the login narration, and then
        // narrates a false "game window closed" when the LaunchPad hands off and exits.
        if (WindowFinder.GuessEverQuest() is { } w && WindowFinder.IsGameWindow(w.Handle))
        {
            _grindTarget = w.Handle;
            GrindTargetLabel.Text = $"{w.ProcessName} · {w.Title}";
            GrindLogLine($"Game auto-detected: {w.ProcessName} \"{w.Title}\".");
        }
        else GrindTargetLabel.Text = "game not found — launch EQ, then click ◎";
    }

    private void GrindInfo_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Hex("#C6D2DE"),
            FontSize = 12.5,
            LineHeight = 19,
            Margin = new Thickness(18),
            Text = GrindInfoText,
        };
        var win = new Window
        {
            Title = "How the Grind role works",
            Owner = this,
            Width = 660, Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Hex("#0B0F18"),
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        win.ShowDialog();
    }

    private const string GrindInfoText =
@"THE SHORT VERSION
Press START and your avatar works the selected hunting mode: it finds a target, considers it, runs your combat rotation until the mob drops, rests, and goes again. EQL must be the focused window — input only fires while the game has focus, and F12 stops everything from anywhere.

MODES
• HUNT roams the area you've explored: forward bursts with strafes and human-like look-arounds, target nearest NPC, /consider, fight, rest, repeat.
• CAMP holds the exact spot where you press Start. It never strides away — it turns in place scanning for respawns and only shuffles back if a fight pushes it out. Built for camps ringed by hazards.
• HUNTING ZONE hunts only inside the circle / rectangle / polygon you draw on the Maps page (plan tools in the map toolbar). Cross the line and it walks back in.
• WAYPOINTS patrols the route you draw on the Maps page — closely but never exactly: every leg aims a little off the marker, speeds vary, and it sometimes pauses like a player checking the area. Sequence mode ping-pongs 1→N→1; Random hops like someone with no plan.

TARGETING
Aggressive engages whatever the mode finds. Defensive holds until something attacks first. Directive only ever fights mobs on your list — build it on the Game Data page with '☠ Target with Grind'. Under Aggressive/Defensive you can further require hostile /con reads (scowls / glares threateningly) so faction accidents can't happen.

TETHER
Leashes the bot to where the run started (the first /loc). It curves back inside before the line, and walks STRAIGHT home if it crosses — heading comes from the compass when calibrated, else from /loc movement. The circle draws on the Maps page and the in-game overlay. Slider: 10-unit steps below 50 for tight camps, 50-unit steps above.

COMBAT ROTATION
One key per line, delay optional: '4,1400' presses 4 then waits 1.4s; a bare '4' waits 3.2s (the 3s global cooldown + 200ms latency). Keys 0-9, A-Z, F1-F24, Tab, Home/End/PgUp/PgDn, mouse1-5; a single in-game macro key is one line; '#' starts a comment. BARD MELODY MODE (checkbox in this section): the FIRST line is your /melody hotkey — it fires once and keeps singing, recast only when the log says the melody stopped (stun, fizzled note, song end).

KEYBINDS + /loc
Set your movement keys in Grind Settings. Bind 'target nearest NPC' in-game to the target key (Tab by default; mouse buttons work). Bind a key to a macro whose LAST line is /loc — the bot taps it to keep its position live (essential for tether, camp and waypoints). Tip: make a new chat tab and filter Other (Misc) into it so /loc doesn't spam your main chat.

COMPASS
Pick the compass region once (make the compass fully opaque in-game), then Spin-calibrate: the bot turns up to two circles and fingerprints the strip, measuring exactly how many mouse-pixels make 360°. After that every turn is compass-guided and exact. The chip on the section header says whether the calibration is good or needs a redo.

LEVITATE
Give it your Levitate hotkey and buff name: it casts at the start, recasts when the buff drops, and rides the view just above the horizon so it floats over pits and water. If it still falls in, recovery mode looks up, heads back toward the last good ground, climbs ladders it bumps and swims out.

SAFETY
Auto-pauses the instant EQ loses focus; F12 is a global stop; stop-on-death halts the run. Every decision is logged to %AppData%\EQAvatar\logs for debugging.";
}
