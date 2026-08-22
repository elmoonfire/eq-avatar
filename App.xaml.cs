using System;
using System.IO;
using System.Windows;
using EQAvatar.Spike.Config;

namespace EQAvatar.Spike;

public partial class App : Application
{
    public App()
    {
        // Crash visibility (0.9.22): if anything fatal happens — even during startup, before a
        // single window exists — write the full exception to %AppData%\EQAvatar\logs\crash-*.txt
        // and show a message box. The app must never again die silently like v0.9.21 did.
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrash(a.ExceptionObject as Exception, "fatal");
        DispatcherUnhandledException += (_, a) =>
        {
            WriteCrash(a.Exception, "ui");
            // Once the app is actually up, one bad click shouldn't take down a grinding session.
            if (MainWindow is { IsLoaded: true }) a.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Show the main window first, then cover it with an identically sized, opaque splash.
        // Because the splash matches the app's rectangle exactly, nothing behind (desktop or
        // the game) ever peeks around the icon during the fade — you only see splash → app.
        var main = new MainWindow();
        main.SplashUp = true;          // hold off the always-on-top raise until the splash is done
        main.Show();
        MainWindow = main;

        // WRAPPED, because the flag it sets is a promise to turn something back on. Anything that
        // throws in here — a missing splash resource, a shutdown race on Show — would otherwise
        // leave SplashUp true for the rest of the session, silently disabling always-on-top. And
        // the app would keep running: the unhandled-exception handler swallows anything raised once
        // the main window is loaded, which main.Show() above has already guaranteed. No splash, no
        // crash, no message — which is precisely the report this whole change set started from.
        try
        {
            var splash = new SplashWindow { WindowStartupLocation = WindowStartupLocation.Manual };
            if (main.WindowState == WindowState.Maximized)
            {
                // Restored-as-maximized (0.9.21 window memory): Left/Width describe the restore
                // bounds, not the maximized rectangle — cover the work area instead.
                var wa = SystemParameters.WorkArea;
                splash.Left = wa.Left; splash.Top = wa.Top;
                splash.Width = wa.Width; splash.Height = wa.Height;
            }
            else
            {
                splash.Left = main.Left; splash.Top = main.Top;
                splash.Width = main.ActualWidth > 0 ? main.ActualWidth : main.Width;
                splash.Height = main.ActualHeight > 0 ? main.ActualHeight : main.Height;
            }
            splash.Show();
            splash.Activate();             // front of the topmost band, ahead of the window it covers
            splash.PlayThen(() =>
            {
                main.SplashUp = false;
                main.Activate();
            });
        }
        catch (Exception ex)
        {
            main.SplashUp = false;
            // NOT WriteCrash. That one shows a modal "fatal error" box — untrue here, since the app
            // is entirely usable without its splash — and worse, it fires only for the FIRST crash
            // of a session, so spending it on a missing decoration would leave a genuinely fatal
            // error later writing a file and saying nothing on screen. That silent death is the one
            // thing this class was written to prevent.
            try { Diag.BotLog.Log("app", $"the launch splash failed, carrying on without it: {ex}"); }
            catch { /* logging a logging failure helps nobody */ }
        }
    }

    private static int _crashes;

    internal static void WriteCrash(Exception? ex, string where)
    {
        if (ex is null || _crashes >= 5) return;   // cap runaway repeats
        _crashes++;
        string path = "";
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQAvatar", "logs");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{_crashes}.txt");
            File.WriteAllText(path, $"EQ Avatar {AppSettings.AppVersion} crash ({where}) at {DateTime.Now:u}\r\n\r\n{ex}");
        }
        catch { /* crash logging must never crash */ }
        if (_crashes == 1)
        {
            try
            {
                MessageBox.Show(
                    "EQ Avatar hit a fatal error." +
                    (path.Length > 0 ? $"\n\nDetails were saved to:\n{path}" : "") +
                    $"\n\n{ex.GetType().Name}: {ex.Message}",
                    "EQ Avatar — crash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }
}
