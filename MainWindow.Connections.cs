using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Net;

namespace EQAvatar.Spike;

/// <summary>
/// "Last 10 connections" card on the Licensing panel (partial class). The hub groups this
/// character's check-ins into connections server-side (a 15-minute silence starts a new one);
/// each row shows when, how long, which roles ran, and what they produced. Refreshes after a
/// successful check-in and on demand.
/// </summary>
public partial class MainWindow
{
    private bool _connBusy;

    private async void ConnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshConnections();

    internal async Task RefreshConnections()
    {
        if (_connBusy) return;
        if (string.IsNullOrWhiteSpace(_settings.HubUsername))
        {
            ConnHint.Text = "Set a check-in name above — connection history is per character.";
            return;
        }
        _connBusy = true;
        try
        {
            List<ConnRow>? rows = await _hub.GetHistory();
            if (rows is null)
            {
                ConnHint.Text = "Couldn't load connection history — hub unreachable or unauthorized.";
                return;
            }
            ConnList.ItemsSource = rows;
            ConnHint.Text = rows.Count == 0
                ? "No connections recorded yet — check in once and this fills in."
                : $"Last {rows.Count} connection(s) · a 15+ minute silence starts a new one · refreshed {DateTime.Now:HH:mm:ss}";
        }
        finally { _connBusy = false; }
    }
}
