using System.Collections.Generic;

namespace EQAvatar.Spike.Launch;

public enum LaunchRisk { None, Low, High }

/// <summary>Static catalog describing each launch method for the picker.</summary>
public sealed record LaunchMethodInfo(
    string Id,
    string Title,
    string Tagline,
    string Involves,
    string Risk,
    LaunchRisk RiskLevel);

public static class LaunchMethods
{
    public static readonly IReadOnlyList<LaunchMethodInfo> All = new List<LaunchMethodInfo>
    {
        new("Foreground", "Foreground",
            "Simple & safe",
            "EQL runs as your active window; EQ Avatar drives it with normal synthesized input while it is focused. A second monitor holds everything else.",
            "Lowest risk. No injection, no drivers. Only limitation: the game must be the active window while a role runs.",
            LaunchRisk.None),

        new("IsolatedDesktop", "Isolated Desktop",
            "Hidden, no injection",
            "EQL launches on a separate Windows desktop so it has its own focus; your real desktop stays free. Needs a virtual display so the GPU can render there.",
            "No injection. Experimental on this hardware — the game must be able to render on the virtual display; not guaranteed for every setup.",
            LaunchRisk.Low),

        new("Vm", "Virtual Machine",
            "Isolated & reliable",
            "EQL runs inside a GPU-partitioned VM (Hyper-V GPU-PV) where it is always foreground; EQ Avatar controls it there. Your host PC is untouched.",
            "No injection, strong isolation. Heaviest setup (VM + GPU partition) and some performance overhead.",
            LaunchRisk.Low),

        new("Injection", "Injection",
            "Full background",
            "EQL runs normally on your desktop with the full GPU; EQ Avatar injects a helper into the game to feed it input so it responds even when unfocused/behind other windows.",
            "Highest capability, highest risk. Injecting into the client is against EQL's rules and can get the account banned. Chosen with eyes open.",
            LaunchRisk.High),
    };

    public static LaunchMethodInfo? ById(string? id)
    {
        if (id == null) return null;
        foreach (var m in All) if (m.Id == id) return m;
        return null;
    }
}
