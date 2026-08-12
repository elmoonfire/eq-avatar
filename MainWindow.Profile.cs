using System;
using System.Diagnostics;
using System.Windows;

namespace EQAvatar.Spike;

/// <summary>
/// The Profile page (partial class): the character's identity broken out of Licensing — name,
/// server, class combination, CURRENT level (class changes reset it to 10) and the BEST level
/// ever reached on the account, plus the character-sheet OCR that keeps it all honest.
/// </summary>
public partial class MainWindow
{
    private void UpdateProfilePanel()
    {
        if (ProfName is null) return;
        string name = (_settings.HubUsername ?? "").Trim();
        string cls = (_settings.HubClass ?? "").Trim();
        int lv = Math.Max(1, _settings.HubLevel);
        int best = Math.Max(_settings.HubMaxLevel, lv);
        ProfName.Text = name.Length == 0 ? "No character yet" : name;
        ProfSub.Text = name.Length == 0
            ? "set your check-in name on the Licensing page, then fill the character below"
            : $"{(_settings.HubServer ?? "Rivervale").Trim()}{(cls.Length > 0 ? " · " + cls : "")}{(string.IsNullOrWhiteSpace(_settings.HubRace) ? "" : " · " + _settings.HubRace)}";
        ProfCurLvl.Text = $"Lv {lv}" + (cls.Length > 0 ? $" · {cls}" : "");
        ProfBestLvl.Text = $"Lv {best}";
    }

    private void OpenArmory_Click(object sender, RoutedEventArgs e)
    {
        string name = (_settings.HubUsername ?? "").Trim();
        if (name.Length == 0) { ShowToast("Set your name first (Licensing page)"); return; }
        string url = _settings.HubUrl;
        int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
        string baseUrl = i >= 0 ? url.Substring(0, i) : url;
        try { Process.Start(new ProcessStartInfo(baseUrl + "profile.php?u=" + Uri.EscapeDataString(name)) { UseShellExecute = true }); }
        catch (Exception ex) { GrindLogLine("Couldn't open browser: " + ex.Message); }
    }
}
