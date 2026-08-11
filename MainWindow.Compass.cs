using System;
using System.Threading;
using System.Windows;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike;

/// <summary>
/// Compass wiring (partial class). Pick-region opens a captured frame to drag a box over the
/// in-game compass; spin-calibrate turns the character a full circle to fingerprint it. The
/// resulting CompassReader is handed to the Hunt role so homing/facing use REAL heading reads.
/// </summary>
public partial class MainWindow
{
    private CompassReader? _compassSvc;

    private CompassReader CompassSvc => _compassSvc ??= new CompassReader(() =>
        _grindTarget != IntPtr.Zero ? _grindTarget : (WindowFinder.GuessEverQuest()?.Handle ?? IntPtr.Zero));

    private void UpdateCompassStatus()
    {
        if (CompassStatus is null) return;
        CompassReader c = CompassSvc;
        CompassStatus.Text =
            !c.HasRect ? "not set — pick the region"
            : !c.Ready ? "region set — spin-calibrate next"
            : c.MappingLearned ? $"ready · {c.PxPerDeg:0.0} px/° · mapped"
            : $"ready · {c.PxPerDeg:0.0} px/° (mapping locks in while hunting)";
    }

    private void CompassPick_Click(object sender, RoutedEventArgs e)
    {
        using System.Drawing.Bitmap? frame = CompassSvc.CaptureFrame();
        if (frame is null)
        { GrindLogLine("No game window to capture — Target EverQuest first (and keep it on screen)."); return; }
        var dlg = new CompassPickWindow(frame) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            CompassSvc.SetRect(dlg.NX, dlg.NY, dlg.NW, dlg.NH);
            GrindLogLine("Compass region saved. Stand somewhere safe in-game, then press Spin-calibrate.");
        }
        UpdateCompassStatus();
    }

    private async void CompassSpin_Click(object sender, RoutedEventArgs e)
    {
        if (!CompassSvc.HasRect) { GrindLogLine("Pick the compass region first."); return; }
        CompassSpinBtn.IsEnabled = false;
        GrindLogLine("Spin calibration: focusing EQ and turning one full circle — hands off for ~10 seconds…");
        try
        {
            string result = await CompassSvc.SpinCalibrate(CancellationToken.None);
            GrindLogLine("Compass: " + result);
        }
        catch (Exception ex) { GrindLogLine("Compass calibration failed: " + ex.Message); }
        finally { CompassSpinBtn.IsEnabled = true; UpdateCompassStatus(); }
    }
}
