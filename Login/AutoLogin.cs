using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    public bool Running => _cts is { IsCancellationRequested: false };

    public AutoLogin(string server, AppSettings settings)
    {
        _server = string.IsNullOrWhiteSpace(server) ? "Rivervale" : server.Trim();
        _settings = settings;
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop() { _cts?.Cancel(); Log?.Invoke("Auto-login stopped."); }

    private async Task Loop(CancellationToken ct)
    {
        Log?.Invoke($"Auto-login started (server: {_server}). Leave the launcher/game in front.");
        DateTime deadline = DateTime.Now.AddMinutes(8);   // generous — the launcher can update for a while
        try
        {
            while (!ct.IsCancellationRequested && DateTime.Now < deadline)
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
                    Log?.Invoke("Waiting for the EverQuest LaunchPad / game window…");
                    await Task.Delay(1500, ct);
                    continue;
                }

                SetForegroundWindow(w);
                await Task.Delay(300, ct);
                var found = await ScreenText.ReadAsync(w);
                LogSeen(found);   // diagnostic: print what OCR reads so a missed click is debuggable

                // 1) Character select — click the green 'Enter World' in the LEFT menu.
                bool charSelect = ScreenText.Find(found, "Enter World", out Point pEnter)
                               || ScreenText.Find(found, "Create Character", out _)
                               || ScreenText.Find(found, "Return Home", out _);
                if (charSelect)
                {
                    if (ScreenText.Find(found, "Enter World", out pEnter))
                    {
                        Click(pEnter);
                        Log?.Invoke("Clicked 'Enter World' (by text) — logging into the game.");
                        Done?.Invoke();
                        return;
                    }
                    // OCR can't read the stylized green text — it's the only green in the left menu column.
                    if (ScreenText.FindGreenButton(w, 0.0, 0.30, 0.20, 0.99, 30) is Point pGreenEW)
                    {
                        Click(pGreenEW);
                        Log?.Invoke($"OCR couldn't read 'Enter World' — clicked the green button in the left menu at {pGreenEW.X:0},{pGreenEW.Y:0}. Logging in.");
                        Done?.Invoke();
                        return;
                    }
                    Log?.Invoke("At character select, but couldn't locate 'Enter World'. Roughly where is it on screen?");
                    await Task.Delay(Vary(1500), ct);
                    continue;
                }

                // 2) Server select — choose the server, then PLAY.
                if (ScreenText.Find(found, "SELECTION", out _) || ScreenText.Find(found, _server, out _))
                {
                    if (ScreenText.Find(found, _server, out Point pServer))
                    {
                        Click(pServer);
                        Log?.Invoke($"Selected server '{_server}'.");
                        await Task.Delay(700, ct);
                    }
                    var f2 = await ScreenText.ReadAsync(w);
                    if (ScreenText.Find(f2, "PLAY", out Point pPlay2))
                    {
                        Click(pPlay2);
                        Log?.Invoke("Clicked PLAY (server select) — loading character screen.");
                        await Task.Delay(3000, ct);
                    }
                    await Task.Delay(Vary(1200), ct);
                    continue;
                }

                // 3) LaunchPad — click PLAY. Try OCR text first, then the green-button colour fallback.
                if (ScreenText.Find(found, "PLAY", out Point pPlay))
                {
                    Click(pPlay);
                    Log?.Invoke("Clicked PLAY (launcher, by text). Waiting for it to load…");
                    await Task.Delay(3500, ct);
                    continue;
                }
                if (!isGame && ScreenText.FindGreenButton(w) is Point pGreen)
                {
                    Click(pGreen);
                    Log?.Invoke($"OCR couldn't read 'PLAY' — clicked the green button at {pGreen.X:0},{pGreen.Y:0}. If that's not PLAY, tell me roughly where PLAY sits.");
                    await Task.Delay(3500, ct);
                    continue;
                }

                Log?.Invoke(isGame
                    ? "Game window focused — waiting for the server or character screen…"
                    : "Launcher focused but no PLAY text or green button found yet — waiting…");
                await Task.Delay(Vary(1500), ct);
            }
            Log?.Invoke(ct.IsCancellationRequested ? "Auto-login cancelled." : "Auto-login timed out (8 min).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("Auto-login error: " + ex.Message); }
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
