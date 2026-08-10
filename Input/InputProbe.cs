using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EQAvatar.Spike.Input;

/// <summary>
/// The single most important experiment in this spike: can EQ Avatar drive EQL while the
/// game window is NOT focused?
///
/// v1 tried plain SendInput + a single PostMessage to the top-level window, and both did
/// nothing. Hayden can drive EQL from the background with AutoHotkey, so it IS possible —
/// which points at one of three things this version tests directly:
///
///   1) ELEVATION / UIPI. If EQL runs as Administrator and this app doesn't, Windows
///      silently drops BOTH our SendInput (to the focused game) and our PostMessage.
///      This is the #1 suspect and why there's a "Relaunch as admin" button.
///   2) WRONG TARGET WINDOW. Keyboard focus often lives on a child render control, not the
///      top-level frame. So we enumerate child windows and can post to a specific one.
///   3) INPUT PATH. DirectInput/raw-input games ignore posted messages; AttachThreadInput +
///      SendInput can deliver where PostMessage can't. We offer that as its own method.
///
/// Whichever method makes the character react is the one the real input module will use.
/// </summary>
public static class InputProbe
{
    // ---- SendInput ----------------------------------------------------------
    // CANONICAL layout: the union MUST be sized to the largest member (MOUSEINPUT) so that
    // Marshal.SizeOf<INPUT>() equals the real Windows INPUT size (40 bytes on x64). A keyboard-
    // only union is smaller, so SendInput's cbSize check fails and NOTHING is sent — that was a
    // real bug behind "SendInput did nothing" on 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(ushort uCode, uint uMapType);

    // ---- mouse buttons + relative move (for con=mouse5, right-mouse look, etc.) ----
    /// <summary>Press or release a mouse button as real hardware-like input to the focused window.</summary>
    public static void MouseButtonEvent(MouseBtn b, bool down)
    {
        uint flags = 0, data = 0;
        switch (b)
        {
            case MouseBtn.Left:   flags = down ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP;   break;
            case MouseBtn.Right:  flags = down ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP;  break;
            case MouseBtn.Middle: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case MouseBtn.X1:     flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON1; break;
            case MouseBtn.X2:     flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON2; break;
        }
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { mouseData = data, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Tap a mouse button (down, hold, up) to the focused window.</summary>
    public static void MouseTap(MouseBtn b, int holdMs = 40)
    {
        MouseButtonEvent(b, true);
        System.Threading.Thread.Sleep(holdMs);
        MouseButtonEvent(b, false);
    }

    /// <summary>Relative mouse move — used to pan the camera while right-mouse-look is held.</summary>
    public static void MouseMoveRelative(int dx, int dy)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Keys that require the extended-key flag so games read the right scancode
    // (navigation cluster + arrows + right-ctrl/alt + numpad divide).
    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or   // PgUp/PgDn/End/Home/arrows
        0x2D or 0x2E or 0x90 or 0xA3 or 0xA5 or 0x6F;                      // Ins/Del/NumLock/RCtrl/RAlt/NumpadDiv

    private static void SendScan(ushort vk, bool keyUp)
    {
        ushort scan = MapVirtualKey(vk, 0);            // MAPVK_VK_TO_VSC
        uint flags = KEYEVENTF_SCANCODE | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        if (keyUp) flags |= KEYEVENTF_KEYUP;
        // wVk MUST be 0 when KEYEVENTF_SCANCODE is set — this is what makes it read as real
        // hardware input the way AutoHotkey's Send does, which DirectInput games honour.
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Hold a key down (for movement — e.g. run forward). Pair with KeyUp.</summary>
    public static void KeyDown(ushort vk) => SendScan(vk, keyUp: false);
    /// <summary>Release a held key.</summary>
    public static void KeyUp(ushort vk) => SendScan(vk, keyUp: true);

    /// <summary>Hardware-like scan-code tap to whatever window is focused RIGHT NOW.</summary>
    public static void SendInputKey(ushort vk, int holdMs = 40)
    {
        SendScan(vk, keyUp: false);
        System.Threading.Thread.Sleep(holdMs);
        SendScan(vk, keyUp: true);
    }

    // ---- PostMessage / SendMessage -----------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;

    private static (IntPtr down, IntPtr up) BuildLParams(ushort vk, bool extended)
    {
        uint scan = MapVirtualKey(vk, 0);
        long ext = extended ? (1L << 24) : 0;
        long down = 0x00000001L | ((long)scan << 16) | ext;
        long up = down | (1L << 30) | (1L << 31);
        return ((IntPtr)down, (IntPtr)up);
    }

    /// <summary>Post WM_KEYDOWN/WM_KEYUP to a specific window without focusing it.</summary>
    public static void PostKey(IntPtr hWnd, ushort vk, int holdMs = 40, bool extended = false)
    {
        var (d, u) = BuildLParams(vk, extended);
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, d);
        System.Threading.Thread.Sleep(holdMs);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)vk, u);
    }

    /// <summary>Synchronous SendMessage variant (some windows honor this when Post is ignored).</summary>
    public static void SendKey(IntPtr hWnd, ushort vk, int holdMs = 40, bool extended = false)
    {
        var (d, u) = BuildLParams(vk, extended);
        SendMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, d);
        System.Threading.Thread.Sleep(holdMs);
        SendMessage(hWnd, WM_KEYUP, (IntPtr)vk, u);
    }

    /// <summary>Post a WM_CHAR (for typing into a chat/edit field of the target).</summary>
    public static void PostChar(IntPtr hWnd, char c) => PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);

    // ---- AttachThreadInput + SendInput -------------------------------------
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Attach our input queue to the game's thread, SetFocus to it, then SendInput. This can
    /// reach a window that ignores posted messages, without necessarily raising it visually.
    /// </summary>
    public static void AttachedSendInputKey(IntPtr hWnd, ushort vk, int holdMs = 40)
    {
        uint targetTid = GetWindowThreadProcessId(hWnd, out _);
        uint myTid = GetCurrentThreadId();
        bool attached = AttachThreadInput(myTid, targetTid, true);
        try
        {
            SetFocus(hWnd);
            SendInputKey(vk, holdMs);
        }
        finally
        {
            if (attached) AttachThreadInput(myTid, targetTid, false);
        }
    }

    // ---- key helpers --------------------------------------------------------
    public static ushort VkFromChar(char c)
    {
        c = char.ToUpperInvariant(c);
        return c; // '0'..'9' and 'A'..'Z' map directly to their VK codes
    }

    // ---- elevation / integrity ---------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, out uint info, uint len, out uint retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort elevation check of another process. Returns (queryOk, isElevated).
    /// If we can't even open it to ask, that itself hints it's higher-integrity than us.
    /// </summary>
    public static (bool queryOk, bool elevated) GetProcessElevation(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return (false, false);
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out IntPtr token)) return (false, false);
            try
            {
                bool ok = GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _);
                return (ok, ok && elevated != 0);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(hProc); }
    }
}
