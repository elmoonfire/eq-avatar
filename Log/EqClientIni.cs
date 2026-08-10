using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EQAvatar.Spike.Log;

/// <summary>
/// Reads/writes eqclient.ini to make sure the game writes a log file we can tail.
/// EQ writes logs only when logging is enabled; EQBuddy notes it "forces Log=1 in
/// eqclient.ini" while the game is closed. We do the same, non-destructively, with a backup.
///
/// NOTE: only change eqclient.ini while EverQuest Legends is CLOSED — the client
/// rewrites the file on exit and will clobber changes made while it is running.
/// </summary>
public static class EqClientIni
{
    public sealed record Result(bool Changed, string Message, string? BackupPath);

    private static readonly Regex LogLine =
        new(@"^\s*Log\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Ensure the eqclient.ini "Log" key is set to enabled (1). Creates a .bak the first time.
    /// </summary>
    public static Result EnsureLoggingEnabled(string iniPath)
    {
        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            return new Result(false, $"eqclient.ini not found at: {iniPath}", null);

        string[] lines = File.ReadAllLines(iniPath);
        bool found = false;
        bool alreadyOn = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!LogLine.IsMatch(lines[i])) continue;
            found = true;
            string current = lines[i];
            string value = current.Substring(current.IndexOf('=') + 1).Trim();
            if (value is "1" or "TRUE" or "true" or "True")
            {
                alreadyOn = true;
            }
            else
            {
                lines[i] = "Log=1";
            }
            break;
        }

        if (found && alreadyOn)
            return new Result(false, "Logging is already enabled (Log is on). Nothing to change.", null);

        // Back up before writing.
        string backup = iniPath + ".eqavatar.bak";
        if (!File.Exists(backup))
            File.Copy(iniPath, backup);

        if (!found)
        {
            // Try to slot it under [Defaults]; otherwise append a small section.
            var sb = new StringBuilder();
            bool inserted = false;
            foreach (string line in lines)
            {
                sb.AppendLine(line);
                if (!inserted && line.Trim().Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("Log=1");
                    inserted = true;
                }
            }
            if (!inserted)
            {
                sb.AppendLine();
                sb.AppendLine("[Defaults]");
                sb.AppendLine("Log=1");
            }
            File.WriteAllText(iniPath, sb.ToString());
            return new Result(true, "Added Log=1 to eqclient.ini (backup written).", backup);
        }

        File.WriteAllLines(iniPath, lines);
        return new Result(true, "Set Log=1 in eqclient.ini (backup written).", backup);
    }
}
