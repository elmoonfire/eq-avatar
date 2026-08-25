using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Log;
using EQAvatar.Spike.Login;

namespace EQAvatar.Spike;

/// <summary>
/// Bringing the game BACK — the half of unattended running that keep-alive cannot do.
///
/// THE MEASUREMENT THIS IS BUILT ON. Three instrumented overnight closes, three causes:
///   08-23  a death → the respawn window took input away → idle kick → END_GAME
///   08-24  a stolen focus (a desktop context menu) → the run paused → idle kick → END_GAME
///   08-25  a Tuesday PATCH — the character was mid-fight, killed a spahi at 04:59:57, and the
///          world terminated the session at 05:00:55. The client shut down cleanly. Nothing was
///          broken and nothing could have prevented it.
/// 0.10.55 fixed the first two by never letting input stop. The third is not preventable at all:
/// the right answer to an outage is to sit it out and come back, which is what this does.
///
/// WHAT IT WILL NOT DO, and why each guard is here rather than a nice-to-have:
///  • It never starts unless a role was ACTUALLY RUNNING when the window died. A game closed
///    while nobody was grinding is a game the user closed.
///  • It never starts while a person is at the keyboard (the same measured idle gate the
///    unattended guard uses). If you are there, relaunching your game out from under you is
///    the rudest thing this app could do.
///  • It never starts when the client's own log says the session was CAMPED OUT deliberately.
///  • It is BOUNDED — `RecoverMaxAttempts` tries and a hard four-hour ceiling. An unbounded
///    relaunch loop against a login server is how an account gets noticed, and a lost night is
///    the lesser problem.
///  • F12 kills it like everything else.
///
/// WHY IT WAITS DIFFERENT AMOUNTS. <see cref="CloseReason"/> reads the client's own dbg.txt and
/// says whether the world went away or the client fell over. A patch is not answering in sixty
/// seconds, so the first attempt is ten minutes out and the backoff is generous; a crash can be
/// retried almost at once. Guessing one wait for both would either hammer a patching server or
/// waste half an hour after a crash.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _recoverCts;
    /// <summary>True only across the recovery's OWN call to the start button. `_recoverBusy` cannot
    /// serve here: it stays true until RecoverAsync's finally, which runs AFTER the restart, so the
    /// obvious test ("am I busy?") answered yes for both the recovery's restart and a person's —
    /// and a person's start then never cancelled the recovery. That left a second launcher able to
    /// fire on top of a live session the user had just started by hand.</summary>
    private bool _recoverRestarting;
    /// <summary>Set around the recovery's own call to the stop handler, so tearing down the paused
    /// roles at the start of a recovery is not mistaken for the user pressing Stop.</summary>
    private bool _recoverStopping;
    /// <summary>Which role to restart once we are back in the world — captured at the moment the
    /// window died, because by the time the verdict lands the role objects may have been stopped.</summary>
    private string? _recoverRole;
    private bool _recoverBusy;

    /// <summary>Backoff between attempts, in minutes, after the first (reason-chosen) wait.
    /// Rises and then flattens: if the world is still down after an hour it is a long patch, and
    /// a half-hourly knock is the polite version of waiting.</summary>
    private static readonly int[] RecoverBackoffMinutes = { 10, 15, 20, 30, 30, 30 };
    private const int RecoverCeilingHours = 4;

    /// <summary>Called from the tick the instant the window is found dead, BEFORE anything stops
    /// the roles — this is the only moment the answer is still available.</summary>
    private void RememberRunningRoleForRecovery()
    {
        _recoverRole = _hunt is { Running: true } ? "hunt"
                     : _grind is { Running: true } ? "grind"
                     // A HELD SESSION COUNTS AS A RUN IN PROGRESS. After a death the role stops by
                     // design and the guard keeps the client alive; if the client dies anyway,
                     // "no role was running" would abandon the night on the very failure 0.10.55
                     // exists to survive. The client is recovered — but see RestartRole: a
                     // character sitting at its bind point is NOT sent hunting again.
                     : GuardWasHolding ? "hold"
                     : null;
    }

    /// <summary>The window came back — it was a rebuild, not a close. Drop the remembered role so
    /// it can never be spent on a later, unrelated close.</summary>
    private void ForgetRecoveryRole() { if (!_recoverBusy) _recoverRole = null; }

    /// <summary>Called from the death verdict once the PROCESS is confirmed gone.</summary>
    private void ConsiderRecovery()
    {
        if (_recoverBusy) return;
        if (!_settings.AutoRecoverEnabled) return;
        if (_recoverRole is null)
        {
            // Not a failure — the common case. Said once, quietly, so the absence of a relaunch
            // never looks like the feature silently not working.
            GrindLogLine("No role was running when the game closed, so I'm not relaunching it.");
            return;
        }
        _recoverBusy = true;
        _recoverCts = new CancellationTokenSource();
        _ = RecoverAsync(_recoverRole, _recoverCts.Token);
    }

    /// <summary>F12 and any manual start cancel a recovery in flight.</summary>
    private void CancelRecovery(string why)
    {
        if (_recoverCts is null) return;
        // Only speak if something was actually in flight. A completed recovery used to leave its
        // token behind, so the next F12 announced cancelling a recovery that had finished hours ago.
        bool live = _recoverBusy;
        try { _recoverCts.Cancel(); } catch { }
        _recoverCts.Dispose();
        _recoverCts = null;
        _recoverBusy = false;
        // AND THE LOGIN IT STARTED. Cancelling the loop while AutoLogin was mid-sequence left it
        // clicking through launcher screens with nothing supervising it — and if the cancel came
        // from the user starting a run by hand, that was two automations driving one window.
        if (live) { _login?.Stop(); GrindLogLine("Auto-recover cancelled — " + why + "."); }
    }

    private async Task RecoverAsync(string role, CancellationToken ct)
    {
        try
        {
            string logFolder = LogFolderBox.Text.Trim();
            CloseReason.Verdict verdict = CloseReason.FromLogFolder(logFolder);
            GrindLogLine($"Reading the client's own dbg.txt: {verdict.Say}.");
            if (verdict.Evidence.Length > 0) GrindLogLine("  evidence: " + verdict.Evidence);

            if (verdict.Kind == CloseKind.UserQuit)
            { GrindLogLine("That was a deliberate camp-out, so I'm leaving it closed."); return; }

            string launcher = _settings.LauncherPath;
            if (string.IsNullOrWhiteSpace(launcher) || !File.Exists(launcher))
            { GrindLogLine($"Auto-recover can't run: the launcher path '{launcher}' doesn't exist. Set it on the Launch page."); return; }

            // CHECKED ONCE, UP FRONT. The zone line is the only proof of being back in the world,
            // so with no readable character log every attempt is guaranteed to "fail" after its
            // twelve-minute wait — six relaunches over four hours, each one torn down by the
            // failure handler, all reporting a timeout that was never winnable.
            if (EqLogWatcher.FindNewestLog(logFolder) is null)
            { GrindLogLine($"Auto-recover can't run: no character log found in '{logFolder}', so I'd have no way to know when the character is back in the world. Set the log folder on the Log Reader panel."); return; }

            // Stop the roles for real. They are only PAUSED right now — the foreground sink went
            // unready when the window died and every role loop is sitting in its "paused" branch
            // waiting for a window that is never coming back. Restarting on top of that would
            // leave two loops driving one character.
            _recoverStopping = true;
            try { StopGrind_Click(this, new RoutedEventArgs()); }
            finally { _recoverStopping = false; }
            // Disarm the death-hold for the duration. Its keep-alive tap is harmless input, but
            // it would be a SECOND automation touching the window while the auto-login clicks
            // through character select, and "only one thing drives the game at a time" is worth
            // more than the tap. The hold path re-arms it after the zone-in.
            EnsureGuard().HoldSession = false;

            DateTime ceiling = DateTime.UtcNow.AddHours(RecoverCeilingHours);
            int maxAttempts = Math.Clamp(_settings.RecoverMaxAttempts, 1, 12);
            TimeSpan wait = verdict.FirstWait;

            for (int attempt = 1; attempt <= maxAttempts && !ct.IsCancellationRequested; attempt++)
            {
                GrindLogLine($"Auto-recover attempt {attempt}/{maxAttempts}: waiting {wait.TotalMinutes:0} minutes, "
                           + "then relaunching the game and logging back in.");
                if (!await WaitUnattended(wait, ceiling, ct)) return;

                GrindLogLine("Relaunching EverQuest…");
                BeginLaunch(startLauncher: true);

                // GROUND TRUTH FOR "WE ARE BACK IN". Not the launcher's PLAY click, not the
                // Enter World click — the client printing that it entered a zone. Everything
                // earlier is a click this app made and can be wrong about; the zone line is the
                // server confirming the character is standing in the world. The window is wide
                // because a patch day is exactly when the launcher takes a long time.
                bool inWorld = await WaitForZoneAsync(logFolder, TimeSpan.FromMinutes(12), ct);
                if (ct.IsCancellationRequested) { _login?.Stop(); return; }

                if (inWorld)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    if (role == "hold")
                    {
                        // Back in, but the character died before all this and is standing at its
                        // bind point. Resuming the hunt from there is the exact sequence that
                        // walked it into the sea on 08-23. Hold the session instead and say so.
                        EnsureGuard().HoldSession = true;
                        GrindLogLine("Back in the world. The character had died before the client closed, so it is "
                                   + "wherever it respawned — I'm holding the session alive rather than hunting on "
                                   + "from there. Start the run when you've put it back at camp.");
                        return;
                    }
                    GrindLogLine("Back in the world. Letting it settle, then restarting the run.");
                    RestartRole(role);
                    return;
                }

                _login?.Stop();
                GrindLogLine("That attempt didn't reach the world" +
                             (attempt < maxAttempts ? " — backing off and trying again." : "."));
                wait = TimeSpan.FromMinutes(RecoverBackoffMinutes[Math.Min(attempt - 1, RecoverBackoffMinutes.Length - 1)]);
                if (DateTime.UtcNow + wait > ceiling)
                { GrindLogLine($"Auto-recover has been trying for {RecoverCeilingHours} hours — stopping. The game is closed and waiting for you."); return; }
            }
            if (!ct.IsCancellationRequested)
                GrindLogLine($"Auto-recover gave up after {maxAttempts} attempts. The game is closed and waiting for you.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GrindLogLine("Auto-recover error: " + ex.Message); }
        finally { _recoverBusy = false; _recoverRole = null; }
    }

    /// <summary>
    /// Sleep out a wait, but only while the machine STAYS unattended and the feature stays on.
    ///
    /// A person who sits down during a ten-minute patch wait must not have a launcher thrown at
    /// them, so presence pauses the clock rather than cancelling — they may well get up again,
    /// and the run should still come back. Returns false when the recovery should abandon.
    /// </summary>
    private async Task<bool> WaitUnattended(TimeSpan wait, DateTime ceiling, CancellationToken ct)
    {
        DateTime graceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(70);
        // FLOORED AT THE GRACE, because a wait shorter than the grace skips the presence check
        // entirely: the crash verdict waits 60s, the grace ran 70s, and every sample inside that
        // minute declined to look at the input counter — so the one guard that says "never start
        // while a person is at the keyboard" was inoperative for the default verdict, which is
        // also the one that fires when dbg.txt cannot be read at all.
        DateTime due = DateTime.UtcNow + wait;
        if (due < graceUntil) due = graceUntil;
        bool saidPresent = false;
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > ceiling)
            { GrindLogLine("Auto-recover hit its time ceiling while waiting — stopping."); return false; }
            if (!_settings.AutoRecoverEnabled)
            { GrindLogLine("Auto-recover was switched off while waiting — stopping."); return false; }

            // A GRACE WINDOW, because the input counter is not yet about a person.
            // GetLastInputInfo counts this app's OWN synthesized input, and a role was driving the
            // game until seconds ago, so for the first minute a low reading means us, not someone
            // at the desk. The first attempt to fix this latched on "have I ever seen 60s idle?"
            // — which a continuously active user never satisfies, so the gate was dead for exactly
            // the person it protects. Waiting out one input-idle period instead is honest for
            // both: after it, a low counter can only be a human (every role is stopped, and the
            // auto-login only runs INSIDE an attempt, never during this wait).
            bool present = DateTime.UtcNow > graceUntil && UnattendedGuard.SecondsSinceInput() < 60;

            if (present)
            {
                if (!saidPresent)
                { saidPresent = true; GrindLogLine("You're at the keyboard, so I'm holding the relaunch until the machine is idle again."); }
                // EXTENDED, NEVER SHORTENED. Assigning `now + 1 minute` outright turned a
                // ten-minute patch wait into a one-minute one the moment anything touched the
                // input counter — which, per the note above, it always did.
                DateTime push = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                if (push > due) due = push;
            }
            else if (saidPresent)
            { saidPresent = false; GrindLogLine("Machine is idle again — the relaunch countdown is running."); }

            if (!present && DateTime.UtcNow >= due) return true;
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        return false;
    }

    /// <summary>
    /// Wait for the client to say it entered a zone. Tails the character log directly rather than
    /// asking any role, because during a recovery no role exists yet — this IS the thing that
    /// decides whether one may be started.
    /// </summary>
    private static async Task<bool> WaitForZoneAsync(string logFolder, TimeSpan timeout, CancellationToken ct)
    {
        string? path = EqLogWatcher.FindNewestLog(logFolder);
        if (path is null) return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new EqLogWatcher(path);
        void OnLine(string raw)
        {
            // The same parser the roles use, so a zone line a player TYPED cannot satisfy this —
            // "I have entered the chat" in General must never restart a grind.
            if (LogEventParser.Parse(raw).Kind == LogEventKind.Zone) tcs.TrySetResult(true);
        }
        watcher.LineRead += OnLine;
        watcher.Start(fromStart: false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var delay = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            Task done = await Task.WhenAny(tcs.Task, delay);
            return done == tcs.Task;
        }
        catch (OperationCanceledException) { return false; }
        finally { watcher.LineRead -= OnLine; }
    }

    /// <summary>Press the same button the user would have pressed. Deliberately NOT a private
    /// re-implementation of the start path: every guard, banner, session record and settings read
    /// on that path applies to a recovered run exactly as it does to a hand-started one.</summary>
    private void RestartRole(string role)
    {
        GrindLogLine($"Restarting the run (it was the {role} role when the game closed).");
        _recoverRestarting = true;
        try { StartGrind_Click(this, new RoutedEventArgs()); }
        finally { _recoverRestarting = false; }

        // REPORT WHAT ACTUALLY STARTED. StartGrind_Click branches on the page's own checkboxes,
        // which may have been changed since; announcing the remembered role as though it were the
        // started one would be the app telling the user something it did not check.
        string? now = _hunt is { Running: true } ? "hunt" : _grind is { Running: true } ? "grind" : null;
        if (now is null)
            GrindLogLine("The restart didn't take — the Grind page banner will say why. The game is up; the run is not.");
        else if (now != role)
            GrindLogLine($"Note: the page is set to the {now} role now, so that is what restarted, not {role}.");
    }

    private void AutoRecover_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoRecoverEnabled = AutoRecoverBox.IsChecked == true;
        _settings.Save();
        GrindLogLine(_settings.AutoRecoverEnabled
            ? "Auto-recover ON — if the game closes while a run is going and you're away, I'll wait out the "
              + "outage, relaunch, log back in, and restart the run. Bounded: "
              + $"{Math.Clamp(_settings.RecoverMaxAttempts, 1, 12)} attempts, {RecoverCeilingHours} hours."
            : "Auto-recover OFF — a game that closes overnight stays closed.");
        if (!_settings.AutoRecoverEnabled) CancelRecovery("switched off");
    }

    private void InitRecoveryUi() => AutoRecoverBox.IsChecked = _settings.AutoRecoverEnabled;
}
