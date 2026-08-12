// Retired in 0.9.22.
//
// The launch splash lives at /SplashWindow.xaml(.cs) and is shown exactly once from
// App.OnStartup: an opaque window matching the app's rectangle, the robot fading in and
// then the whole thing fading away to reveal the UI.
//
// From 0.9.18 to 0.9.21 this file held a SECOND splash that MainWindow.Loaded layered on
// top of the first one — two overlapping opaque fades. One splash now; this class is gone.
namespace EQAvatar.Spike.Ui { }
