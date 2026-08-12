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
}
