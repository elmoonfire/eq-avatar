using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;
using EQAvatar.Spike.Login;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike;

/// <summary>
/// Re-Instance: after the instance expires and the character is thrown into the public zone, make
/// a NEW instance so the run can carry on.
///
/// Hayden, 08-26, watching it happen live: "if I'm in an instance and it kicks me out I don't
/// really want to go right back to my location in the public version of the zone as other players
/// will be there. I would want the bot to get me back into the instance… otherwise there is no
/// point in having the bot running."
///
/// The UI work lives here rather than in HuntRole because it is all windows and clicking, and the
/// role drives a character. HuntRole calls in through a callback and gets back one bit: are we in
/// a new instance, PROVEN by a zone line in the log. Everything else — which button, whether
/// there is a charge left, whether the window is even the right window — is decided here, and
/// every one of those decisions is allowed to answer "no".
/// </summary>
public partial class MainWindow
{
    /// <summary>The instance menu takes a moment to draw after the icon is clicked, and the
    /// options window a moment more after that. Both are generous: a read taken too early sees
    /// the game behind the window, finds nothing, and gives up on a sequence that was working.</summary>
    private const int InstanceMenuSettleMs = 1400;
    private const int InstanceWindowSettleMs = 1800;

    /// <summary>How long to wait for the zone line that proves we are actually inside. Entering an
    /// instance is a full zone with a loading screen, and this machine's is not this machine's.</summary>
    private static readonly TimeSpan InstanceZoneTimeout = TimeSpan.FromMinutes(4);

    /// <summary>How long to wait for the person to stop using the computer before giving up on
    /// re-instancing this time, and how idle the machine has to be to count as quiet.</summary>
    private static readonly TimeSpan ReInstanceQuietWait = TimeSpan.FromMinutes(4);
    /// <summary>The SAME number the unattended guard uses. Two presence tests in one app is two
    /// different answers to one question, and the guard's header says so.</summary>
    private const int ReInstanceIdleSeconds = UnattendedGuard.IdleGateSec;

    /// <summary>
    /// Everything this file says to the user. THE ROLE CALLS IN ON A THREAD-POOL THREAD, because
    /// HuntRole.Start runs its loop under Task.Run — so there is no synchronization context and
    /// nothing marshals a continuation back. GrindLogLine writes straight into a TextBox, and the
    /// first version of this file called it directly: the very first line, right after the icon
    /// was clicked, threw "the calling thread cannot access this object", the role caught it,
    /// reported "Re-Instance failed with an error" and parked. Every night, on the first eject,
    /// with the instance menu left open on screen and the true fault invisible behind a message
    /// about the game's UI.
    /// </summary>
    private void RiLog(string m) => Dispatcher.BeginInvoke(new Action(() => GrindLogLine(m)));

    /// <summary>
    /// Work the instance UI and report whether the character ended up somewhere new.
    ///
    /// The sequence is icon → menu row → Enter, and it refuses at every step rather than pressing
    /// on hopefully. The refusals are the feature: the window this ends at also carries "Quit
    /// Instance", and <see cref="InstanceWindow.FindSafeButton"/> exists because the app's normal
    /// text search is a substring match that would happily return it.
    /// </summary>
    private async Task<bool> ReInstanceAsync(CancellationToken ct)
    {
        IntPtr h = _grindTarget;
        if (h == IntPtr.Zero)
        { RiLog("Re-Instance: there is no game window to work with."); return false; }

        if (_settings.InstanceIconNX <= 0 && _settings.InstanceIconNY <= 0)
        {
            RiLog("Re-Instance: the instance icon on the menu bar has not been picked, so I have nothing to "
                + "click. Pick it on the Grind page (“pick instance icon”) while the game is on screen.");
            return false;
        }

        // CHECKED ONCE, UP FRONT, because the proof of success comes out of the character log and
        // pressing Enter without one spends a real instance charge on an attempt this app will
        // then have to report as a failure. Auto-recover learned this the same way.
        string logFolder = await Dispatcher.InvokeAsync(() => LogFolderBox.Text.Trim());
        if (EqLogWatcher.FindNewestLog(logFolder) is null)
        {
            RiLog($"Re-Instance: no character log found in '{logFolder}', so I'd have no way to know whether "
                + "entering worked — and pressing Enter to find out spends a charge. Set the log folder on the "
                + "Log Reader panel.");
            return false;
        }

        // NOT WHILE SOMEBODY IS USING THE COMPUTER. This is the only thing in the app that takes
        // the foreground from inside a role loop, and GameFocus's own header says focus is the
        // panic brake and must not be grabbed in a loop. A person typing at midnight must not have
        // EverQuest thrown in front of them mid-sentence. If the game is ALREADY in front, nothing
        // is being taken and the wait is skipped — that is the case where the user is watching the
        // run and wants it to carry on.
        if (!await WaitForAQuietMomentAsync(h, ct)) return false;

        if (!await GameFocus.BringAndSettleAsync(h, settleMs: 500))
        { RiLog("Re-Instance: couldn't bring the game to the front, so I can't read its windows."); return false; }

        // AND THE APP MUST GET OUT OF ITS OWN WAY. ScreenText copies ON-SCREEN pixels over the
        // game's rectangle — the only capture that works for this DirectX client — so anything
        // floating above the game is read as though it were the game. This window is topmost by
        // default and its own console is, right now, full of the words "instance", "charge" and
        // "Enter": it would satisfy the "is this the options window?" test with its own narration
        // and then be clicked. UpdateTopmost already steps aside for the auto-login for exactly
        // this reason; it had simply never been told about this.
        // THE try OPENS BEFORE THE FLAG IS SET, not after. With the flag raised outside it, an F12
        // during the 350 ms settle below cancels the delay, the exception leaves this method
        // without running the finally, and ReInstanceBusy stays true for the life of the process:
        // always-on-top never comes back — not even from its own toggle, because UpdateTopmost
        // keeps reading the flag — and the map overlay stays hidden behind a button that says
        // "Close overlay". A latch that outlives its own scope is not a latch, it is a leak.
        try
        {
            ReInstanceBusy = true;

            // AND MAKE IT SO NOW, not on the next UI tick. The flag is read by UpdateTopmost off a
            // 300 ms timer, and the first click of the sequence follows immediately: for those few
            // hundred milliseconds this window is still topmost over the game's menu bar, and a
            // click aimed at the instance icon lands on EQ Avatar instead. SafeClickPoint cannot
            // catch that — the game genuinely IS the foreground window and the point genuinely IS
            // inside its rectangle; something is merely drawn on top. The user then gets told to
            // re-pick an icon that was correct. So: apply it synchronously, hide the overlay, and
            // let the desktop settle before touching anything.
            try { await Dispatcher.InvokeAsync(() => { UpdateTopmost(); _overlay?.StepAside(true); }); }
            catch { /* dispatcher going away — the app is closing */ }
            await Task.Delay(350, ct);
            return await DriveInstanceUiAsync(h, logFolder, ct);
        }
        finally
        {
            // NOTHING AWAITED IN A finally. Dispatcher.InvokeAsync on a dispatcher that is shutting
            // down returns a CANCELLED operation, so awaiting it here throws TaskCanceledException
            // out of the finally — discarding the real answer, and landing in the role's silent
            // "operation cancelled" catch as though the user had pressed F12. Fire and forget, and
            // let the flag be the thing that is true.
            ReInstanceBusy = false;
            StepAsideForCapture(false);          // fire-and-forget: never await inside a finally
        }
    }

    /// <summary>
    /// True while the instance UI is being read and clicked. <see cref="UpdateTopmost"/> reads it
    /// on the 300 ms tick and keeps this window out of the way for as long as it is set — which is
    /// the only arrangement that works, because that tick will otherwise put the window straight
    /// back on top of the game a third of a second after anything lowers it.
    /// </summary>
    private volatile bool _reInstanceBusy;
    internal bool ReInstanceBusy
    {
        get => _reInstanceBusy;
        private set => _reInstanceBusy = value;
    }

    /// <summary>Hide the map overlay while something OCRs the game underneath. Unconditionally
    /// topmost and deliberately over the game, so it is read as though the game had drawn it.</summary>
    private void StepAsideForCapture(bool aside)
    {
        try { Dispatcher.BeginInvoke(new Action(() => { try { _overlay?.StepAside(aside); } catch { } })); }
        catch { /* dispatcher shutting down — the app is closing and nothing is being captured */ }
    }

    /// <summary>The click sequence itself, with the app's own windows already out of the frame.</summary>
    private async Task<bool> DriveInstanceUiAsync(IntPtr h, string logFolder, CancellationToken ct)
    {
        // 1) THE ICON.
        if (!SafeClickNormalized(h, _settings.InstanceIconNX, _settings.InstanceIconNY, ct, out string iconWhy))
        { RiLog("Re-Instance: " + iconWhy); return false; }
        RiLog("Re-Instance: clicked the instance icon — reading the menu.");
        await Task.Delay(InstanceMenuSettleMs, ct);

        // 2) THE MENU ROW. Which one is there depends on whether the game still thinks we are in
        //    an instance, and after an eject it should be Create — but "should" is not a thing to
        //    build on unattended, so both are accepted and neither is assumed.
        List<FoundText> menu = await ScreenText.ReadAsync(h);
        bool haveRow = InstanceWindow.FindSafeButton(menu, "Create", out Point row, out string createWhy);
        string rowWhy = createWhy;
        if (!haveRow)
        {
            // BOTH REASONS, not just the second one. The first version let the || overwrite
            // createWhy with the manage one, so after an eject — when the row on screen really is
            // "Create Instance" — the message the user got explained why "Manage" was not found,
            // and sent them to re-pick an icon that was working perfectly.
            haveRow = InstanceWindow.FindSafeButton(menu, "Manage", out row, out string manageWhy);
            rowWhy = haveRow ? manageWhy : $"Create: {createWhy}; Manage: {manageWhy}";
        }
        if (!haveRow)
        {
            RiLog($"Re-Instance: the instance menu didn't offer anything I could safely click ({rowWhy}). "
                + "If neither row was on screen at all, the icon pick has probably moved — re-pick it on the "
                + "Grind page.");
            return false;
        }
        if (!SafeClickPoint(h, row, ct, out string rowClickWhy)) { RiLog("Re-Instance: " + rowClickWhy); return false; }
        RiLog($"Re-Instance: clicked {rowWhy} — reading the instance options.");
        await Task.Delay(InstanceWindowSettleMs, ct);

        // 3) THE OPTIONS WINDOW. Identify it before pressing anything in it: if the click above
        //    missed, what is on screen now is the game, and hunting for a button called "Enter"
        //    in a zone full of chat is how a bot presses something nobody meant it to.
        List<FoundText> opts = await ScreenText.ReadAsync(h);
        if (!InstanceWindow.LooksLikeOptions(opts))
        {
            RiLog("Re-Instance: what came up doesn't look like the instance options window, so I'm not "
                + "clicking anything in it. Nothing has been changed.");
            return false;
        }

        // Hayden: the dropdowns (difficulty, solo/multiplayer, respawning) "default to the last
        // chosen setting". So they are left ALONE. Changing a setting the user last chose by hand,
        // unattended, to a value this app guessed, is not a thing worth being clever about.
        int? charges = InstanceWindow.ChargesLeft(opts);
        if (charges == 0)
        {
            string next = InstanceWindow.NextChargeIn(opts) ?? "";
            RiLog("Re-Instance: the window says there are no instance charges left"
                + (next.Length > 0 ? $" ({next})" : "")
                + ", so a new instance isn't possible right now. Stopping with the character alive.");
            return false;
        }

        if (!InstanceWindow.FindSafeButton(opts, "Enter", out Point enter, out string enterWhy))
        {
            RiLog($"Re-Instance: I would not press anything on that window — {enterWhy}. “Quit Instance” "
                + "sits next to “Enter” there, so when I can't tell them apart I press neither.");
            return false;
        }

        // From here the log is the witness, so start listening BEFORE the click: entering is fast
        // enough on a warm zone that a watcher started afterwards can miss the line it is waiting
        // for and time out on a re-entry that worked.
        Task<bool> zoned = WaitForZoneAsync(logFolder, InstanceZoneTimeout, ct);

        if (!SafeClickPoint(h, enter, ct, out string enterClickWhy))
        {
            RiLog("Re-Instance: " + enterClickWhy);
            return false;                              // the watcher dies with its own timeout
        }
        RiLog($"Re-Instance: pressed {enterWhy}"
            + (charges is int c ? $" ({c} charge{(c == 1 ? "" : "s")} showing)" : "")
            + " — waiting for the zone to confirm.");

        bool ok = await zoned;
        RiLog(ok
            ? "Re-Instance: the log says we zoned — we're in a new instance."
            : "Re-Instance: I pressed Enter but no zone line ever arrived, so I can't say we're inside. "
            + "Treating that as a failure rather than walking off on the assumption it worked.");
        return ok;
    }

    /// <summary>
    /// Wait until taking the foreground would not be rude — either the game already has it, or
    /// nobody has touched the machine for a while. Gives up after a few minutes rather than
    /// holding a role loop open indefinitely.
    /// </summary>
    private async Task<bool> WaitForAQuietMomentAsync(IntPtr h, CancellationToken ct)
    {
        var until = DateTime.UtcNow + ReInstanceQuietWait;
        bool said = false;
        while (!ct.IsCancellationRequested)
        {
            if (GameFocus.IsFront(h)) return true;
            if (UnattendedGuard.SecondsSinceInput() >= ReInstanceIdleSeconds) return true;
            if (DateTime.UtcNow > until)
            {
                RiLog("Re-Instance: you've been using the computer for the last few minutes, and I won't pull "
                    + "EverQuest in front of you to click through the instance window. Bring the game up and "
                    + "start the run again when you're ready.");
                return false;
            }
            if (!said)
            {
                said = true;
                RiLog("Re-Instance: the instance expired, but the computer is in use — waiting for a quiet moment "
                    + "before I take the foreground.");
            }
            await Task.Delay(5000, ct);
        }
        return false;
    }

    /// <summary>Click a point given as a fraction of the game window.</summary>
    private bool SafeClickNormalized(IntPtr h, double nx, double ny, CancellationToken ct, out string why)
    {
        if (!UgGetWindowRect(h, out UGRECT r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0)
        { why = "couldn't measure the game window (gone or rebuilding), so there was nowhere to click."; return false; }
        var rng = new Random();
        double x = r.Left + nx * (r.Right - r.Left) + rng.Next(-2, 3);
        double y = r.Top + ny * (r.Bottom - r.Top) + rng.Next(-2, 3);
        return SafeClickPoint(h, new Point(x, y), ct, out why);
    }

    /// <summary>
    /// The only click this file makes, and the three things it re-checks first.
    ///
    /// The point being clicked was computed from a screen read that finished HUNDREDS of
    /// milliseconds ago — an OCR pass over a whole game window, or a settle delay of over a
    /// second. In that time the game can lose the foreground to a notification, and the window can
    /// be moved. Both turn a carefully-chosen point into an absolute click at a coordinate that
    /// now belongs to something else, and one of the things it could belong to is "Quit Instance".
    ///
    /// So, immediately before the press and not a moment earlier:
    ///  • the run has not been stopped (F12 is the panic key and it must beat the last click);
    ///  • the game is still the foreground window — the same rule ForegroundSendInputSink.Ready
    ///    enforces for every keystroke this app sends, which this path would otherwise bypass;
    ///  • the point is still INSIDE the game's rectangle, which is what catches a moved window and
    ///    is the last line of defence against clicking into some other application entirely.
    /// </summary>
    private bool SafeClickPoint(IntPtr h, Point p, CancellationToken ct, out string why)
    {
        if (ct.IsCancellationRequested)
        { why = "the run was stopped before I pressed anything."; return false; }
        if (!GameFocus.IsFront(h))
        { why = "the game stopped being the front window between reading it and clicking, so I didn't click."; return false; }
        if (!UgGetWindowRect(h, out UGRECT r))
        { why = "couldn't measure the game window just before clicking, so I didn't click."; return false; }
        if (p.X < r.Left || p.X > r.Right || p.Y < r.Top || p.Y > r.Bottom)
        { why = $"the point I meant to click ({p.X:0}, {p.Y:0}) is no longer inside the game window — it must have moved. Not clicking."; return false; }

        var rng = new Random();
        HumanizedMouse.MoveInstant(p.X, p.Y);
        Thread.Sleep(120 + rng.Next(80));
        HumanizedMouse.Click(rng);
        why = "";
        return true;
    }

    // ---------------- Grind-page controls ----------------

    private void ReInstance_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReInstanceEnabled = ReInstanceBox.IsChecked == true;
        _settings.Save();
        GrindLogLine(_settings.ReInstanceEnabled
            ? "Re-Instance ON — when the instance expires I'll make a new one, walk to the shore point, then back "
              + "to camp, instead of stopping for the night. It needs the instance icon picked and (if there's "
              + "water between the zone-in and camp) a re-entry point."
            : "Re-Instance OFF — an expiry will park the run at the zone-in with the character alive.");
    }

    private void InstanceIconPick_Click(object sender, RoutedEventArgs e)
    {
        using System.Drawing.Bitmap? frame = VitalsSvc.CaptureFrame();
        if (frame is null)
        { GrindLogLine("No game window to capture — Target EverQuest first (and keep it on screen)."); return; }

        var dlg = new CompassPickWindow(frame,
            "Pick the instance icon",
            "Drag a box over the instance button on the game's menu bar — the one that opens the "
            + "create/manage instance menu. After the instance expires I'll click its centre, take "
            + "the Create (or Manage) row, and press Enter on the options window. I will never "
            + "press Quit or Leave: if I can't tell those apart from Enter, I press nothing.")
        { Owner = this };
        if (dlg.ShowDialog() != true) { UpdateReInstanceLabels(); return; }

        _settings.InstanceIconNX = dlg.NX + dlg.NW / 2;
        _settings.InstanceIconNY = dlg.NY + dlg.NH / 2;
        _settings.Save();
        UpdateReInstanceLabels();
        GrindLogLine($"Instance icon saved at {_settings.InstanceIconNX:0.000}, {_settings.InstanceIconNY:0.000} "
                   + "(normalized to the game window).");
    }

    /// <summary>
    /// Record the re-entry point from where the character is STANDING, rather than asking anyone
    /// to type three numbers.
    ///
    /// It is the spot the user walks to and says "here" — Hayden's is -384.16, -163.78, 4.60, the
    /// one place on Kerra Isle the land is shallow enough to walk out of the water. Typing it is
    /// possible and typing it wrong sends the character swimming at a number, so the button reads
    /// the position the same way everything else does: out of the log, through the parser that
    /// refuses anything a person said.
    /// </summary>
    private void ReEntryHere_Click(object sender, RoutedEventArgs e)
    {
        // The running hunt first — that is the position the navigation itself is steering by, so
        // saving it means the point the character walks to is the point it thinks it is standing
        // on. The map tap second, so this works with no run started at all.
        double? x = _hunt?.LastX ?? (double.IsNaN(_lastLocEw) ? null : _lastLocEw);
        double? y = _hunt?.LastY ?? (double.IsNaN(_lastLocNs) ? null : _lastLocNs);
        double? z = _hunt?.LastZ ?? (double.IsNaN(_lastLocZ) ? null : _lastLocZ);
        if (x is not double px || y is not double py)
        {
            GrindLogLine("I don't have a position to save yet. Start a run (or press your /loc key once with the "
                       + "log reader running) while standing on the spot, then press this again.");
            return;
        }
        if (z is not double pz)
        {
            // REFUSED RATHER THAN DEFAULTED. ReEntryZ is what the return walk uses to decide how
            // deep is "in the sea"; a silent 0 in a zone whose camp sits at z 200 collapses that
            // floor and switches the guard off for the whole walk home. The same "null is not
            // zero" rule the charge reader follows. /loc carries a Z, so this is a rare case and
            // asking again costs nothing.
            GrindLogLine("I have your position but not its altitude, and the return walk needs the altitude to "
                       + "know how deep is water. Press your /loc key once while standing on the spot, then press "
                       + "this again.");
            return;
        }
        _settings.ReEntryX = px;
        _settings.ReEntryY = py;
        _settings.ReEntryZ = pz;
        _settings.ReEntrySet = true;
        _settings.ReEntryZone = _charZoneStem ?? _mapZone ?? "";
        _settings.Save();
        UpdateReInstanceLabels();
        // PRINTED IN /loc ORDER — NS, EW, Z — because that is what the game shows the user and
        // what every other position line in this app prints (see the tether-anchor line). The
        // numbers are stored EW-first internally; transposing them in the message only teaches the
        // user to distrust a pick that was correct.
        GrindLogLine($"Re-entry point saved: /loc {py:0.00}, {px:0.00}, {pz:0.00}"
                   + (_settings.ReEntryZone.Length > 0 ? $" in {_settings.ReEntryZone}" : " (zone unknown)") + ". After a re-instance I'll walk here "
                   + "first and then to camp — and if the character ever ends up in deep water, it swims for this "
                   + "point instead of back the way it fell in.");
    }

    private void UpdateReInstanceLabels()
    {
        bool icon = _settings.InstanceIconNX > 0 || _settings.InstanceIconNY > 0;
        InstanceIconLabel.Text = icon ? "icon ✓" : "icon: not picked";
        ReEntryLabel.Text = _settings.ReEntrySet
            ? $"re-entry ✓ ({_settings.ReEntryY:0}, {_settings.ReEntryX:0}"
              + (_settings.ReEntryZone.Length > 0 ? $", {_settings.ReEntryZone}" : "") + ")"
            : "re-entry: not set";
    }

    private void InitReInstanceUi()
    {
        ReInstanceBox.IsChecked = _settings.ReInstanceEnabled;
        UpdateReInstanceLabels();
    }
}
