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
        if (_grindTarget == IntPtr.Zero || IsWindow(_grindTarget)) return false;
        _grindTarget = IntPtr.Zero;
        return true;
    }

    private void AutoTargetEq()
    {
        if (GameWindowDied())
            GrindLogLine("The game window closed — it'll be re-detected when it's back.");
        if (_grindTarget != IntPtr.Zero)
        {
            if (GrindTargetLabel.Text is "—" or "") GrindTargetLabel.Text = "game targeted";
            return;
        }
        if (WindowFinder.GuessEverQuest() is { } w)
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
