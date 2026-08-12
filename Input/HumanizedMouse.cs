using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Input;

/// <summary>
/// Moves the real system cursor along a smooth, varied path instead of teleporting it:
/// cosine ease-in-out for acceleration/deceleration, a sine lateral arc so it curves rather
/// than tracking a ruler-straight line, and run-to-run randomness in speed, curve amount, and
/// launch angle. It always ends exactly on the target (the arc returns to zero at t=1), so
/// clicks still land. Purely about how the motion looks; it drives the foreground automation.
/// </summary>
public static class HumanizedMouse
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] p, int cb);

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                       MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private static void MoveAbs(double x, double y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
        int nx = (int)Math.Round((x - vx) * 65535.0 / vw);
        int ny = (int)Math.Round((y - vy) * 65535.0 / vh);
        var mv = new INPUT { mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } };
        SendInput(1, new[] { mv }, Marshal.SizeOf<INPUT>());
    }

    public static (int X, int Y) CursorPos() { GetCursorPos(out POINT p); return (p.X, p.Y); }

    /// <summary>Virtual-screen bounds in PHYSICAL pixels (matches the coordinate space MoveTo uses).</summary>
    public static (int X, int Y, int W, int H) VirtualScreen() =>
        (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
         GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Glide the cursor to (tx,ty) with humanized easing, arc, and jitter.</summary>
    public static async Task MoveTo(double tx, double ty, AppSettings s, Random rng, CancellationToken ct)
    {
        GetCursorPos(out POINT p);
        double sx = p.X, sy = p.Y, dx = tx - sx, dy = ty - sy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 2) { MoveAbs(tx, ty); return; }

        // Speed → duration, varied by the global variance %.
        double v = s.RandomVariancePercent / 100.0;
        double speed = Math.Max(120, s.MouseSpeedPxPerSec) * (1 + (rng.NextDouble() * 2 - 1) * v);
        int durMs = (int)Math.Clamp(dist / speed * 1000.0, 90, 1600);

        // Perpendicular direction, rotated by a random launch-angle jitter.
        double perpX = -dy / dist, perpY = dx / dist;
        double jitter = s.MouseAngleJitterDegrees * Math.PI / 180.0 * (rng.NextDouble() * 2 - 1);
        double cj = Math.Cos(jitter), sj = Math.Sin(jitter);
        (perpX, perpY) = (perpX * cj - perpY * sj, perpX * sj + perpY * cj);

        // Random arc amount + side.
        double amp = dist * s.MouseArc * (0.6 + rng.NextDouble() * 0.8) * (rng.NextDouble() < 0.5 ? -1 : 1);

        int steps = Math.Max(12, durMs / 12);
        for (int i = 1; i <= steps && !ct.IsCancellationRequested; i++)
        {
            double t = (double)i / steps;
            double ease = 0.5 - 0.5 * Math.Cos(Math.PI * t);   // cosine accel → decel
            double lat = amp * Math.Sin(Math.PI * t);          // sine arc, zero at both ends
            MoveAbs(sx + dx * ease + perpX * lat, sy + dy * ease + perpY * lat);
            await Task.Delay(Math.Max(4, durMs / steps), ct);
        }
        MoveAbs(tx, ty);
    }

    /// <summary>Jump the cursor straight to a screen point (no easing) — used by the keybind
    /// auto-capture, which just needs the pointer parked over the list before it scrolls.</summary>
    public static void MoveInstant(double x, double y) => MoveAbs(x, y);

    /// <summary>Turn the wheel: negative = scroll down (away from you), positive = up.
    /// One "click" is WHEEL_DELTA, exactly what a physical notch sends.</summary>
    public static void Scroll(int clicks)
    {
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        int sz = Marshal.SizeOf<INPUT>();
        int step = clicks < 0 ? -120 : 120;
        for (int i = 0; i < Math.Abs(clicks); i++)
        {
            var wheel = new INPUT { mi = new MOUSEINPUT { mouseData = unchecked((uint)step), dwFlags = MOUSEEVENTF_WHEEL } };
            SendInput(1, new[] { wheel }, sz);
            Thread.Sleep(28);
        }
    }

    public static void Click(Random rng)
    {
        var down = new INPUT { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up = new INPUT { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        int sz = Marshal.SizeOf<INPUT>();
        SendInput(1, new[] { down }, sz);
        Thread.Sleep(35 + rng.Next(45));   // varied press dwell
        SendInput(1, new[] { up }, sz);
    }
}
