using System.Windows;

namespace EQAvatar.Spike;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Show the main window first, then cover it with an identically sized, opaque splash.
        // Because the splash matches the app's rectangle exactly, nothing behind (desktop or
        // the game) ever peeks around the icon during the fade — you only see splash → app.
        var main = new MainWindow();
        main.Show();
        MainWindow = main;

        var splash = new SplashWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = main.Width,
            Height = main.Height,
            Left = main.Left,
            Top = main.Top,
        };
        splash.Show();
        splash.PlayThen(() => main.Activate());
    }
}
