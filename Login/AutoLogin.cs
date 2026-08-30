using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Input;

namespace EQAvatar.Spike.Login;

/// <summary>
/// Drives the launch sequence with screen text: LaunchPad's green PLAY → server select
/// (pick the configured server, then PLAY) → character select's "Enter World". It re-finds
/// the EverQuest/LaunchPad window every pass, so it survives the LaunchPad→game handoff
/// (they are different processes). Foreground/real-desktop method; the injection path reuses
/// it once the game is controllable in the background.
/// </summary>
public sealed class AutoLogin
{
    public event Action<string>? Log;
    public event Action? Done;

    private readonly string _server;
    private readonly AppSettings _settings;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;

    /// <summary>If set, this exe is started before OCR begins (so Launch can start the game itself).</summary>
    public string LauncherPath { get; set; } = "";
    private bool _launcherStarted;
    private string _lastSeen = "";

    /// <summary>
    /// How often the login screens are re-read.
    ///
    /// THE OLD SLEEPS WERE THE BUG. After clicking PLAY this loop slept a flat 3.5 s (launcher) or
    /// 3 s + 1.2 s (server select) before looking again, and the character screen usually appears
    /// somewhere inside that window — so the bot sat at character select doing nothing for most of
    /// four seconds, every launch. Hayden: "the bot lingers on the character select screen for too
    /// long before logging in. It shouldn't sit there for longer than 2 seconds."
    ///
    /// Those sleeps were doing two jobs at once: waiting for the screen to change, and not clicking
    /// the same button twice while it hadn't. Only the second one is real, and a per-button cooldown
    /// does it properly — so the loop is free to LOOK as often as it likes. It now spots the
    /// character screen within about one poll of it appearing.
    /// </summary>
    private const int PollMs = 400;

    /// <summary>Don't press the same PLAY again until the screen has had a fair chance to change.
    /// These are the old sleeps, doing the one job they were actually needed for.</summary>
    private const double LauncherPlayCoolSec = 3.5;
    private const double ServerPlayCoolSec = 3.0;

    // TickCount64, NOT DateTime.Now — the rule this codebase already wrote down in HuntRole and
    // which the first draft of this change broke. Every one of these is a "was it recent?" test,
    // and wall-clock arithmetic across a daylight-saving step or an NTP correction produces a
    // NEGATIVE difference that sails under any such test. An unattended re-login at 01:59 on a
    // fall-back night would have found every cooldown permanently true, clicked nothing ever
    // again, and — because the eight-minute deadline was computed off the same clock — not even
    // timed out for another hour. Task.Delay was monotonic; these have to be too.
    private long _lastLauncherPlay = long.MinValue / 4;
    private long _lastServerPlay = long.MinValue / 4;
    private long _lastServerPick = long.MinValue / 4;
    private long _saidNoEnterWorld = long.MinValue / 4;
    private long _saidWaitingWindow = long.MinValue / 4;
    private long _saidWaitingScreen = long.MinValue / 4;
    private long _saidSeen = long.MinValue / 4;
    /// <summary>When the character screen was first recognised. With every console line now
    /// stamped, the gap between that line and the click is readable straight off the log.</summary>
    private long _charSelectSeen = long.MinValue / 4;
    /// <summary>When this launch began, for the total on the last line.</summary>
    private long _startedTicks;

    /// <summary>Say something at most once every <paramref name="sec"/> seconds. The loop looks ten
    /// times more often than it used to, and a line that was reasonable at one every second and a
    /// half writes a hundred and fifty a minute at four hundred milliseconds — burying the OCR dump
    /// that is the only thing able to explain why it is still waiting.</summary>
    private void LogOccasionally(ref long stamp, double sec, string msg)
    {
        if (Cooling(stamp, sec)) return;
        stamp = Environment.TickCount64;
        Log?.Invoke(msg);
    }

    private static bool Cooling(long lastTicks, double sec) => Environment.TickCount64 - lastTicks < sec * 1000;

    /// <summary>
    /// Find a button whose label IS this word — not one that merely contains it.
    ///
    /// ⚠ FROM HAYDEN'S 08-29 CONSOLE, and it is worse than a near miss. ScreenText.Find is a
    /// case-insensitive SUBSTRING match, and one pass OCR'd his Windows TASKBAR instead of the
    /// launcher:
    ///
    ///   OCR sees: EQ | Rocket League | Synergy | VLC media player | Telegram | Driver Booster 13
    ///           | PDF-XCha.. Editor | Roblox Studio | Playe | Roblox Playe | AMD Ryzen Master …
    ///   Clicked PLAY (launcher, by text).
    ///
    /// "Playe" contains "play". The app clicked the Roblox Player button on his taskbar and
    /// announced it as pressing PLAY on the launcher. Every other word on that list is a program
    /// too — "Player", "Last Played", "Replay" are all one bad OCR pass away from being a click on
    /// something nobody asked for.
    ///
    /// So a button label has to match EXACTLY. ScreenText emits every WORD at its own centre and
    /// then the whole LINE at the average of those centres, so an exact test naturally lands on the
    /// word itself; the near-duplicate line entry for a one-word button sits at the same point and
    /// is harmless. Anything OCR renders as part of a longer run is left to the green-button
    /// fallback, which is a colour test and cannot be fooled by a neighbouring program's name.
    /// </summary>
    private static bool FindWord(List<FoundText> items, string word, out Point center)
    {
        // A WORD BOUNDARY, NOT AN EXACT MATCH. Exact was the first draft and review found it too
        // strict in the one place with no safety net: the launcher has a green-button fallback when
        // OCR mangles PLAY, and the SERVER screen cannot have one (it is the game window, and the
        // fallback is gated to non-game windows). The same OCR that returns "Charactee" for Create
        // Character returns "PLAY!" or "PLAY NOW" for a PLAY button, and demanding equality there
        // would re-click the server row for eight minutes and never press it.
        //
        // \b keeps everything that reads as the word and rejects everything that merely contains
        // its letters: "PLAY!" and "PLAY NOW" match; "Playe", "Played", "player" and "Replay" do
        // not — which is the whole point, because "Roblox Playe" is what it clicked.
        var re = new Regex($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        foreach (FoundText f in items)
            if (re.IsMatch(f.Text))
            { center = new Point(f.X, f.Y); return true; }
        center = default;
        return false;
    }

    /// <summary>THE LOOP IS ALIVE — not "cancel wasn't requested", which is what this used to
    /// test. The loop also ENDS by itself: reaching the game, the 8-minute timeout, an error. None
    /// of those cancel anything, so the old test stayed true forever after the first successful
    /// launch — and since Launch refuses while Running, closing the game and pressing Launch again
    /// got "already running" until the whole app was restarted. The house rule exists because of
    /// exactly this shape, and this class predates the rule.</summary>
    public bool Running => _alive;
    private volatile bool _alive;

    public AutoLogin(string server, AppSettings settings)
    {
        _server = string.IsNullOrWhiteSpace(server) ? "Rivervale" : server.Trim();
        _settings = settings;
    }

    public void Start()
    {
        if (Running) return;
        _alive = true;                     // set HERE, not in the task — Start() twice in one tick
        _cts = new CancellationTokenSource();   // must see the first one as already running
        _ = Task.Run(() => Loop(_cts.Token));
    }

    /// <summary>Idempotent and quiet about it: the panic key calls this on every press, and a
    /// console line claiming to have stopped a launch that wasn't running reads as a bug.</summary>
    public void Stop()
    {
        if (!Running) return;
        _cts?.Cancel();
        Log?.Invoke("Auto-login stopped.");
    }

    private async Task Loop(CancellationToken ct)
    {
        try
        {
        _startedTicks = Environment.TickCount64;
        Log?.Invoke($"Auto-login started (server: {_server}). Leave the launcher/game in front.");
        long deadline = Environment.TickCount64 + 8 * 60_000;   // generous — the launcher can update for a while
        try
        {
            while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                (IntPtr w, bool isGame) = FindLaunchTarget();
                if (w == IntPtr.Zero)
                {
                    if (!_launcherStarted && !string.IsNullOrWhiteSpace(LauncherPath) && File.Exists(LauncherPath))
                    {
                        _launcherStarted = true;
                        try { Process.Start(new ProcessStartInfo(LauncherPath) { UseShellExecute = true }); Log?.Invoke("Started launcher: " + LauncherPath); }
                        catch (Exception ex) { Log?.Invoke("Couldn't start launcher: " + ex.Message); }
                    }
                    else if (!_launcherStarted)
                        Log?.Invoke("No launcher/game window yet — set the launcher path (Set launcher…) so I can start it, or open it yourself.");
                    LogOccasionally(ref _saidWaitingWindow, 5, "Waiting for the EverQuest LaunchPad / game window…");
                    await Task.Delay(1500, ct);
                    continue;
                }

                // ONLY WHEN IT ISN'T ALREADY IN FRONT. At a 400 ms poll a blind
                // SetForegroundWindow + 300 ms settle on every pass would spend most of the loop
                // settling a window that never moved — and re-raising the game once a second is
                // its own kind of rude.
                if (GetForegroundWindow() != w)
                {
                    SetForegroundWindow(w);
                    await Task.Delay(300, ct);
                }
                var found = await ScreenText.ReadAsync(w);
                // Rate-limited as well as deduped: on the LaunchPad's patch screen the percentage
                // changes every frame, so "has the text changed?" is true on every single pass —
                // making the one varying line the only one the flood guard did not cover.
                if (!Cooling(_saidSeen, 1.5)) { _saidSeen = Environment.TickCount64; LogSeen(found); }

                // 1) Character select — click the green 'Enter World' in the LEFT menu.
                //
                // ⚠ WHAT THIS SCREEN ACTUALLY OCRs AS, from Hayden's console on 08-29. Every one of
                // the three phrases this used to look for is stylized art on his client, and OCR
                // reads none of them reliably:
                //
                //   "Enter World"      → never read once, in either login
                //   "Create Character" → "Charactee", "Chåeactéf", "ICreate Character"
                //   "Return Home"      → absent entirely
                //
                // So the test was false on pass after pass while the character screen sat there in
                // plain sight, and the bot only got in when OCR happened to render Create Character
                // legibly. In the pasted log that took roughly THIRTY-FIVE SECONDS and about
                // eighteen OCR passes — the "lingers too long" Hayden reported, and the reason
                // making the loop poll faster (0.10.63) barely helped: a faster loop asking a
                // question that is nearly always "no" just gets told "no" more often.
                //
                // What IS on every single one of those dumps is the panel header "Characters" and
                // the "Reset UI to Default" button. Those are ordinary UI text, not the stylized
                // art, which is exactly why OCR can read them. Neither appears on the launcher or
                // the server screen (checked against the dumps of both), and the click this
                // unlocks is confined to the left-menu column, where the launcher's green buttons
                // — at x≈1505 and x≈1946 on a 2560-wide screen — cannot reach.
                //
                // "Reset" is deliberately matched on its own: the tail of that button came back as
                // "to Default", "to Detault" and "LII to Default" across passes, and the one word
                // that survived every reading is the first one.
                // AND ONLY IN THE GAME WINDOW. Character select does not exist anywhere else, and
                // without this gate a launcher frame — whose patch notes are ordinary OCR-friendly
                // text and can easily say "Characters" — would enter this branch, click whatever
                // scattered green the left column happened to contain, raise Done, and print
                // "Reached the game. Launch complete." over a launch that had not started.
                //
                // A "Reset"+"Default" signal was in the first draft and is gone: those are two
                // unanchored substrings that appear together in EQ's own Options window, AMD Ryzen
                // Master and Driver Booster — two of which are on Hayden's taskbar — and unlike the
                // launcher case, isGame does not save you from the game's own Options panel.
                bool charSelect = isGame
                               && (ScreenText.Find(found, "Enter World", out Point pEnter)
                                || ScreenText.Find(found, "Create Character", out _)
                                || ScreenText.Find(found, "Return Home", out _)
                                || ScreenText.Find(found, "Characters", out _));
                if (!charSelect) pEnter = default;
                if (charSelect)
                {
                    if (_charSelectSeen == long.MinValue / 4)
                    {
                        _charSelectSeen = Environment.TickCount64;
                        Log?.Invoke("Character screen recognised.");
                    }
                    if (ScreenText.Find(found, "Enter World", out pEnter))
                    {
                        Click(pEnter);
                        Log?.Invoke($"Clicked 'Enter World' (by text) {SinceStart()} — logging into the game.");
                        Done?.Invoke();
                        return;
                    }
                    // OCR can't read the stylized green text — it's the only green in the left menu column.
                    if (ScreenText.FindGreenButton(w, 0.0, 0.30, 0.20, 0.99, 30) is Point pGreenEW)
                    {
                        Click(pGreenEW);
                        Log?.Invoke($"OCR couldn't read 'Enter World' — clicked the green button in the left menu at "
                                  + $"{pGreenEW.X:0},{pGreenEW.Y:0} {SinceStart()}. Logging in.");
                        Done?.Invoke();
                        return;
                    }
                    // RATE-LIMITED, because at a 400 ms poll this would otherwise write the same
                    // sentence a hundred and fifty times a minute and bury the OCR dump that is the
                    // only thing able to explain it.
                    // FALL THROUGH, don't `continue`. This branch sits in front of the server and
                    // launcher ones, so a frame that looked like character select but had no green
                    // button in the left column used to starve BOTH of them — the login then
                    // repeated one sentence every five seconds for the full eight minutes without
                    // pressing anything. Letting it drop into the next branch means a false
                    // positive costs a wasted test rather than the whole launch.
                    LogOccasionally(ref _saidNoEnterWorld, 5,
                        "Something looked like character select, but there is no 'Enter World' and no green button "
                      + "in the left menu — carrying on with the other screens.");
                }

                // 2) Server select — choose the server, then PLAY.
                // ⚠ THE CHARACTER SCREEN ALSO SHOWS THE SERVER NAME, so this branch can be
                // entered while standing at character select — and it then clicks the server name
                // as though it were a list row. I tried tightening it to require PLAY as well, and
                // review showed that trades a cosmetic problem for a hard one: on a server screen
                // whose PLAY is dim until a row is SELECTED, clicking the row is exactly what makes
                // PLAY readable, so requiring PLAY first removes the bootstrap and the login
                // dead-ends for the full eight minutes with no fallback (this screen is the game
                // window, so the green-button rescue below is gated off).
                //
                // Telling the two screens apart properly needs the one thing I do not have: what
                // OCR actually reads on each of them. The console prints it every pass as
                // "OCR sees: …". Get that line for the character screen and this becomes a fact
                // instead of a guess. Until then the old, working behaviour stands.
                if (ScreenText.Find(found, "SELECTION", out _) || ScreenText.Find(found, _server, out _))
                {
                    // Still the server screen a moment after we pressed PLAY on it — that is the
                    // screen taking its time, not a press that missed. Keep looking, don't re-press.
                    if (Cooling(_lastServerPlay, ServerPlayCoolSec)) { await Task.Delay(Vary(PollMs), ct); continue; }

                    // THE ROW HAS ITS OWN COOLDOWN. The branch gate stamps only when PLAY is
                    // found, so a server screen whose stylized PLAY OCR cannot read left this
                    // clicking the server row about once a second, for eight minutes, announcing
                    // it every time. It is the one button in this file that had no guard.
                    if (ScreenText.Find(found, _server, out Point pServer) && !Cooling(_lastServerPick, 2.5))
                    {
                        _lastServerPick = Environment.TickCount64;
                        Click(pServer);
                        Log?.Invoke($"Selected server '{_server}'.");
                        await Task.Delay(700, ct);
                    }
                    var f2 = await ScreenText.ReadAsync(w);
                    if (FindWord(f2, "PLAY", out Point pPlay2))
                    {
                        Click(pPlay2);
                        _lastServerPlay = Environment.TickCount64;
                        Log?.Invoke("Clicked PLAY (server select) — watching for the character screen.");
                    }
                    await Task.Delay(Vary(PollMs), ct);
                    continue;
                }

                // 3) LaunchPad — click PLAY. Try OCR text first, then the green-button colour fallback.
                if (FindWord(found, "PLAY", out Point pPlay))
                {
                    if (Cooling(_lastLauncherPlay, LauncherPlayCoolSec)) { await Task.Delay(Vary(PollMs), ct); continue; }
                    Click(pPlay);
                    _lastLauncherPlay = Environment.TickCount64;
                    Log?.Invoke("Clicked PLAY (launcher, by text). Watching for the next screen…");
                    await Task.Delay(Vary(PollMs), ct);
                    continue;
                }
                // THE COOLDOWN IS TESTED FIRST, because FindGreenButton is not a cheap predicate:
                // it captures the whole window and scans over a million pixels. Behind the `if` it
                // ran on eight passes out of nine only to have its answer discarded.
                if (!isGame && !Cooling(_lastLauncherPlay, LauncherPlayCoolSec)
                    && ScreenText.FindGreenButton(w) is Point pGreen)
                {
                    Click(pGreen);
                    _lastLauncherPlay = Environment.TickCount64;
                    Log?.Invoke($"OCR couldn't read 'PLAY' — clicked the green button at {pGreen.X:0},{pGreen.Y:0}. If that's not PLAY, tell me roughly where PLAY sits.");
                    await Task.Delay(Vary(PollMs), ct);
                    continue;
                }

                LogOccasionally(ref _saidWaitingScreen, 5, isGame
                    ? "Game window focused — waiting for the server or character screen…"
                    : "Launcher focused but no PLAY text or green button found yet — waiting…");
                await Task.Delay(Vary(PollMs), ct);
            }
            Log?.Invoke(ct.IsCancellationRequested ? "Auto-login cancelled." : "Auto-login timed out (8 min).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("Auto-login error: " + ex.Message); }
        }
        finally { _alive = false; }        // EVERY exit — success, timeout, cancel, throw
    }

    /// <summary>Print the distinct text OCR found this pass (only when it changes) so a missed
    /// click can be diagnosed — you can tell me exactly what the launcher screen read as.</summary>
    private void LogSeen(List<FoundText> found)
    {
        var texts = new List<string>();
        foreach (FoundText f in found)
        {
            string s = f.Text.Trim();
            if (s.Length >= 2 && !texts.Contains(s)) texts.Add(s);
        }
        string joined = string.Join(" | ", texts);
        if (joined.Length > 240) joined = joined.Substring(0, 240) + "…";
        if (joined != _lastSeen)
        {
            _lastSeen = joined;
            Log?.Invoke("OCR sees: " + (joined.Length == 0 ? "(no text recognized on this screen)" : joined));
        }
    }

    /// <summary>
    /// How long this whole launch has taken, on the line that ends it.
    ///
    /// The first draft measured from "character screen recognised" to the click — and recognition
    /// and click happen in the SAME loop iteration, so it printed "(0.0s after seeing it)" every
    /// time. It would have printed that during the thirty-five-second stall too, because the stall
    /// was time spent NOT recognising the screen. The honest number is the one the user is timing
    /// with a wristwatch: how long from pressing Launch to being in.
    /// </summary>
    private string SinceStart()
        => $"(login took {(Environment.TickCount64 - _startedTicks) / 1000.0:0.0}s)";

    private int Vary(int ms) => _settings.Vary(ms, _rng);

    /// <summary>Find the GAME window first, else the LAUNCHER — both browser-excluded so a Chrome
    /// tab titled "EverQuest …" can never be mistaken for the game. Zero handle => neither is open.</summary>
    private static (IntPtr handle, bool isGame) FindLaunchTarget()
    {
        WindowInfo? game = WindowFinder.GuessEverQuest();
        if (game != null && WindowFinder.IsGameWindow(game.Handle)) return (game.Handle, true);
        WindowInfo? lp = WindowFinder.GuessLauncher();
        if (lp != null) return (lp.Handle, false);
        return (IntPtr.Zero, false);
    }

    // ---- Win32: focus + absolute mouse click ----
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] p, int cb);

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                       MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private static void Click(Point screen)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
        int nx = (int)Math.Round((screen.X - vx) * 65535.0 / vw);
        int ny = (int)Math.Round((screen.Y - vy) * 65535.0 / vh);

        var move = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } };
        var down = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } };
        var up = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } };

        int sz = Marshal.SizeOf<INPUT>();
        SendInput(1, new[] { move }, sz);
        Thread.Sleep(40);
        SendInput(1, new[] { down }, sz);
        Thread.Sleep(40);
        SendInput(1, new[] { up }, sz);
    }
}
