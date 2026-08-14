using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EQAvatar.Spike.Diag;

/// <summary>
/// The three facts every support report carries, worked out once and never asked of the member.
///
/// WHY THIS IS NOT IN <c>Net/</c>. Screen size is a WPF question and the rest of <c>Net/</c> is
/// deliberately free of it. Putting the capture here keeps <see cref="Net.SupportClient"/> a
/// transport and keeps the "what do we know about this machine" question in one place, next to
/// the crash log that asks the same question.
///
/// SCREEN IS IN REAL PIXELS, not WPF's device-independent ones. A member on a 3840-wide monitor
/// at 150% scaling reports 2560x1440 through <c>SystemParameters</c>, and an officer reading a
/// bug about an overlay landing in the wrong place needs the number the game is actually running
/// at. Win32 is asked directly for that reason, with the WPF value as the fallback if it fails.
/// </summary>
public static class SupportMetrics
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static string? _os;
    private static string? _screen;

    /// <summary>Something like "Microsoft Windows 10.0.26100". Whatever the runtime reports —
    /// invented tidying ("Windows 11") would be a guess, and the build number is the useful part.</summary>
    public static string Os => _os ??= Capture(() => RuntimeInformation.OSDescription.Trim(), "");

    /// <summary>Primary display, in physical pixels: "3440x1440".</summary>
    public static string Screen => _screen ??= Capture(() =>
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0)
        {
            // Falls back to WPF's DIPs. Less accurate under scaling, and still far better than
            // sending nothing at all.
            w = (int)Math.Round(System.Windows.SystemParameters.PrimaryScreenWidth);
            h = (int)Math.Round(System.Windows.SystemParameters.PrimaryScreenHeight);
        }
        return w > 0 && h > 0
            ? w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture)
            : "";
    }, "");

    /// <summary>One line for the "this travels with your report" note in the support window.</summary>
    public static string Summary(string character, string version)
    {
        string who = string.IsNullOrWhiteSpace(character) ? "no character linked" : character;
        return $"{who} · v{version} · {(Os.Length > 0 ? Os : "unknown OS")} · {(Screen.Length > 0 ? Screen : "unknown screen")}";
    }

    /// <summary>Metric capture must never be the thing that breaks a crash report.</summary>
    private static string Capture(Func<string> f, string fallback)
    {
        try { return f() ?? fallback; } catch { return fallback; }
    }
}
