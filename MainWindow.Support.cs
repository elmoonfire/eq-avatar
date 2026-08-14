using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQAvatar.Spike.Diag;
using EQAvatar.Spike.Ui;

namespace EQAvatar.Spike;

/// <summary>
/// The support button, and the crash reporter that goes with it (partial class).
///
/// SELF-CONTAINED, like <c>MainWindow.Sections.cs</c> and for the same reasons. It edits neither
/// <c>MainWindow.xaml</c> nor <c>MainWindow.xaml.cs</c> nor <c>App.xaml.cs</c>: it hooks itself
/// up from a module initializer, adds its chip to the rail footer the XAML already built, and
/// subscribes to the exception events as an ADDITIONAL handler alongside the crash logger that
/// has been there since 0.9.22. Delete this file and the app is exactly what it was.
///
/// WHY THE RAIL FOOTER RATHER THAN A BUTTON ON EACH PAGE. The brief was "a support icon on most
/// pages". The footer strip — Launch Method, the updater chip, the version — is outside the
/// section list that <c>MainWindow.Sections.cs</c> collapses, so one chip there is present on
/// every page, always, and cannot be hidden by collapsing a section. Twenty-odd copies of the
/// same button, one per panel, would be the same feature with twenty places to forget.
///
/// WHY A MODULE INITIALIZER AND NOT A STATIC CONSTRUCTOR. A class gets exactly one static
/// constructor and <c>MainWindow.Sections.cs</c> already has it. A module initializer runs once
/// when the assembly loads, before any window exists, and registering a class handler there is
/// equivalent — without two files fighting over the same member.
/// </summary>
public partial class MainWindow
{
    private Border? _supportChip;
    private SupportWindow? _supportWindow;

    /// <summary>
    /// Wire the chip to every MainWindow that loads. The dispatcher hop matches the rail build:
    /// it puts this after every instance Loaded handler, so the footer is fully realised.
    /// </summary>
    internal static void HookSupport()
    {
        EventManager.RegisterClassHandler(typeof(MainWindow), LoadedEvent, new RoutedEventHandler(
            (s, _) =>
            {
                if (s is not MainWindow w) return;
                w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(w.InstallSupport));
            }));
    }

    /// <summary>
    /// Put the chip in the rail footer and start the crash reporter.
    ///
    /// Everything is inside a try/catch that logs and gives up: a support button that fails to
    /// appear is a nuisance, and an app that will not open because its support button threw is a
    /// far worse bug than whatever anybody wanted to report.
    /// </summary>
    private void InstallSupport()
    {
        try
        {
            CrashReporter.Install(_settings);

            if (_supportChip is not null) return;
            if (UpdaterChip.Parent is not StackPanel footer) return;

            _supportChip = BuildSupportChip();

            // Above the version line, below the updater chip: the two ambient things you glance
            // at when something is wrong, in the order you need them — "am I current?" first,
            // "tell someone" second.
            int at = footer.Children.IndexOf(UpdaterChip);
            if (at < 0) footer.Children.Add(_supportChip);
            else footer.Children.Insert(at + 1, _supportChip);
        }
        catch (Exception ex)
        {
            BotLog.Log("support", "button not installed: " + ex);
        }
    }

    private Border BuildSupportChip()
    {
        var icon = new TextBlock
        {
            Text = "\U0001F41E",
            FontSize = 12,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Report a problem",
            FontSize = 11,
            Foreground = Frozen("#9AA7B4"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);

        var chip = new Border
        {
            Margin = new Thickness(0, 6, 0, 2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Tell the officers something is wrong — your version, OS and screen go with it",
            Child = row,
        };

        // Rests dark and quiet like the updater chip beside it. Support should be findable, not
        // insistent: a button that shouts is a button people click by accident.
        chip.MouseEnter += (_, _) =>
        {
            chip.Background = Frozen("#12FFFFFF");
            chip.BorderBrush = Frozen("#2A4A57");
            label.Foreground = Frozen("#E6EDF3");
        };
        chip.MouseLeave += (_, _) =>
        {
            chip.Background = Brushes.Transparent;
            chip.BorderBrush = Brushes.Transparent;
            label.Foreground = Frozen("#9AA7B4");
        };
        chip.MouseLeftButtonUp += (_, _) => OpenSupport();

        return chip;
    }

    /// <summary>
    /// Show the support window, or bring the open one forward.
    ///
    /// One window, not one per click: someone who cannot find the window they already opened
    /// clicks the button again, and a second copy of a half-typed bug report helps nobody.
    /// </summary>
    internal void OpenSupport()
    {
        try
        {
            if (_supportWindow is { IsLoaded: true })
            {
                if (_supportWindow.WindowState == WindowState.Minimized)
                    _supportWindow.WindowState = WindowState.Normal;
                _supportWindow.Activate();
                return;
            }

            _supportWindow = new SupportWindow(_settings, this);
            _supportWindow.Closed += (_, _) => _supportWindow = null;
            _supportWindow.Show();
        }
        catch (Exception ex)
        {
            BotLog.Log("support", "window failed to open: " + ex);
            MessageBox.Show("The support window could not open.\n\n" + ex.Message,
                            "EQ Avatar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Runs once as the assembly loads. Its only job is to let <see cref="MainWindow"/> register its
/// class handler without owning the static constructor, which another partial already holds.
/// </summary>
internal static class SupportBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Order matters: the crash hook goes on FIRST, so a fault thrown while the rail is
        // still being built is already being recorded by the time anything else runs.
        try { CrashReporter.InstallEarly(); } catch { }
        try { MainWindow.HookSupport(); }
        catch { /* never stop the app from starting over a support button */ }
    }
}
