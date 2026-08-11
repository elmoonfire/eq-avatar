using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike.Update;

/// <summary>Result of an update check.</summary>
public sealed record UpdateInfo(bool Available, string CurrentVersion, string LatestVersion,
                                string? DownloadUrl, string? Notes, string? Error);

/// <summary>
/// One-click updater fed from GitHub Releases (same model as EQ Legends Companion). It compares the
/// app version to the newest release tag, downloads the release's zip asset, extracts it, and hands
/// off to a tiny .cmd that waits for the app to exit, copies the new files in place, and relaunches.
/// No third-party packages — just HttpClient + the built-in zip support.
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static Updater()
    {
        // GitHub requires a User-Agent; the API accept header keeps the shape stable.
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EQAvatar-Updater");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        string cur = AppSettings.AppVersion;
        try
        {
            // cache-bust so we always see the newest manifest, not a stale CDN copy
            string manifestUrl = AppSettings.UpdateManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string body = await Http.GetStringAsync(manifestUrl);
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            string ver = root.TryGetProperty("version", out JsonElement v) ? (v.GetString() ?? "") : "";
            string notes = root.TryGetProperty("notes", out JsonElement nt) ? (nt.GetString() ?? "") : "";
            string? dl = root.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;

            bool newer = IsNewer(ver, cur);
            return new UpdateInfo(newer && dl != null, cur, Clean(ver), dl, notes, null);
        }
        catch (Exception ex)
        {
            return new UpdateInfo(false, cur, cur, null, null, ex.Message);
        }
    }

    /// <summary>Download the release zip and extract it to a temp staging folder; returns that folder.
    /// Reports 0–100 download percent via <paramref name="progress"/> when the server sends a length.</summary>
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (info.DownloadUrl is null) throw new InvalidOperationException("No download URL for this release.");
        string root = Path.Combine(Path.GetTempPath(), "EQAvatarUpdate");
        string zip = Path.Combine(Path.GetTempPath(), "EQAvatarUpdate.zip");
        string extract = Path.Combine(root, "new");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(extract);

        using (HttpResponseMessage resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using Stream src = await resp.Content.ReadAsStreamAsync();
            using var dst = File.Create(zip);
            byte[] buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total is long tt && tt > 0) progress?.Report(Math.Min(100.0, read * 100.0 / tt));
            }
        }
        progress?.Report(100);
        ZipFile.ExtractToDirectory(zip, extract, overwriteFiles: true);
        try { File.Delete(zip); } catch { /* non-fatal */ }
        return extract;
    }

    /// <summary>Launch the swap-and-restart script, then the caller should shut the app down.</summary>
    public static void ApplyAndRestart(string extractDir)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string exe = Path.Combine(appDir, "EQAvatar.Spike.exe");
        string cmd = Path.Combine(Path.GetTempPath(), "eqavatar_update.cmd");
        string staging = Path.GetDirectoryName(extractDir) ?? extractDir;

        string script =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"IMAGENAME eq EQAvatar.Spike.exe\" | find /I \"EQAvatar.Spike.exe\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"xcopy /E /Y /I \"{extractDir}\\*\" \"{appDir}\\\" >nul\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            $"rmdir /S /Q \"{staging}\" >nul 2>&1\r\n" +
            "del \"%~f0\" >nul 2>&1\r\n";
        File.WriteAllText(cmd, script);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    private static string Clean(string? tag) => (tag ?? "").TrimStart('v', 'V').Trim();

    private static bool IsNewer(string tag, string current)
    {
        if (Version.TryParse(Clean(tag).Split('-')[0], out Version? a) &&
            Version.TryParse(Clean(current).Split('-')[0], out Version? b))
            return a > b;
        return false;
    }
}
