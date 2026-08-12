// ============================================================================
//  EQ AVATAR RESCUE — the break-glass updater.
//
//  Sole job: when the main app hits a fatal error and won't launch, double-click
//  this exe in the install folder. It fetches the latest release and replaces
//  the files around it. Nothing else — no scheduler, no UI, no config.
//
//  How it works:
//    1. Copies itself to %TEMP% and relaunches from there, so even
//       EQAvatarRescue.exe in the install folder can be replaced.
//    2. Reads latest.json from the repo (falls back to the GitHub
//       releases/latest asset URL if that fails).
//    3. Downloads the release zip, sanity-checks it, backs up the files it is
//       about to replace into _rescue-backup-<timestamp>\, then extracts over
//       the install folder with per-file retries for anything locked.
//
//  Flags: --target <dir> (install folder; set automatically by the relaunch)
//         --yes          (no prompts — assume yes everywhere)
// ============================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

const string RescueVersion = "1.0";
const string ManifestUrl = "https://raw.githubusercontent.com/elmoonfire/eq-avatar/main/latest.json";
const string FallbackZipUrl = "https://github.com/elmoonfire/eq-avatar/releases/latest/download/EQAvatar.Spike.zip";
const string AppExeName = "EQAvatar.Spike.exe";
const string AppProcess = "EQAvatar.Spike";

bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
bool fromTemp = args.Any(a => a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase));
string? targetArg = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i].Equals("--target", StringComparison.OrdinalIgnoreCase)) targetArg = args[i + 1];

string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
string exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();

// ---- step 0: get out of the install folder so we can replace ourselves too ----
if (!fromTemp && targetArg is null)
{
    try
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"EQAvatarRescue-{Guid.NewGuid():N}.exe");
        File.Copy(exePath, tmp, true);
        Process.Start(new ProcessStartInfo(tmp, $"--from-temp --target \"{exeDir}\"{(yes ? " --yes" : "")}")
        { UseShellExecute = OperatingSystem.IsWindows() });
        return 0;                       // the temp copy takes it from here
    }
    catch
    {
        // couldn't relaunch from temp — carry on in place (our own exe just won't be replaceable)
    }
}

string target = Path.GetFullPath(targetArg ?? exeDir);

// ---- logging: console + %AppData%\EQAvatar\logs\rescue-*.log ----
StreamWriter? logFile = null;
try
{
    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "logs");
    Directory.CreateDirectory(logDir);
    logFile = new StreamWriter(Path.Combine(logDir, $"rescue-{DateTime.Now:yyyyMMdd-HHmmss}.log"), append: false) { AutoFlush = true };
}
catch { }
void Log(string msg)
{
    Console.WriteLine(msg);
    try { logFile?.WriteLine($"{DateTime.Now:HH:mm:ss}  {msg}"); } catch { }
}
bool Ask(string question)          // returns true for yes; --yes answers everything
{
    if (yes) return true;
    Console.Write(question + " [y/N] ");
    string? a = Console.ReadLine();
    return a is not null && a.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
}

int exit;
try
{
    Log("==============================================");
    Log($" EQ AVATAR RESCUE v{RescueVersion} — break-glass updater");
    Log("==============================================");
    Log($" install folder : {target}");
    Log("");

    // ---- sanity: does this look like the EQ Avatar folder? ----
    if (!File.Exists(Path.Combine(target, AppExeName)) && !File.Exists(Path.Combine(target, "EQAvatar.Spike.dll")))
    {
        Log($" WARNING: {AppExeName} not found here — this may not be the EQ Avatar folder.");
        if (!Ask(" Continue anyway and extract the release into this folder?"))
        { Log(" Aborted — run this from the EQ Avatar install folder."); return Done(1); }
    }

    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
    http.Timeout = TimeSpan.FromMinutes(5);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("EQAvatarRescue/" + RescueVersion);

    // ---- find the latest release ----
    string zipUrl = FallbackZipUrl, version = "latest";
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(ManifestUrl, cts.Token));
        version = doc.RootElement.GetProperty("version").GetString() ?? "latest";
        zipUrl = doc.RootElement.GetProperty("url").GetString() ?? FallbackZipUrl;
        Log($" latest release : {version}");
    }
    catch (Exception ex)
    {
        Log($" manifest unreachable ({ex.GetType().Name}) — using the GitHub latest-release URL directly.");
    }

    // ---- if the app is somehow running, offer to close it ----
    try
    {
        var running = Process.GetProcessesByName(AppProcess);
        if (running.Length > 0)
        {
            Log($" {AppProcess} is running ({running.Length} instance(s)) — its files will be locked.");
            if (Ask(" Close it now?"))
                foreach (var p in running)
                {
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
                }
        }
    }
    catch { }

    // ---- download ----
    Log("");
    Log($" downloading  {zipUrl}");
    string zipPath = Path.Combine(Path.GetTempPath(), $"eqavatar-rescue-{Guid.NewGuid():N}.zip");
    using (var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
    {
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(zipPath);
        var buf = new byte[81920];
        long got = 0; int read, lastPct = -1;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            got += read;
            if (total is > 0)
            {
                int pct = (int)(got * 100 / total.Value);
                if (pct != lastPct) { Console.Write($"\r   {pct}%  of {total.Value / 1048576.0:0.0} MB   "); lastPct = pct; }
            }
        }
        Console.WriteLine();
        Log($" downloaded {got / 1048576.0:0.0} MB");
    }

    // ---- verify + plan ----
    using var zip = ZipFile.OpenRead(zipPath);
    var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
    if (entries.Count == 0 || !entries.Any(e => e.Name.Equals(AppExeName, StringComparison.OrdinalIgnoreCase)
                                             || e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
    { Log(" ERROR: the downloaded zip doesn't look like an EQ Avatar release — aborting, nothing touched."); return Done(1); }
    Log($" release contains {entries.Count} file(s)");

    // ---- backup whatever we're about to replace ----
    string backupDir = Path.Combine(target, $"_rescue-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
    int backedUp = 0;
    foreach (var e in entries)
    {
        string dest = SafePath(target, e.FullName);
        if (File.Exists(dest))
        {
            string bak = SafePath(backupDir, e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
            File.Copy(dest, bak, true);
            backedUp++;
        }
    }
    if (backedUp > 0) Log($" backed up {backedUp} current file(s) → {Path.GetFileName(backupDir)}\\");

    // ---- extract with per-file retries (locked files get 3 chances) ----
    Log("");
    int ok = 0;
    var failed = new List<string>();
    foreach (var e in entries)
    {
        string dest = SafePath(target, e.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        bool done = false;
        for (int attempt = 1; attempt <= 3 && !done; attempt++)
        {
            try { e.ExtractToFile(dest, overwrite: true); done = true; }
            catch (IOException) when (attempt < 3) { await Task.Delay(800); }
            catch (UnauthorizedAccessException) when (attempt < 3) { await Task.Delay(800); }
        }
        if (done) { ok++; Log($"   ✓ {e.FullName}"); }
        else { failed.Add(e.FullName); Log($"   ✗ {e.FullName}   (locked or access denied)"); }
    }
    try { File.Delete(zipPath); } catch { }

    // ---- report ----
    Log("");
    if (failed.Count == 0)
    {
        Log($" DONE — {ok} file(s) replaced. EQ Avatar is now {version}.");
        Log($" Start {AppExeName} normally. When you're happy, delete {Path.GetFileName(backupDir)}\\.");
        exit = 0;
    }
    else
    {
        Log($" PARTIAL — {ok} replaced, {failed.Count} FAILED (something is holding them open):");
        foreach (string f in failed) Log($"    · {f}");
        Log(" Close every EQ Avatar window (check Task Manager), then run this again.");
        exit = 2;
    }
}
catch (Exception ex)
{
    Log("");
    Log(" FATAL: " + ex.Message);
    Log(" Nothing may have been changed. Full details in the rescue log.");
    try { logFile?.WriteLine(ex.ToString()); } catch { }
    exit = 1;
}
return Done(exit);

int Done(int code)
{
    if (!yes)
    {
        Console.WriteLine();
        Console.Write(" Press Enter to close…");
        try { Console.ReadLine(); } catch { }
    }
    logFile?.Dispose();
    return code;
}

// Zip-Slip guard: entry paths must stay inside the destination root.
static string SafePath(string root, string entryPath)
{
    string full = Path.GetFullPath(Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        throw new IOException("zip entry escapes the target folder: " + entryPath);
    return full;
}
