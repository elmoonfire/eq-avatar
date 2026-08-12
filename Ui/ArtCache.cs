using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace EQAvatar.Spike.Ui;

/// <summary>
/// Mascot scene art for the in-app UI. The images live on eqavatar.ldtlan.com (so art can be
/// refreshed without an app release) and are cached forever in %AppData%\EQAvatar\art after the
/// first download. Binding is fire-and-forget: cached art appears instantly, fresh art pops in
/// when its download lands, and with no network the UI simply shows its text captions.
/// </summary>
public static class ArtCache
{
    private const string BaseUrl = "https://eqavatar.ldtlan.com/avatar/img/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly HashSet<string> InFlight = new();
    private static readonly Dictionary<string, BitmapImage> Loaded = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "art");

    /// <summary>Set the image now if cached, otherwise download then set. Never throws.</summary>
    public static void Bind(Image target, string name)
    {
        try
        {
            if (Loaded.TryGetValue(name, out BitmapImage? ready)) { target.Source = ready; return; }
            string path = Path.Combine(Dir, name);
            if (File.Exists(path)) { target.Source = FromFile(name, path); return; }
            lock (InFlight) { if (!InFlight.Add(name + "|" + target.GetHashCode())) return; }
            _ = FetchThenSet(target, name, path);
        }
        catch { /* art must never break the UI */ }
    }

    private static async Task FetchThenSet(Image target, string name, string path)
    {
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(BaseUrl + name);
            if (bytes.Length < 200) return;
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(path, bytes);
            target.Dispatcher.Invoke(() => { try { target.Source = FromFile(name, path); } catch { } });
        }
        catch { /* offline / blocked — captions carry the UI */ }
    }

    private static BitmapImage FromFile(string name, string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        Loaded[name] = bi;
        return bi;
    }

    /// <summary>Cached bitmap or null (kicks a background download for next time).</summary>
    public static BitmapImage? Get(string name)
    {
        try
        {
            if (Loaded.TryGetValue(name, out BitmapImage? ready)) return ready;
            string path = Path.Combine(Dir, name);
            if (File.Exists(path)) return FromFile(name, path);
            lock (InFlight) { if (!InFlight.Add(name)) return null; }
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = await Http.GetByteArrayAsync(BaseUrl + name);
                    if (bytes.Length < 200) return;
                    Directory.CreateDirectory(Dir);
                    await File.WriteAllBytesAsync(path, bytes);
                }
                catch { }
            });
            return null;
        }
        catch { return null; }
    }
}
