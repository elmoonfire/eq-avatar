using System;
using System.Windows;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike;

/// <summary>
/// Vitals wiring (partial class). EQ's log has no health or mana line, so the only honest source
/// is the HUD itself: the user drags a box over each bar once while full, and the reader learns
/// what "full" looks like. The Hunt role uses it to decide whether a rest is needed at all, and
/// when it is, to stand up again as soon as the bars recover.
/// </summary>
public partial class MainWindow
{
    private VitalsReader? _vitalsSvc;

    private VitalsReader VitalsSvc => _vitalsSvc ??= new VitalsReader(() =>
        _grindTarget != IntPtr.Zero ? _grindTarget : (WindowFinder.GuessEverQuest()?.Handle ?? IntPtr.Zero));

    /// <param name="live">Read the bars right now. Off during startup, where two screen grabs plus
    /// a window enumeration on the UI thread would stall the launch for no benefit.</param>
    private void UpdateVitalsStatus(bool live = true)
    {
        if (VitalsStatus is null) return;
        VitalsReader v = VitalsSvc;
        VitalsStatus.Text = !v.Ready ? "not set — stand at full, then pick each bar"
                          : live ? v.Describe()
                          : "bars set — press 'Test read' to check them";
        if (VitalsVerdict is null || VitalsVerdictText is null) return;
        bool both = v.Hp.Set && v.Mana.Set;
        // The chip has to reflect what actually governs resting, not just whether bars exist:
        // with the gate unticked she's back on the blind timer however good the bars are.
        if (v.Ready && _settings.RestGateEnabled)
        {
            VitalsVerdict.Background = Hex("#12261B");
            VitalsVerdict.BorderBrush = Hex("#2C8C55");
            VitalsVerdictText.Foreground = Hex("#B6F2C9");
            VitalsVerdictText.Text = both ? "reading health + mana" : v.Hp.Set ? "health only" : "mana only";
        }
        else
        {
            VitalsVerdict.Background = Hex("#2A2410");
            VitalsVerdict.BorderBrush = Hex("#7A6320");
            VitalsVerdictText.Foreground = Hex("#FFE1A6");
            VitalsVerdictText.Text = v.Ready ? "gate off — resting on a timer" : "not set — resting on a timer";
        }
    }

    /// <summary>The gate checkbox carries IsChecked="True" in XAML, so this fires while the window
    /// is still being built — the null guard is what keeps that from taking the app down.</summary>
    private void VitalsGate_Changed(object sender, RoutedEventArgs e)
    {
        if (RestHpBox is null || VitalsStatus is null) return;
        _settings.RestGateEnabled = RestGateBox.IsChecked == true;
        UpdateVitalsStatus(live: false);
    }

    private void VitalsPickHp_Click(object sender, RoutedEventArgs e) => PickBar(mana: false);
    private void VitalsPickMana_Click(object sender, RoutedEventArgs e) => PickBar(mana: true);

    private void PickBar(bool mana)
    {
        string what = mana ? "MANA" : "HEALTH";
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        { GrindLogLine("No game window to capture — Target EverQuest first (and keep it on screen)."); return; }

        var dlg = new CompassPickWindow(frame,
            $"Pick the {what.ToLowerInvariant()} bar",
            $"Drag a box INSIDE your {what} bar — just the coloured part, not the frame or the numbers — then press Enter. "
            + $"Your {what.ToLowerInvariant()} must be FULL right now, because this is what she'll measure against.")
        { Owner = this };
        if (dlg.ShowDialog() != true) { UpdateVitalsStatus(); return; }

        // Learn from the frame she drew on, not from a fresh capture — the picker was covering
        // the game a moment ago and the desktop underneath repaints on its own schedule.
        if (!VitalsSvc.SetBar(mana, dlg.NX, dlg.NY, dlg.NW, dlg.NH, frame))
        { GrindLogLine($"Couldn't read the {what.ToLowerInvariant()} bar — try again."); UpdateVitalsStatus(); return; }

        if (VitalsReader.TooSquare(mana ? VitalsSvc.Mana : VitalsSvc.Hp))
            GrindLogLine($"That {what.ToLowerInvariant()} box is nearly square, so she can't tell which way the bar drains — "
                       + "re-pick a box that's clearly longer along the bar than across it.");

        double? read = mana ? VitalsSvc.ManaFraction() : VitalsSvc.HealthFraction();
        GrindLogLine($"{what} bar saved — reading {(read is double r ? $"{r * 100:0}%" : "nothing")} live right now."
                   + (read is double x && x < 0.9
                        ? " That should be ~100% if you were full — re-pick with a tighter box inside the coloured part."
                        : ""));
        UpdateVitalsStatus();
    }

    private void VitalsTest_Click(object sender, RoutedEventArgs e)
    {
        VitalsReader v = VitalsSvc;
        if (!v.Ready) { GrindLogLine("No bars picked yet — press 'Pick HP bar' while you're at full health."); return; }
        GrindLogLine("Vitals read: " + v.Describe() + " — compare that against the game before trusting the rest gate.");
        UpdateVitalsStatus();
    }

    // ---------------- target window ----------------

    private void TargetPick_Click(object sender, RoutedEventArgs e)
    {
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        { GrindLogLine("No game window to capture — Target EverQuest first (and keep it on screen)."); return; }

        var dlg = new CompassPickWindow(frame,
            "Pick the target window",
            "Drag a box over your TARGET window — frame, name and health bar together, not just the bar. "
            + "You must have a mob targeted right now: this is the picture she'll compare against.")
        { Owner = this };
        if (dlg.ShowDialog() != true) { UpdateTargetStatus(); return; }

        if (!VitalsSvc.SetTargetBox(dlg.NX, dlg.NY, dlg.NW, dlg.NH, frame))
        { GrindLogLine("Couldn't read the target window — try again."); UpdateTargetStatus(); return; }

        double m = VitalsSvc.TargetMatch();
        GrindLogLine($"Target window saved — matching {(m < 0 ? "?" : $"{m * 100:0}")}% with your target up. "
                   + $"Now CLEAR your target and press 'Test target read': it should fall well below {_settings.TargetMatchPercent}%. "
                   + "If it doesn't, your UI keeps an empty target window on screen — put the match % in the gap between the two readings.");
        UpdateTargetStatus();
    }

    private void TargetTest_Click(object sender, RoutedEventArgs e)
    {
        if (!VitalsSvc.HasTargetBox) { GrindLogLine("Pick the target window first (with a mob targeted)."); return; }
        ApplyVitalsFields();                                  // honour a threshold typed but not yet saved
        double m = VitalsSvc.TargetMatch();
        // Use the SAME clamped threshold the role uses, so this verdict can never disagree with it.
        double need = Roles.HuntRole.TargetNeed(_settings);
        GrindLogLine(m < 0
            ? "Couldn't read the target window — is the game on screen?"
            : $"Target window matches {m * 100:0}% — she reads that as {(m >= need ? "TARGETED" : "nothing targeted")} "
              + $"(threshold {need * 100:0}%).");
        UpdateTargetStatus();
    }

    private void TargetGate_Changed(object sender, RoutedEventArgs e)
    {
        if (TargetStatus is null || TargetMatchBox is null) return;
        _settings.TargetGateEnabled = TargetGateBox.IsChecked == true;
        UpdateTargetStatus();
    }

    private void UpdateTargetStatus()
    {
        if (TargetStatus is null) return;
        TargetStatus.Text = !VitalsSvc.HasTargetBox ? "not set — she'll consider to find out, as before"
                          : _settings.TargetGateEnabled ? "set — considering only when targeted"
                          : "set, but the gate is off";
    }

    /// <summary>Read the rest-gating boxes into settings (called with the rest of ApplyHuntFields).</summary>
    private void ApplyVitalsFields()
    {
        _settings.RestGateEnabled = RestGateBox.IsChecked == true;
        _settings.TargetGateEnabled = TargetGateBox.IsChecked == true;
        if (int.TryParse(RestHpBox.Text.Trim(), out int hp)) _settings.RestHpPercent = Math.Clamp(hp, 0, 100);
        if (int.TryParse(RestManaBox.Text.Trim(), out int mp)) _settings.RestManaPercent = Math.Clamp(mp, 0, 100);
        if (int.TryParse(RestMaxBox.Text.Trim(), out int cap)) _settings.RestMaxSeconds = Math.Clamp(cap, 5, 3600);
        if (int.TryParse(TargetMatchBox.Text.Trim(), out int tm)) _settings.TargetMatchPercent = Math.Clamp(tm, 10, 100);
    }

    /// <summary>Fill the rest-gating boxes from saved settings on load.</summary>
    private void InitVitalsUi()
    {
        RestGateBox.IsChecked = _settings.RestGateEnabled;
        RestHpBox.Text = _settings.RestHpPercent.ToString();
        RestManaBox.Text = _settings.RestManaPercent.ToString();
        RestMaxBox.Text = _settings.RestMaxSeconds.ToString();
        TargetGateBox.IsChecked = _settings.TargetGateEnabled;
        TargetMatchBox.Text = _settings.TargetMatchPercent.ToString();
        UpdateVitalsStatus(live: false);
        UpdateTargetStatus();
    }

    // ---------------------------------------------------------------- the attack border

    /// <summary>
    /// Pick the strip of the unit frame that FLASHES while auto attack is running.
    ///
    /// Only the rectangle is stored — no photograph. The test at run time is not "does this look
    /// like the picture" but "did red PULSE here while I watched", which is the only question a
    /// flash can answer: catch a flashing border mid-blink and a single frame of it is
    /// indistinguishable from a still one. Not storing a reference also means the pick survives the
    /// window moving, the UI being rescaled, and the colours being different from the day it was
    /// taken.
    /// </summary>
    private void AttackBorder_Click(object sender, RoutedEventArgs e)
    {
        // A CHECK MAY BE IN FLIGHT. ShowDialog pumps the dispatcher, so the sampler's Task.Delay
        // continuations keep running behind the picker — and MeanOf re-reads the rectangle every
        // sample, so a check started on the old strip would finish half on the new one and then
        // write its verdict onto a rectangle that has proved nothing.
        if (_borderBusy) { SetBorderState("still watching the last strip — one moment", false); return; }
        // NOT MID-RUN either. Re-picking clears both checks, so a run in progress would silently
        // drop to the three-guess blind path with nothing said — and the role thread reads these
        // four fields while the sampler is running, so they must not be rewritten underneath it.
        if (_hunt is { Running: true } || _grind is { Running: true })
        { SetBorderState("stop the run first — re-picking clears both checks", false); return; }
        AutoTargetEq();
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null) { SetBorderState("no game window to capture — launch EQ first", false); return; }
        var dlg = new Ocr.CompassPickWindow(frame, "Pick the flashing attack border",
            "Drag a THIN box over a piece of the border that flashes red while you are attacking — a strip along "
            + "one edge of the unit frame is ideal. The game world showing through it is fine — she looks for "
            + "RED PULSES, not for change. Keep your health and mana bars out of it. Then press Enter.",
            SwatchSize, loupeNX: _settings.LoupeNX, loupeNY: _settings.LoupeNY)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;
        AbsorbPickerPrefs(dlg, SwatchSize, rememberSize: false);

        _settings.AttackFlashX = dlg.NX; _settings.AttackFlashY = dlg.NY;
        _settings.AttackFlashW = dlg.NW; _settings.AttackFlashH = dlg.NH;
        // A NEW STRIP HAS PROVED NOTHING. The old measurement was about the old rectangle.
        _settings.AttackFlashSeen = 0;
        _settings.AttackFlashJumps = 0;
        _settings.AttackFlashQuiet = -1;
        _settings.Save();
        ShowBorderState();
    }

    // TWO BUTTONS, NOT ONE, and this is the whole point of the design.
    //
    // The app cannot tell which state a look was taken in, and it must not try: a weak look with
    // attack ON and a real look with attack OFF are the SAME reading — a low pulse count — so
    // inferring the state from the count lets one under-counted fight stand as the proof that this
    // strip goes quiet, which is precisely the claim the quiet check exists to test. The user is
    // the only thing in the system that knows whether attack is running, so the user says so, by
    // pressing one button rather than the other.
    private void AttackBorderOn_Click(object sender, RoutedEventArgs e) => CheckBorder(attackOn: true);
    private void AttackBorderOff_Click(object sender, RoutedEventArgs e) => CheckBorder(attackOn: false);

    /// <summary>
    /// Say where the setup has got to, WITHOUT measuring anything.
    ///
    /// Read-only on purpose. This runs unattended when the Grind page is first built, and a check
    /// that writes needs to know which state the character is in — which nobody has told it at
    /// that moment. The old version sampled here, and starting the app mid-fight could therefore
    /// file an attack-ON reading as the proof that the strip falls silent.
    /// </summary>
    private void ShowBorderState()
    {
        if (AttackBorderState is null) return;
        if (!_settings.AttackFlashSet)
        { SetBorderState("not picked — she'll have to guess, at most 3 times a run", false); return; }

        int want = Roles.HuntRole.SetupJumpsWanted;
        bool onOk = _settings.AttackFlashJumps >= want;
        bool offRun = _settings.AttackFlashQuiet >= 0;
        if (onOk && _settings.AttackFlashQuiet == 0)
        {
            SetBorderState($"checked both ways — {_settings.AttackFlashJumps} red changes with attack on, dead still "
                       + "with it off. She'll trust this strip.", true);
            return;
        }
        if (!onOk && !offRun)
        { SetBorderState($"picked, not checked. Turn auto attack ON and press \u201Ccheck: attack ON\u201D, then turn it OFF "
                     + "and press the other one.", false); return; }
        if (onOk)
        { SetBorderState($"attack-ON check passed ({_settings.AttackFlashJumps} red changes)"
                     + (offRun ? $", but with attack off it still went red {_settings.AttackFlashQuiet} time(s) — that "
                                 + "strip flickers red on its own. Move it and check both ways again."
                               : ". Now turn auto attack OFF and press \u201Ccheck: attack OFF\u201D."), offRun == false); return; }
        if (_settings.AttackFlashQuiet == 0)
        { SetBorderState(_settings.AttackFlashJumps > 0
              ? $"still with attack off, but the attack-ON check only counted {_settings.AttackFlashJumps} red changes "
                + $"of the {want} needed. "
                // THE SAME TWO FAULTS THE CHECK ITSELF DISTINGUISHES. Showing "try a thinner strip"
                // for both means the useful advice is given once and the useless advice on every
                // later visit to this page — and a slow border cannot be fixed by re-drawing.
                + (_settings.AttackFlashSeen >= Roles.HuntRole.MinFlashSpread * 2
                     ? "It is clear enough; it just blinks too slowly for me to be sure it has gone quiet rather than "
                       + "been caught mid-blink. She'll still recognise it when it fires, which only ever cancels a press."
                     : "Try a thinner strip right on the flashing edge.")
              : $"still with attack off. Now turn auto attack ON and press \u201Ccheck: attack ON\u201D.",
            _settings.AttackFlashJumps == 0); return; }
        SetBorderState($"with attack off that strip went red {_settings.AttackFlashQuiet} time(s), so something there "
                   + "flickers red whatever you are doing. She won't trust it — move the strip and check again.", false);
    }

    /// <summary>
    /// Watch the picked strip for the full window and record it against the state the user just
    /// declared.
    ///
    /// This is where the pick earns its trust. "It isn't flashing" and "this is a piece of screen
    /// where nothing ever happens" are the same reading, and only one of them justifies pressing an
    /// auto-attack toggle — so the run declines to conclude anything from stillness until BOTH
    /// checks have passed: flashing with room to spare while attack runs, and completely still
    /// while it doesn't.
    ///
    /// Latest-wins on both numbers, deliberately. A running best-ever certified the luckiest look
    /// the strip ever managed and could never be revised downwards, so a strip that had drifted off
    /// the unit frame stayed green for ever. What the readout claims is what this strip did the
    /// last time it was checked, which is a statement the user can re-test in either direction.
    /// </summary>
    private async void CheckBorder(bool attackOn)
    {
        if (AttackBorderState is null) return;
        if (!_settings.AttackFlashSet) { ShowBorderState(); return; }
        if (_borderBusy) { SetBorderState("still watching — one moment", false); return; }
        // NOT WHILE THE BOT IS PLAYING. A check taken mid-run records whatever the fight happened to
        // be doing, under a label the user chose several seconds ago and may no longer be true.
        if (_hunt is { Running: true } || _grind is { Running: true })
        { SetBorderState("stop the run first — a check taken mid-fight would record the fight, not your answer", false); return; }
        _borderBusy = true;
        try
        {
            AutoTargetEq();
            if (_grindTarget == IntPtr.Zero)
            { SetBorderState("start the game first, then check — I can only read this strip inside the game window", false); return; }

            // GIVE THEM TIME TO GET BACK TO THE GAME, and say so.
            //
            // To press this button they must be looking at THIS window, which means this window is
            // in front of the game — and the strip is read by blitting the screen, so whatever is
            // in front of it is what gets read. Without a lead-in every check would either fail
            // outright or, worse, quietly measure the EQ Avatar window and call it still.
            for (int left = LeadInSeconds; left > 0; left--)
            {
                SetBorderState($"switch to EverQuest now \u2014 watching in {left}\u2026 (auto attack should be "
                           + (attackOn ? "ON)" : "OFF)"), true);
                await Task.Delay(1000);
            }
            SetBorderState(attackOn
                ? $"watching that strip with attack ON for about {Roles.HuntRole.SetupWindowSeconds:0} seconds\u2026"
                : $"watching that strip with attack OFF for about {Roles.HuntRole.SetupWindowSeconds:0} seconds\u2026", true);
            // stopEarly: false. The run stops at the third edge because three answers its question;
            // this check has to measure how much HEADROOM the border has over a whole window, and a
            // count stopped at three can only ever report three.
            Roles.HuntRole.FlashLook look = await Roles.HuntRole.SampleFlash(
                VitalsSvc, _settings, Roles.HuntRole.FlashBar(), () => true, ms => Task.Delay(ms), stopEarly: false);
            if (!look.Watched || !look.Full)
            { SetBorderState("couldn't see that strip for long enough — the game needs to be in front and nothing "
                         + "covering the unit frame, including this window and the map overlay. Nothing recorded.",
                         false); return; }
            // THE WORLD MOVED WHILE WE WERE LOOKING. Ten seconds is long enough for the user to
            // have started the run — F7 works from inside the game, which is exactly where this
            // check just sent them — and the answer they typed a moment ago is about a character
            // who is now fighting. Writing it would overwrite a good proof with fight data.
            if (_hunt is { Running: true } || _grind is { Running: true })
            { SetBorderState("a run started while I was watching, so I've thrown that check away — stop the run and "
                         + "check again.", false); return; }

            int want = Roles.HuntRole.SetupJumpsWanted;
            if (attackOn) { _settings.AttackFlashJumps = look.Jumps; _settings.AttackFlashSeen = look.Amplitude; }
            else _settings.AttackFlashQuiet = look.Jumps;
            _settings.Save();

            if (attackOn)
            {
                if (look.Jumps == 0)
                    SetBorderState($"nothing went red in about {Roles.HuntRole.SetupWindowSeconds:0} seconds. If auto attack "
                               + "really was on, that strip isn't catching the border — re-pick a thinner one right on "
                               + "the edge that flashes.", false);
                else if (look.Jumps < want)
                    // TWO DIFFERENT FAULTS, and the same advice fixes only one of them. A strip that
                    // barely moves is a geometry problem and a thinner strip fixes it. A strip that
                    // moves a long way but only a few times is a border that blinks too slowly for
                    // this to be safe, and no amount of re-drawing will change that — telling that
                    // user to try a thinner strip sends them round a circle they cannot win.
                    SetBorderState(look.Amplitude >= Roles.HuntRole.MinFlashSpread * 2
                      ? $"that border is clear enough (it moved {look.Amplitude:0}) but it only changed {look.Jumps} "
                        + $"times in about {Roles.HuntRole.SetupWindowSeconds:0} seconds — it blinks too slowly for me to be "
                        + "sure it has gone quiet rather than caught it mid-blink, so I won't press your attack key on "
                        + "it. She'll still recognise it when it fires, which only ever cancels a press."
                      : $"only {look.Jumps} red changes and it moved just {look.Amplitude:0} — I want at least {want} "
                        + "changes before I'll act on this strip going quiet, because a check that scrapes past means "
                        + "later looks will sometimes fall short, and falling short is what presses your attack key. "
                        + "Try a thinner strip right on the flashing edge.", false);
                else if (_settings.AttackFlashQuiet == 0)
                    SetBorderState($"FLASHING — {look.Jumps} red changes (it moved {look.Amplitude:0}), and it was still "
                               + "with attack off. Both checks passed; she'll trust this strip.", true);
                else
                    SetBorderState($"FLASHING — {look.Jumps} red changes (it moved {look.Amplitude:0}). Now turn auto "
                               + "attack OFF and press \u201Ccheck: attack OFF\u201D, so I know this strip goes quiet too.", true);
            }
            else if (look.Jumps > 0)
                SetBorderState($"that strip went red {look.Jumps} time(s) with attack OFF, so something there flickers "
                           + "red whatever you are doing — she'd read that as \u201Cattack is on\u201D for ever and never "
                           + "touch your key. Move the strip and check again.", false);
            else if (_settings.AttackFlashJumps >= want)
                SetBorderState($"dead still with attack off, and {_settings.AttackFlashJumps} red changes with it on. Both "
                           + "checks passed; she'll trust this strip.", true);
            else
                SetBorderState("dead still with attack off \u2014 good. Now turn auto attack ON and press "
                           + "\u201Ccheck: attack ON\u201D.", true);
        }
        catch { SetBorderState("couldn't watch that strip just then", false); }
        finally { _borderBusy = false; }
    }

    private bool _borderBusy;
    /// <summary>Seconds between the click and the first sample, so the user can put the game back in
    /// front of this window.</summary>
    private const int LeadInSeconds = 3;

    private void SetBorderState(string text, bool good)
    {
        if (AttackBorderState is null) return;
        AttackBorderState.Text = text;
        AttackBorderState.Foreground = Hex(good ? "#7CE38B" : "#FFCB6B");
    }
}
