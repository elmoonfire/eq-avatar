using System;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Net;

namespace EQAvatar.Spike;

/// <summary>
/// Remote-control glue (partial class). The phone app / website queue commands on the hub;
/// the RemoteClient loop pulls them here and this file maps them onto the real roles — the
/// same code paths as clicking the buttons. Everything that touches the UI or the game window
/// runs on the dispatcher. Live status (role, zone, /loc, counters) is snapshotted for the
/// phone's home screen, and finished sessions upload so reports/charts work away from the PC.
/// </summary>
public partial class MainWindow
{
    private RemoteClient? _remote;

    // Live position for the phone map — fed from the maps log tap (OnMapsLogLine).
    private double _lastLocEw = double.NaN, _lastLocNs = double.NaN;
    private DateTime _lastLocAt = DateTime.MinValue;

    private void StartRemoteControl()
    {
        if (_remote != null) return;
        _remote = new RemoteClient(_settings, ExecuteRemoteCommand, RemoteStatusSnapshot);
        _remote.Log += m => Dispatcher.InvokeAsync(() => LicLogLine("[remote] " + m));
        _remote.Start();
    }

    /// <summary>Everything the phone needs to paint "what is my character doing right now".</summary>
    private object? RemoteStatusSnapshot()
    {
        try
        {
            return Dispatcher.Invoke(() =>
            {
                var (role, actions, kills, xp) = HubStats();
                bool paused =
                    (_hunt is { Running: true } h && h.Stats.State.Contains("paused", StringComparison.OrdinalIgnoreCase))
                    || (_grind is { Running: true } g && g.Stats.Paused);
                if (_follower is { Running: true } f)
                { role = "Follower"; actions = f.Stats.Assists + f.Stats.Refollows; kills = f.Stats.Kills; }

                return (object)new
                {
                    role,
                    paused,
                    zone = _heat.Current is { Length: > 0 } z ? z : null,
                    zone_stem = _charZoneStem,
                    loc = double.IsNaN(_lastLocEw) ? null : new
                    {
                        ew = _lastLocEw,
                        ns = _lastLocNs,
                        age_s = _lastLocAt == DateTime.MinValue ? -1 : (int)(DateTime.Now - _lastLocAt).TotalSeconds,
                    },
                    session = new { actions, kills, xp_ticks = xp, recording = Recorder.Active },
                    level = _settings.HubLevel,
                    cls = _settings.HubClass,
                    stream = new { live = false },      // flips on when the WebRTC publisher lands
                    version = AppSettings.AppVersion,
                };
            });
        }
        catch { return null; }
    }

    /// <summary>Route one remote command onto the UI thread and into the same code paths the buttons use.</summary>
    private Task<(bool ok, string result)> ExecuteRemoteCommand(RemoteCommand cmd) =>
        Dispatcher.InvokeAsync(() => cmd.Kind switch
        {
            "switch_role" => RemoteSwitchRole(cmd.Str("role") ?? ""),
            "stop" => RemoteStop(),
            "set_grind_area" => RemoteSetGrindArea(cmd),
            "clear_grind_area" => RemoteClearGrindArea(),
            "farm_mob" => RemoteFarmMob(cmd.Str("name") ?? cmd.Str("mob") ?? ""),
            _ => (false, $"unknown command kind '{cmd.Kind}'"),
        }).Task;

    private (bool ok, string result) RemoteSwitchRole(string role)
    {
        switch (role.Trim().ToLowerInvariant())
        {
            case "stop":
            case "idle":
                return RemoteStop();

            case "grind":
            case "hunt":
            {
                if (_grind is { Running: true } || _hunt is { Running: true })
                    return (true, "a grind role is already running");
                if (!RemoteEnsureGameTarget(out string why)) return (false, why);
                if (role.Trim().Equals("hunt", StringComparison.OrdinalIgnoreCase)) HuntBox.IsChecked = true;
                StartGrind_Click(this, new RoutedEventArgs());
                bool hunting = _hunt is { Running: true };
                return (hunting || _grind is { Running: true })
                    ? (true, $"started {(hunting ? "Hunt" : "Grind")} remotely — remember the game must stay the focused window")
                    : (false, "could not start — check the rotation and log settings on the PC");
            }

            case "follower":
            {
                if (_follower is { Running: true }) return (true, "Follower is already running");
                if (!RemoteEnsureGameTarget(out string why)) return (false, why);
                StartFollower_Click(this, new RoutedEventArgs());
                return _follower is { Running: true }
                    ? (true, $"started Follower remotely (leader: {_settings.FollowerLeader})")
                    : (false, "could not start Follower — set the leader name on the PC first");
            }

            default:
                return (false, $"unknown role '{role}' — use Grind, Hunt, Follower, or Stop");
        }
    }

    private (bool ok, string result) RemoteStop()
    {
        bool wasRunning = _grind is { Running: true } || _hunt is { Running: true } || _follower is { Running: true };
        _grind?.Stop();
        _hunt?.Stop();
        _follower?.Stop();
        _grindTimer.Stop();
        _followerTimer.Stop();
        UpdateGrindStats();
        UpdateFollowerStats();
        return (true, wasRunning ? "all roles stopped" : "nothing was running");
    }

    /// <summary>Phone: "keep it near where it is" — tether to the current spot with the given radius.</summary>
    private (bool ok, string result) RemoteSetGrindArea(RemoteCommand cmd)
    {
        int radius = int.TryParse(cmd.Str("radius"), out int r) ? Math.Clamp(r, 50, 1500) : _settings.HuntTetherRadius;
        TetherBox.IsChecked = true;
        TetherSlider.Value = radius;
        ApplyHuntFields();
        _settings.Save();
        // A new anchor takes effect on the next role start; if Hunt is live it re-anchors next /loc.
        return (true, $"tether armed — radius {radius} around the {(double.IsNaN(_lastLocEw) ? "next start point" : "current spot")}");
    }

    private (bool ok, string result) RemoteClearGrindArea()
    {
        TetherBox.IsChecked = false;
        ApplyHuntFields();
        _settings.Save();
        return (true, "tether cleared — free roam within explored bounds");
    }

    /// <summary>Phone: "go kill this" — add to the directive list and switch the stance.</summary>
    private (bool ok, string result) RemoteFarmMob(string name)
    {
        name = name.Trim();
        if (name.Length < 2) return (false, "farm_mob needs a mob name");
        AddDirectiveTarget(name);
        return (true, $"'{name}' added to the directive target list (stance: Directive)");
    }

    /// <summary>A remote start can't click "Target EverQuest" first — find the game window ourselves.</summary>
    private bool RemoteEnsureGameTarget(out string why)
    {
        why = "";
        if (_grindTarget != IntPtr.Zero) return true;
        if (WindowFinder.GuessEverQuest() is { } w)
        {
            _grindTarget = w.Handle;
            GrindTargetLabel.Text = $"target: {w.ProcessName} \"{w.Title}\"  0x{w.Handle.ToInt64():X}";
            return true;
        }
        why = "EverQuest isn't running on the PC (no game window found)";
        return false;
    }
}
