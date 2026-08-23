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
    /// like the picture" but "did this change while I watched", which is the only question a FLASH
    /// can answer: catch a flashing border mid-blink and a single frame of it is indistinguishable
    /// from a still one. Not storing a reference also means the pick survives the window moving,
    /// the UI being rescaled, and the colours being different from the day it was taken.
    /// </summary>
    private void AttackLamp_Click(object sender, RoutedEventArgs e)
    {
        AutoTargetEq();
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null) { SetLampState("no game window to capture — launch EQ first", false); return; }
        var dlg = new Ocr.CompassPickWindow(frame, "Pick the flashing attack border",
            "Drag a THIN box over a piece of the border that flashes red while you are attacking — a strip along "
            + "one edge of the unit frame is ideal. Keep your health and mana bars out of it, or they will look "
            + "like a flash every time they move. Then press Enter.",
            SwatchSize, loupeNX: _settings.LoupeNX, loupeNY: _settings.LoupeNY)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;
        AbsorbPickerPrefs(dlg, SwatchSize, rememberSize: false);

        _settings.AttackFlashX = dlg.NX; _settings.AttackFlashY = dlg.NY;
        _settings.AttackFlashW = dlg.NW; _settings.AttackFlashH = dlg.NH;
        // A NEW STRIP HAS PROVED NOTHING. The old measurement was about the old rectangle.
        _settings.AttackFlashSeen = 0;
        _settings.Save();
        SetLampState("picked. Now turn auto attack ON in game and click this text — I'll watch that strip for a "
                   + "second and see whether it really flashes.", true);
    }

    /// <summary>
    /// Watch the picked strip for about a second and say what happened.
    ///
    /// This is also where the pick earns its trust. "It isn't flashing" and "this is a piece of
    /// screen where nothing ever happens" are the same reading, and only one of them justifies
    /// pressing an auto-attack toggle — so until a real flash has been measured here, the run
    /// declines to conclude anything from stillness.
    /// </summary>
    private async void RefreshLampState()
    {
        if (AttackLampState is null) return;
        if (!_settings.AttackFlashSet)
        { SetLampState("not picked — she'll have to guess, at most 3 times a run", false); return; }
        if (_lampBusy) return;
        _lampBusy = true;
        try
        {
            AutoTargetEq();
            if (_grindTarget == IntPtr.Zero)
            {
                // NOT AN ALARM. This runs once at startup, and the game not being open yet is the
                // ordinary case, not a fault with the pick.
                SetLampState("picked — start the game and click here to check it", true);
                return;
            }
            SetLampState("watching that strip…", true);
            double bar = Math.Max(Roles.HuntRole.MinFlashSpread, _settings.AttackFlashSeen * 0.35);
            Roles.HuntRole.FlashLook look = await Roles.HuntRole.SampleFlash(
                VitalsSvc, _settings, bar, () => true, ms => Task.Delay(ms));
            if (!look.Watched)
            { SetLampState("couldn't read that strip — is it inside the game window?", false); return; }
            double spread = look.Spread;

            if (spread >= bar && spread >= Roles.HuntRole.MinFlashSpread)
            {
                // The strongest flash seen wins: the bar is derived from it, and a weak sample later
                // must not be able to talk the threshold down to where noise clears it.
                bool changed = false;
                if (spread > _settings.AttackFlashSeen) { _settings.AttackFlashSeen = spread; changed = true; }
                // THE DUTY IS THE MEASUREMENT THAT MATTERS MOST, because the run's trimmed range
                // needs two lit samples to survive trimming and a border that merely winks gives
                // it one. The smallest duty seen wins — it is the one the run has to cope with.
                if (look.Duty > 0 && (_settings.AttackFlashDuty <= 0 || look.Duty < _settings.AttackFlashDuty))
                { _settings.AttackFlashDuty = look.Duty; changed = true; }
                if (changed) _settings.Save();
                // AND SAY WHEN IT IS ONLY JUST A FLASH. MeanOf averages the whole strip, so a two
                // pixel border inside a generous box is diluted to within a unit or two of the noise
                // floor — it works today and misreads mid-fight on the first frame that lands badly.
                // The number to judge that by is already in hand; it just has to be said.
                bool faint = spread < Roles.HuntRole.MinFlashSpread * 2;
                bool brief = look.Duty > 0 && look.Duty < Roles.HuntRole.MinTrustedDuty;
                SetLampState(
                    faint
                      ? $"flashing, but only just (moved {spread:0}, floor is {Roles.HuntRole.MinFlashSpread:0}). "
                        + "Re-pick a narrower strip right on the border — a wide box averages the flash away and "
                        + "she may misread it mid-fight."
                    : brief
                      ? $"FLASHING (moved {spread:0}), but lit only {look.Duty * 100:0}% of the time — a wink rather "
                        + "than a blink. I can spot it when it fires, but I can't safely tell 'not flashing' from "
                        + "'I looked between flashes', so I won't press your attack key on that reading. A strip "
                        + "closer to the part that stays lit longest would fix it."
                      : $"FLASHING (moved {spread:0}, lit {look.Duty * 100:0}% of the time) — that's auto attack on, "
                        + "and it's what she'll watch for.",
                    !faint && !brief);
            }
            else if (_settings.AttackFlashProven)
                SetLampState($"not flashing (moved {spread:0}, needs {bar:0}) — that reads as auto attack off.", true);
            else
                SetLampState($"nothing moved (only {spread:0}). Turn auto attack ON and click again — until I have "
                           + "seen this strip flash once, I won't take stillness as proof it's off.", false);
        }
        catch { SetLampState("couldn't watch that strip just then", false); }
        finally { _lampBusy = false; }
    }

    private bool _lampBusy;

    private bool _lampClickWired;

    private void SetLampState(string text, bool good)
    {
        if (AttackLampState is null) return;
        AttackLampState.Text = text;
        AttackLampState.Foreground = Hex(good ? "#7CE38B" : "#FFCB6B");
        // CLICKING THE READOUT WATCHES AGAIN — the button must keep meaning "pick", because a
        // re-pick is the only thing that discards the measurement. Wired once: this method runs on
        // every route in, and hooking the same handler each time would fire it as many times as the
        // page had been visited.
        if (_lampClickWired) return;
        _lampClickWired = true;
        AttackLampState.Cursor = System.Windows.Input.Cursors.Hand;
        AttackLampState.MouseLeftButtonUp += (_, _) => RefreshLampState();
    }
}
