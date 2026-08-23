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

    // ---------------------------------------------------------------- the attack indicator

    /// <summary>
    /// Learn what the auto-attack indicator looks like while attack is OFF.
    ///
    /// OFF and not ON, because OFF is the state a person can reliably arrange while picking — stand
    /// still, don't attack, drag a box — whereas holding attack on through a pick means being in a
    /// fight while dragging a rectangle. At run time the question is then "does this still look like
    /// off", and everything that could go wrong with it (the lamp lit, a tooltip across it, the
    /// window moved) answers no, i.e. "assume attack is on", i.e. don't press a toggle. The safe
    /// answer is the easy one to arrange.
    /// </summary>
    private void AttackLamp_Click(object sender, RoutedEventArgs e)
    {
        AutoTargetEq();
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        {
            SetLampState("no game window to capture — launch EQ first", false);
            return;
        }
        var dlg = new Ocr.CompassPickWindow(frame, "Pick the attack indicator",
            "Make sure auto attack is OFF first. Drag a SMALL box around the little light that comes on when you "
            + "are attacking — keep your health and mana bars OUT of it, or it will look different every second "
            + "for reasons that have nothing to do with attack. Then press Enter.",
            SwatchSize, loupeNX: _settings.LoupeNX, loupeNY: _settings.LoupeNY)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;
        AbsorbPickerPrefs(dlg, SwatchSize, rememberSize: false);

        Roles.QuestFind.IconPatch? off = Roles.QuestFind.PatchFromRegion(frame, dlg.NX, dlg.NY, dlg.NW, dlg.NH);
        if (off is not { Ok: true })
        {
            SetLampState("that box was too small or off-screen — try again", false);
            return;
        }
        // Capped like the quest icon references, and for a sharper reason: this rides inside
        // AppSettings, which is rewritten on a 450 ms debounce every time anything on any page
        // changes — far more often than questscripts.json.
        if (off.Data.Length > 120_000)
        {
            SetLampState($"that box is {off.W}×{off.H} — too big to store. Drag a small one around just the light.",
                         false);
            return;
        }
        _settings.AttackLampX = dlg.NX; _settings.AttackLampY = dlg.NY;
        _settings.AttackLampW = dlg.NW; _settings.AttackLampH = dlg.NH;
        _settings.AttackLampOff = off;
        // A NEW PICK IS UNPROVEN AGAIN. The old proof was about the old box.
        _settings.AttackLampProven = false;
        _settings.AttackLampSawOn = false;
        _settings.Save();
        SetLampState($"learned ({off.W}×{off.H}) as ATTACK OFF. Now turn attack ON in game and click this text — "
                   + "it has to see the difference before it will trust the reading.", true);
    }

    /// <summary>What the indicator reads RIGHT NOW, against what was learned. Shown so the pick can
    /// be trusted before a run depends on it: a pick that silently learned the wrong thing looks
    /// exactly like one that worked.</summary>
    private void RefreshLampState()
    {
        if (AttackLampState is null) return;
        if (!_settings.AttackLampSet) { SetLampState("not picked — she'll have to guess, at most 3 times a run", false); return; }
        try
        {
            using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
            if (frame is null) { SetLampState("learned (game not on screen)", true); return; }
            Roles.QuestFind.IconPatch? now = Roles.QuestFind.PatchFromRegion(
                frame, _settings.AttackLampX, _settings.AttackLampY, _settings.AttackLampW, _settings.AttackLampH);
            if (now is not { Ok: true } live || _settings.AttackLampOff is not { Ok: true } off
                || live.W != off.W || live.H != off.H)
            { SetLampState("learned — but that spot can't be read now; re-pick if the window moved", false); return; }
            double ncc = Roles.QuestFind.Ncc(live.Pixels, off.Pixels);
            // A TWO-STATE HANDSHAKE, because one reading proves nothing. "It stopped looking like
            // the off picture" is produced by the lamp lighting — and equally by a tooltip drifting
            // over that corner, or the window moving a pixel between the pick and the check. Only a
            // region that changes and then changes BACK has demonstrated it is tracking a light
            // rather than an accident, and it is this proof that unlocks the one answer ("off")
            // which ends in the toggle being pressed.
            if (ncc < 0.97)
            {
                if (!_settings.AttackLampSawOn) { _settings.AttackLampSawOn = true; _settings.Save(); }
                SetLampState(_settings.AttackLampProven
                    ? $"reads ATTACK ON ({ncc * 100:0}% like the off picture)"
                    : $"reads ATTACK ON ({ncc * 100:0}% like the off picture) — good. Now turn attack OFF in game "
                      + "and click here once more, and I'll know this box really is following the light.", true);
            }
            else if (_settings.AttackLampProven)
                SetLampState($"reads attack off ({ncc * 100:0}% like the off picture)", true);
            else if (_settings.AttackLampSawOn)
            {
                _settings.AttackLampProven = true;
                _settings.Save();
                SetLampState("reads attack off again — that's both states seen, so this box really is following "
                           + "the light. She'll trust it from now on and stop guessing.", true);
            }
            else
                SetLampState($"reads attack off ({ncc * 100:0}% like the off picture). Turn attack ON in game and "
                           + "click here — until I've seen this box look different I won't trust it to tell me "
                           + "attack is off.", false);
        }
        catch { SetLampState("learned", true); }
    }

    private bool _lampClickWired;

    private void SetLampState(string text, bool good)
    {
        if (AttackLampState is null) return;
        AttackLampState.Text = text;
        AttackLampState.Foreground = Hex(good ? "#7CE38B" : "#FFCB6B");
        // CLICKING THE READOUT RE-READS IT — the button must keep meaning "pick", because a second
        // pick taken while attack is ON would learn the lit lamp as the picture of OFF and invert
        // the whole test. Wired once: this method runs on every route in, and hooking the same
        // handler each time would fire it as many times as the page had been visited.
        if (_lampClickWired) return;
        _lampClickWired = true;
        AttackLampState.Cursor = System.Windows.Input.Cursors.Hand;
        AttackLampState.MouseLeftButtonUp += (_, _) => RefreshLampState();
    }
}
