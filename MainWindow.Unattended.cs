using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike;

/// <summary>
/// The unattended-run seam: the <see cref="UnattendedGuard"/>'s wiring, the death → respawn-click
/// recovery, and the Grind-page controls for both. Everything here exists because of two
/// instrumented overnight runs (deathwatch, 2026-08-24): the client AFK-flags ~30 minutes after
/// input stops and the server kicks it ~31.6 minutes after that — at which point the client
/// EXITS (END_GAME), not pauses. A death and a stolen focus both start that clock. The app's
/// answer used to be "stop for safety", which is precisely how the clock runs out.
/// </summary>
public partial class MainWindow
{
    private UnattendedGuard? _guard;

    [StructLayout(LayoutKind.Sequential)]
    private struct UGRECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool UgGetWindowRect(IntPtr hWnd, out UGRECT rect);

    private UnattendedGuard EnsureGuard()
    {
        return _guard ??= new UnattendedGuard(
            () => _grindTarget,
            () => _grind is { Running: true } || _hunt is { Running: true }
               || _questRun is { Running: true } || _mergeRun is { Running: true },
            _settings,
            m => Dispatcher.BeginInvoke(new Action(() => GrindLogLine(m))));
    }

    /// <summary>Called from the 300 ms UI tick, right after the focus stamp. Also the lazy
    /// attach point for the guard's log tail: _currentLog is resolved at role start but can also
    /// appear later (Find newest log), and the guard should hear the A.F.K. line either way.</summary>
    private void GuardTick()
    {
        UnattendedGuard g = EnsureGuard();
        g.Attach(_currentLog);
        g.Tick();
    }

    /// <summary>A role is starting: the hold (if any) is over — a running role IS the keep-alive —
    /// and the guard gets the same log the role got.</summary>
    private void GuardOnRoleStart()
    {
        UnattendedGuard g = EnsureGuard();
        g.HoldSession = false;
        g.ResetThrash();        // a fresh run is the user saying "try again"
        g.Attach(_currentLog);
    }

    /// <summary>F12 means EVERYTHING stops — the hold too. The guard sending keep-alive taps after
    /// a panic press would be the app fighting the one instruction that must always win.</summary>
    private void GuardHoldOff() { if (_guard != null) _guard.HoldSession = false; }

    /// <summary>One clause of cause for the close history, or empty when no guard ever ran.</summary>
    private string GuardCloseCause() => _guard?.CloseCause(DateTime.UtcNow) ?? "";

    /// <summary>How many minutes ago the guard's most recent death/AFK sighting was, or null when
    /// it has seen neither. The caller compares it against the RUN length: an event older than the
    /// run started before the run and cannot explain the run ending. Without this, 0.10.55 blamed
    /// a 5am server patch on a death from the previous evening.</summary>
    private (string Cause, double? AgeMinutes) GuardCloseCauseWithAge()
        => _guard?.CloseCauseWithAge(DateTime.UtcNow) ?? ("", null);

    /// <summary>Was the guard holding a dead character's session alive when the window died? That
    /// still counts as a run in progress for recovery purposes — the client is what needs bringing
    /// back, even though the character must NOT be sent hunting again from its bind point.</summary>
    private bool GuardWasHolding => _guard?.HoldSession == true;

    // ---------------- death → respawn window ----------------

    /// <summary>
    /// The hunt parked itself for safety — character alive, run over. Hold the session so the
    /// client survives until someone can put the character back at camp.
    /// </summary>
    private void OnHuntParked()
    {
        EnsureGuard().HoldSession = true;
        // ONLY CLAIM IT IF IT WILL HAPPEN. The hold is gated on the guard being enabled, so with
        // the checkbox off this line used to promise a keep-alive nothing was going to send — and
        // the run would end in the very idle kick the hold exists to prevent.
        GrindLogLine(_settings.UnattendedGuardEnabled
            ? "Holding the session alive (activity tap every few minutes) so the client isn't idle-kicked while the "
              + "character waits. Put it back at camp and start the run again when you're ready."
            : "⚠ The unattended guard is OFF, so nothing will keep this session alive — the client will be idle-kicked "
              + "in about half an hour and then exit. Turn the guard on, or come back to the character before then.");
    }

    /// <summary>
    /// The hunt died. Stopping the hunt was right (the character is about to be somewhere else —
    /// resuming at bind is how it walked into the sea); what must NOT happen is input stopping.
    /// So: hold the session immediately, then deal with the respawn window if its button has been
    /// picked. The character ends parked at bind with the guard tapping Shift every few minutes —
    /// alive, safe, and exactly where it respawned — until a person starts the next run.
    /// </summary>
    private void OnHuntDied()
    {
        UnattendedGuard g = EnsureGuard();
        g.HoldSession = true;
        if (_settings.RespawnClickNX <= 0 && _settings.RespawnClickNY <= 0)
        {
            GrindLogLine("No respawn button is picked, so I can't click through the respawn window "
                       + "— pick it on the Grind page (die somewhere safe once to do it). I'll keep "
                       + "the session alive meanwhile; the respawn window may still time out on the "
                       + "server side, so this is the half of the fix that needs you.");
            return;
        }
        _ = RespawnRecoverAsync();
    }

    /// <summary>
    /// Click the respawn window's button and report honestly what happened. Three waits matter:
    /// the window takes a beat to appear after the death line; the game may not be focused (a
    /// death can follow a focus loss); and after the click EQ shows a loading screen during which
    /// the game WINDOW is destroyed and rebuilt — the watchdog narrates that as a rebuild, which
    /// is exactly what it is, and re-attaches on its own.
    /// </summary>
    private async Task RespawnRecoverAsync()
    {
        GrindLogLine("Respawn: waiting for the respawn window, then clicking the picked button.");
        await Task.Delay(4000);
        IntPtr h = _grindTarget;
        if (h == IntPtr.Zero) { GrindLogLine("Respawn: the game window is already gone — nothing to click."); return; }

        // The kick clock gives roughly an hour from the death; one focus attempt would be the
        // stingiest possible use of it. Five tries, twenty seconds apart, narrated once.
        bool front = false;
        for (int attempt = 0; attempt < 5 && !front; attempt++)
        {
            if (attempt > 0) await Task.Delay(20000);
            front = await GameFocus.BringAndSettleAsync(h, settleMs: 400);
        }
        if (!front)
        { GrindLogLine("Respawn: couldn't bring the game forward after 5 tries — the guard keeps working on it; the click is off."); return; }

        // A dead handle fails GetWindowRect, so this line is also the liveness check — and it must
        // be, because calling GameWindowDied() from here would consume the tick watchdog's one-shot
        // detection and swallow its narration.
        if (!UgGetWindowRect(h, out UGRECT r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0)
        { GrindLogLine("Respawn: couldn't measure the game window (gone or rebuilding) — no click."); return; }

        var rng = new Random();
        int x = r.Left + (int)(_settings.RespawnClickNX * (r.Right - r.Left)) + rng.Next(-2, 3);
        int y = r.Top + (int)(_settings.RespawnClickNY * (r.Bottom - r.Top)) + rng.Next(-2, 3);
        HumanizedMouse.MoveInstant(x, y);
        await Task.Delay(120 + rng.Next(80));
        HumanizedMouse.Click(rng);
        GrindLogLine("Respawn: clicked the respawn button. Expect the loading screen (the game window "
                   + "rebuilds — that's normal). The character will be AT ITS RESPAWN POINT, so I am "
                   + "NOT resuming the hunt: the session stays alive on the guard's keep-alive until "
                   + "you start the next run. (Camp-to-bind pathing is what the Pathfinding tool is for.)");
    }

    // ---------------- Grind-page controls ----------------

    private void Unattended_Click(object sender, RoutedEventArgs e)
    {
        _settings.UnattendedGuardEnabled = UnattendedBox.IsChecked == true;
        _settings.Save();
        GrindLogLine(_settings.UnattendedGuardEnabled
            ? "Unattended guard ON — I'll refocus the game and answer the A.F.K. flag when the machine "
              + "is idle, and keep the session alive after a death. It never acts while you're at the keyboard."
            : "Unattended guard OFF — overnight runs are back to the measured failure: input stops → "
              + "A.F.K. ~30 min → server kick ~30 min later → the client exits.");
    }

    private void RespawnPick_Click(object sender, RoutedEventArgs e)
    {
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        { GrindLogLine("No game window to capture — Target EverQuest first (and keep it on screen)."); return; }

        var dlg = new CompassPickWindow(frame,
            "Pick the respawn button",
            "Drag a box over the button you'd click on the RESPAWN window (do this once while the "
            + "window is actually up — die somewhere safe). After a death I'll click its centre, "
            + "then hold the session alive at the respawn point instead of hunting on.")
        { Owner = this };
        if (dlg.ShowDialog() != true) { UpdateRespawnPickLabel(); return; }

        _settings.RespawnClickNX = dlg.NX + dlg.NW / 2;
        _settings.RespawnClickNY = dlg.NY + dlg.NH / 2;
        _settings.Save();
        UpdateRespawnPickLabel();
        GrindLogLine($"Respawn button saved at {_settings.RespawnClickNX:0.000}, {_settings.RespawnClickNY:0.000} "
                   + "(normalized to the game window). On the next death I'll click it and hold the session.");
    }

    private void UpdateRespawnPickLabel()
    {
        bool set = _settings.RespawnClickNX > 0 || _settings.RespawnClickNY > 0;
        RespawnPickLabel.Text = set ? "respawn ✓" : "respawn: not picked";
    }

    private void InitUnattendedUi()
    {
        UnattendedBox.IsChecked = _settings.UnattendedGuardEnabled;
        UpdateRespawnPickLabel();
    }
}
