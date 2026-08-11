using System;
using System.Windows;
using System.Windows.Threading;
using EQAvatar.Spike.Input;

namespace EQAvatar.Spike;

/// <summary>
/// OCR auto-scan (partial class). With the box ticked, the app quietly checks the game window
/// every few seconds; whenever the inventory is open it reads the character sheet by itself —
/// no button pressing — and forwards a good read to the hub profile at most every 10 minutes.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _ocrAutoTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private bool _ocrAutoBusy;
    private bool _ocrAutoWired;
    private DateTime _ocrLastAutoSend = DateTime.MinValue;

    private void OcrAuto_Click(object sender, RoutedEventArgs e)
    {
        _settings.OcrAutoScan = OcrAutoBox.IsChecked == true;
        _settings.Save();
        if (_settings.OcrAutoScan) StartOcrAuto();
        else { _ocrAutoTimer.Stop(); OcrStatus.Text = "auto-scan off"; }
    }

    private void StartOcrAuto()
    {
        if (!_ocrAutoWired) { _ocrAutoWired = true; _ocrAutoTimer.Tick += async (_, _) => await OcrAutoTick(); }
        _ocrAutoTimer.Start();
        OcrStatus.Text = "auto-scan on — open your inventory in-game and it reads itself";
    }

    private async System.Threading.Tasks.Task OcrAutoTick()
    {
        if (_ocrAutoBusy || !_settings.OcrAutoScan) return;
        IntPtr hwnd = _grindTarget;
        if (hwnd == IntPtr.Zero && WindowFinder.GuessEverQuest() is { } w) hwnd = w.Handle;
        if (hwnd == IntPtr.Zero) return;                       // game not running — stay quiet
        _ocrAutoBusy = true;
        try
        {
            // Silent probe: no logging unless something is actually found.
            Ocr.InventorySnapshot? snap = await Ocr.InventoryReader.ReadAsync(hwnd, null);
            if (snap is null || !snap.Fields.ContainsKey("hp")) return;
            _lastSnap = snap;
            RenderOcrSnapshot(snap);                            // same rendering as the manual read
            OcrStatus.Text = $"auto-read OK at {DateTime.Now:HH:mm:ss}"
                           + (snap.Warnings.Count > 0 ? $" ({snap.Warnings.Count} warning(s))" : "");
            if ((DateTime.Now - _ocrLastAutoSend).TotalMinutes >= 10)
            {
                _ocrLastAutoSend = DateTime.Now;
                OcrSend_Click(this, new RoutedEventArgs());     // forward to the hub profile
            }
        }
        catch { /* an auto pass must never disturb the app */ }
        finally { _ocrAutoBusy = false; }
    }
}
