using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EQAvatar.Spike.Input;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId)
{
    public override string ToString() => $"{ProcessName}  —  \"{Title}\"  (hwnd 0x{Handle.ToInt64():X})";
}

/// <summary>
/// Enumerates visible top-level windows so the probe can target the EverQuest window
/// without us hard-coding a title. Also exposes a best-effort "find EQ" helper.
/// </summary>
public static class WindowFinder
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static List<WindowInfo> ListWindows()
    {
        var results = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = "?";
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { /* exited */ }

            results.Add(new WindowInfo(hWnd, title, proc, (int)pid));
            return true;
        }, IntPtr.Zero);

        results.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    // Browsers, chat apps, editors: never the game even if their TITLE says "EverQuest"
    // (a Chrome tab about EQ was matching before). Match the real game PROCESS first.
    private static readonly string[] NotGame =
    {
        "chrome", "msedge", "firefox", "opera", "brave", "iexplore", "vivaldi", "chromium",
        "arc", "opera_gx", "edge", "discord", "slack", "notepad", "code", "explorer",
        "devenv", "obs64", "obs", "steam", "steamwebhelper"
    };

    private static bool IsBrowserish(string proc)
    {
        proc = proc.ToLowerInvariant();
        foreach (string b in NotGame) if (proc == b) return true;
        return false;
    }

    /// <summary>
    /// Best-effort guess at the EverQuest window. Priority: the real game process
    /// (eqgame / eqgame64 / eqclient / eqlegends), then a game-ish process, then a title
    /// that mentions EverQuest — but a browser/chat/editor is NEVER the game, so tabs about
    /// EQ topics no longer hijack the target.
    /// </summary>
    public static WindowInfo? GuessEverQuest()
    {
        List<WindowInfo> wins = ListWindows();

        // 1) exact/near game executable name
        foreach (WindowInfo w in wins)
        {
            string pn = w.ProcessName.ToLowerInvariant();
            if (pn is "eqgame" or "eqgame64" or "eqclient" or "eqlegends" or "everquest") return w;
        }
        // 2) game-ish process name (still excluding browsers etc.)
        foreach (WindowInfo w in wins)
        {
            if (IsBrowserish(w.ProcessName)) continue;
            string pn = w.ProcessName.ToLowerInvariant();
            if (pn.Contains("eqgame") || pn.Contains("eqclient") || pn.Contains("everquest") || pn.Contains("eqlegend"))
                return w;
        }
        // 3) title says EverQuest, but only if the owning process is not a browser/chat/editor
        foreach (WindowInfo w in wins)
        {
            if (IsBrowserish(w.ProcessName)) continue;
            if (w.Title.ToLowerInvariant().Contains("everquest")) return w;
        }
        return null;
    }

    /// <summary>The EQL LaunchPad / patcher window, browser-excluded. Matched by process name
    /// (launchpad / patcher) or a title that clearly names the launcher — never a browser tab.</summary>
    public static WindowInfo? GuessLauncher()
    {
        foreach (WindowInfo w in ListWindows())
        {
            if (IsBrowserish(w.ProcessName)) continue;
            string pn = w.ProcessName.ToLowerInvariant();
            string tt = w.Title.ToLowerInvariant();
            if (pn.Contains("launchpad") || pn.Contains("patcher") || pn.Contains("daybreak")
                || tt.Contains("launchpad") || tt.Contains("patcher"))
                return w;
        }
        return null;
    }

    /// <summary>True if this window belongs to the actual game client process (not a browser/launcher).</summary>
    public static bool IsGameWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hWnd, out uint pid);
        try
        {
            string pn = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            return pn is "eqgame" or "eqgame64" or "eqclient" or "eqlegends" or "everquest"
                   || pn.Contains("eqgame") || pn.Contains("eqclient") || pn.Contains("eqlegend");
        }
        catch { return false; }
    }

    /// <summary>The process id that owns a window, or 0. Kept so a DEAD window can still be asked
    /// about: once the handle is gone there is no way back to the process, and "did the game crash
    /// or did it just rebuild its window?" is unanswerable after the fact.</summary>
    public static int OwnerPid(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Is that process still running? The two answers mean completely different things
    /// when a game window vanishes — the client crashed, or the client is fine and replaced its
    /// window (a resolution change, a full-screen toggle, the loading screen after a death) — and
    /// they have completely different fixes.</summary>
    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>List child windows (controls) of a parent, with class + any text.</summary>
    public static List<WindowInfo> ListChildren(IntPtr parent)
    {
        var results = new List<WindowInfo>();
        EnumChildWindows(parent, (hWnd, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            var txt = new StringBuilder(256);
            GetWindowText(hWnd, txt, txt.Capacity);
            GetWindowThreadProcessId(hWnd, out uint pid);
            string title = txt.Length > 0 ? txt.ToString() : "(no text)";
            // Reuse WindowInfo, stashing the class name in the ProcessName slot for display.
            results.Add(new WindowInfo(hWnd, title, "class=" + cls, (int)pid));
            return true;
        }, IntPtr.Zero);
        return results;
    }
}
