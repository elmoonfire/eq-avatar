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

    /// <summary>Read the rest-gating boxes into settings (called with the rest of ApplyHuntFields).</summary>
    private void ApplyVitalsFields()
    {
        _settings.RestGateEnabled = RestGateBox.IsChecked == true;
        if (int.TryParse(RestHpBox.Text.Trim(), out int hp)) _settings.RestHpPercent = Math.Clamp(hp, 0, 100);
        if (int.TryParse(RestManaBox.Text.Trim(), out int mp)) _settings.RestManaPercent = Math.Clamp(mp, 0, 100);
        if (int.TryParse(RestMaxBox.Text.Trim(), out int cap)) _settings.RestMaxSeconds = Math.Clamp(cap, 5, 3600);
    }

    /// <summary>Fill the rest-gating boxes from saved settings on load.</summary>
    private void InitVitalsUi()
    {
        RestGateBox.IsChecked = _settings.RestGateEnabled;
        RestHpBox.Text = _settings.RestHpPercent.ToString();
        RestManaBox.Text = _settings.RestManaPercent.ToString();
        RestMaxBox.Text = _settings.RestMaxSeconds.ToString();
        UpdateVitalsStatus(live: false);
    }
}
