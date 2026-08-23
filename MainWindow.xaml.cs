using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using EQAvatar.Spike.Config;
using EQAvatar.Spike.Data;
using EQAvatar.Spike.Input;
using EQAvatar.Spike.Launch;
using EQAvatar.Spike.Log;
using EQAvatar.Spike.Login;
using EQAvatar.Spike.Map;
using EQAvatar.Spike.Net;
using EQAvatar.Spike.Ocr;
using EQAvatar.Spike.Overlay;
using EQAvatar.Spike.Roles;
using EQAvatar.Spike.Update;
using Path = System.IO.Path;   // disambiguate from System.Windows.Shapes.Path

namespace EQAvatar.Spike;

public partial class MainWindow : Window
{
    private EqLogWatcher? _watcher;
    private MapOverlayWindow? _overlay;
    private string? _currentLog;
    private int _locSeen;
    private readonly DispatcherTimer _fgTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // Grind role state
    private GrindRole? _grind;
    private HuntRole? _hunt;                         // experimental Grind v2 (move + find mobs)
    private IntPtr _grindTarget;
    private readonly DispatcherTimer _grindTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Follower role state (group play: follow + assist a leader)
    private FollowerRole? _follower;
    private readonly DispatcherTimer _followerTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly AppSettings _settings = AppSettings.Load();
    private AutoLogin? _login;
    private CancellationTokenSource? _mouseCts;
    private readonly Random _mouseRng = new();

    // Client Hub (licensing + usage check-in)
    private HubClient _hub = null!;
    private readonly DispatcherTimer _hubTimer = new();

    // Shell (custom chrome + left-nav) state
    private readonly DateTime _sessionStart = DateTime.Now;
    private bool _ready;
    private static readonly string[] Panels =
    {
        "PanelHome", "PanelLog", "PanelInput", "PanelMaps", "PanelData", "PanelSessions", "PanelCombat", "PanelGrind", "PanelFollower",
        "PanelLogin", "PanelMouse", "PanelSequencer", "PanelKeymaps", "PanelQuesting", "PanelAutoMerge", "PanelActivity",
        "PanelProfile", "PanelLicensing", "PanelSettings"
    };
    private static readonly string[] EqClasses =
    {
        "Warrior","Cleric","Paladin","Ranger","Shadow Knight","Druid","Monk","Bard",
        "Rogue","Shaman","Necromancer","Wizard","Magician","Enchanter","Beastlord","Berserker"
    };
    private static readonly string[] EqRaces =
    {
        "Human","Barbarian","Erudite","Wood Elf","High Elf","Dark Elf","Half Elf","Dwarf",
        "Troll","Ogre","Halfling","Gnome","Iksar","Vah Shir","Froglok","Drakkin"
    };

    // Maps (Companion-style zone maps: default + Brewall, heat overlay, live marker)
    private MapLibrary? _mapLib;
    private string? _mapZone;                    // stem currently on screen
    private string? _charZoneStem;               // stem the character is actually in (from the log)
    private readonly List<string> _mapsZoneStems = new();
    private EqLogWatcher? _mapsWatcher;
    private bool _mapsReady;
    private int _mapsHeatTick;

    // Session heat model (fed from the maps log tap; drawn on the Maps page + overlay)
    private readonly HeatmapModel _heat = new();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private const ushort VK_RETURN = 0x0D;
    private const int PANIC_HOTKEY_ID = 0x4551;   // 'EQ'
    private const int PROBE_HOTKEY_ID = 0x4552;   // repeat last Input-probe action while the game is focused
    private const int MERGE_HOTKEY_ID = 0x4553;   // Auto Merge: start/stop the bag sweep without leaving the game
    private const int WM_HOTKEY = 0x0312;
    private const uint VK_F12 = 0x7B;
    private const uint VK_F9 = 0x78;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
    private const uint VK_F7 = 0x76, VK_F8 = 0x77;
    private const uint VK_M = 0x4D;
    private Action? _lastProbe;                   // captured so Ctrl+Alt+F9 can re-fire it in-game
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        Diag.BotLog.Init(AppSettings.AppVersion);
        // Remember the window between runs (0.9.21) — no more resizing every launch.
        if (_settings.WindowWidth >= 600 && _settings.WindowHeight >= 400)
        {
            Width = Math.Min(_settings.WindowWidth, SystemParameters.WorkArea.Width);
            Height = Math.Min(_settings.WindowHeight, SystemParameters.WorkArea.Height);
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        Loaded += (_, _) => OnLoadedInit();
        // (0.9.22) The second splash that used to launch here is gone — App.OnStartup already
        // covers the window with the robot splash; two opaque splashes were fighting each other.
        SourceInitialized += OnSourceInitialized;
        _fgTimer.Tick += (_, _) => TickUi();
        _fgTimer.Start();
        _grindTimer.Tick += (_, _) => UpdateGrindStats();
        _followerTimer.Tick += (_, _) => UpdateFollowerStats();
        _hub = new HubClient(_settings);
        _hubTimer.Tick += (_, _) => { _ = DoCheckIn(false); };
        // The Activity Console follows the shared stream. Subscribed for the window's whole life
        // (not just while its page is open) so it is already complete the first time you open it —
        // the run you want to inspect is always the one that already happened.
        ActivityLog.Added += OnActivityAdded;
        // The detail switch lives on the consoles but is READ from role loops, so the shared flag
        // has to carry the saved answer from the moment the app starts — not from the first time
        // someone happens to open a page that draws the switch. A run started by Ctrl+Alt+M without
        // the window ever being looked at must narrate exactly as much as one started from the page.
        ActivityLog.DetailEnabled = _settings.ConsoleDetail;
        Loaded += (_, _) => StartRemoteControl();   // phone/web remote control + live status + session sync
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Global panic key: F12 stops everything even while the game is focused.
        //
        // The RETURN VALUE is the whole story here. Only one app on the system may own a hotkey,
        // F12 is the most fought-over key on a gaming PC (it is Steam's screenshot key, among
        // others), and losing the race used to be completely silent — the panic key just didn't
        // exist, discovered mid-run. So: the failure is named out loud, and a low-level keyboard
        // hook watches for F12 as well, which no other app can take away. Both firing on one press
        // is handled by a debounce in Panic() itself.
        bool got = RegisterHotKey(_hwnd, PANIC_HOTKEY_ID, 0, VK_F12);
        bool hooked = Input.PanicKey.Install(VK_F12, () => Dispatcher.BeginInvoke(new Action(Panic)));
        if (!got && !hooked)
        {
            ActivityLog.Record(MergeSource, "⚠ F12 could not be claimed OR watched — another app owns it and "
                                          + "the keyboard hook failed. The Stop buttons still work.");
            ActivityLog.Record(QuestSource, "⚠ F12 could not be claimed OR watched — another app owns it and "
                                          + "the keyboard hook failed. The Stop buttons still work.");
        }
        else if (!got)
            Diag.BotLog.Log("app", "another app owns F12 — the keyboard watcher is covering it");
        // Repeat the last Input-probe action while EQ is focused (safe chord so it won't clash with in-game F9).
        RegisterHotKey(_hwnd, PROBE_HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F9);
        // Ctrl+Alt+M rather than a bare key: this fires while EQ has focus, so a single letter
        // would be swallowed out of the middle of anything the user typed in game.
        RegisterHotKey(_hwnd, MERGE_HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_M);

        // F7 STARTS THE GRIND, F8 STOPS IT — watched, not claimed.
        //
        // They used to be one key, Shift+F12, sharing with the panic button. It did not work — the
        // registration lost a race nobody could see — and making the sharing safe took three rounds
        // of review findings on the one feature that is not allowed to regress. Two quiet function
        // keys remove the question.
        //
        // And they go through the low-level WATCHER rather than RegisterHotKey, because a bare
        // registration claims a key system-wide and SWALLOWS it: bind F7 to a hotbar slot in game,
        // open this app, and that slot stops working until it closes — with nothing on screen to
        // connect the two. The watcher observes without taking, and cannot lose a race to another
        // app either. The price is that it sees keys everywhere, so each handler checks the press
        // was aimed at the game before acting.
        Input.PanicKey.Watch(VK_F7, () => Dispatcher.BeginInvoke(new Action(StartGrindFromHotkey)));
        Input.PanicKey.Watch(VK_F8, () => Dispatcher.BeginInvoke(new Action(StopGrindFromHotkey)));
        Diag.BotLog.Log("app", "F7 starts the grind and F8 stops it while EverQuest is in front; "
                             + "F12 stops everything from anywhere. None of the three are taken away from the game.");
    }

    /// <summary>Was that key press meant for us? The watcher sees every key in the system, so a
    /// start key must not fire because someone hit F7 in a browser. The game being in front — or
    /// this app, so the buttons and the keys agree — is the whole test.</summary>
    private bool AimedAtGame()
    {
        IntPtr fg = GetForegroundWindow();
        return fg != IntPtr.Zero && (fg == _grindTarget || fg == _hwnd);
    }

    private void StopGrindFromHotkey()
    {
        if (!AimedAtGame()) return;
        if (_grind is not { Running: true } && _hunt is not { Running: true }) return;
        StopGrind_Click(this, new RoutedEventArgs());
        ShowToast("Grind stopped (F8)");
    }

    /// <summary>Start the grind/hunt from F7. Must be safe to call with the Grind page never having
    /// been opened, and with something else already running.</summary>
    private void StartGrindFromHotkey()
    {
        if (!AimedAtGame()) return;
        // SILENTLY, like the stop key. A held key auto-repeats about thirty times a second, and
        // every repeat after the first would land on "already running" — dozens of toasts, the
        // first of them arriving milliseconds after the start and flatly contradicting it.
        if (_grind is { Running: true } || _hunt is { Running: true }) return;
        if (_questRun is { Running: true } || _questStarting || _mergeRun is { Running: true })
        { ShowToast("Something else is running — F12 first"); return; }
        StartGrind_Click(this, new RoutedEventArgs());
    }

    private DateTime _lastPanic = DateTime.MinValue;
    private int _panicHookTick;
    private int _gameSeekTick;

    /// <summary>
    /// EVERYTHING stops — every role that can send input, the launcher automation included, which
    /// the old panic path missed even though auto-login moves the mouse and clicks like any role.
    /// Debounced because two watchers may both see one press; announced because a panic key the
    /// user cannot tell fired is indistinguishable from one that doesn't work — which is exactly
    /// how it was reported.
    /// </summary>
    private void Panic()
    {
        if ((DateTime.Now - _lastPanic).TotalMilliseconds < 400) return;
        _lastPanic = DateTime.Now;
        StopGrind_Click(this, new RoutedEventArgs());
        StopFollower_Click(this, new RoutedEventArgs());
        StopMouseDemo();
        _login?.Stop();
        // The Keymaps page owns the mouse and keyboard too — the auto-capture scrolls the bind
        // list for over a minute, and the apply TYPES into the game. Review caught both missing
        // from the first version of this method, under a toast claiming everything had stopped.
        _kmAuto?.Cancel();
        _kmApplyCts?.Cancel();
        _questStartCancelled = true;
        // Recorded into BOTH module consoles, not a neutral source neither one shows: whoever
        // pressed the panic key is watching one of these, and the line saying it worked has to be
        // the next line they see there.
        ActivityLog.Record(MergeSource, "■ F12 — everything stopped.");
        ActivityLog.Record(QuestSource, "■ F12 — everything stopped.");
        ShowToast("F12 — everything stopped");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == PANIC_HOTKEY_ID)
            {
                Panic();
                handled = true;
            }
            else if (id == PROBE_HOTKEY_ID)
            {
                _lastProbe?.Invoke();   // fires with EQ focused — this is what makes probe input actually land
                handled = true;
            }
            else if (id == MERGE_HOTKEY_ID)
            {
                ToggleMergeRun();       // the whole point is not having to leave the game to press it
                handled = true;
            }

        }
        return IntPtr.Zero;
    }

    // ---------------- Window shell: chrome, left-nav, Command Center ----------------

    private void TitleMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Chip_Click(object sender, MouseButtonEventArgs e) { if (_ready) NavProfile.IsChecked = true; }
    private void ChipTier_Click(object sender, MouseButtonEventArgs e)
    { if (_ready) NavLicensing.IsChecked = true; e.Handled = true; }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton rb && rb.Tag is string name) ShowPanel(name);
    }

    private void ShowPanel(string name)
    {
        foreach (string p in Panels)
            if (FindName(p) is UIElement el)
                el.Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
        if (name == "PanelHome") RefreshHome();
        if (name == "PanelGrind") { InitArtUi(); AutoTargetEq(); }
        if (name == "PanelSequencer") InitSequencerUi();
        if (name == "PanelKeymaps") InitKeymapsUi();
        if (name == "PanelQuesting") InitQuestingUi();
        if (name == "PanelAutoMerge") InitMergeUi();
        if (name == "PanelActivity") InitActivityUi();
        if (name == "PanelProfile") UpdateProfilePanel();
        if (name == "PanelData") EnsureDataLoaded();
        if (name == "PanelSessions") RefreshSessions();
        if (name == "PanelCombat") RefreshCombatPanel();
        if (name == "PanelLicensing" && ConnList.ItemsSource is null) _ = RefreshConnections();
        // The OCR tab shows two things that other pages change under it — the square's size (any icon
        // pick can write it) and which stored reference disagrees with it. The radio button stays
        // checked across navigation, so Checked does NOT re-fire and only this reaches it.
        if (name == "PanelSettings") SyncSwatchUi();
    }

    private void HomeGoGrind_Click(object sender, RoutedEventArgs e) => NavGrind.IsChecked = true;
    private void HomeGoHeat_Click(object sender, RoutedEventArgs e) => NavMaps.IsChecked = true;
    private void HomeGoLogin_Click(object sender, RoutedEventArgs e) => NavLogin.IsChecked = true;

    /// <summary>Flash a "saved" pill in the title bar that fades away — shown whenever settings are saved.</summary>
    private void ShowToast(string msg)
    {
        SavedToastText.Text = msg;
        SavedToast.BeginAnimation(UIElement.OpacityProperty, null);
        SavedToast.Opacity = 1;
        SavedToast.Visibility = Visibility.Visible;
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1, To = 0,
            BeginTime = TimeSpan.FromSeconds(1.4),
            Duration = new Duration(TimeSpan.FromSeconds(1.1))
        };
        fade.Completed += (_, _) => SavedToast.Visibility = Visibility.Collapsed;
        SavedToast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Always-on 300ms tick: refresh the foreground label, the character chip, and the home stats.</summary>
    private void TickUi()
    {
        UpdateForeground();
        UpdateTopmost();
        if (!_ready) return;
        // The panic hook gets a heartbeat. Windows silently REMOVES a low-level hook whose UI
        // thread stalls past its timeout — and when another app owns F12 the hook is the only
        // panic route, so a silently dead hook is the original bug back again with no symptom.
        // There is no API to ask whether a hook still lives; re-seating it every ~30 s costs a
        // millisecond and makes the answer always yes.
        if (++_panicHookTick >= 100) { _panicHookTick = 0; Input.PanicKey.Refresh(); }
        // The OTHER half of the game-window watchdog below: a game that APPEARED. Launching
        // outside the app (or after a close) used to go unnoticed until the user visited a page
        // that happened to look. Every ~3 s while nothing is targeted is cheap — one EnumWindows —
        // and never runs while a manual ◎ pick or a live target is held, so it can't fight either.
        if (_grindTarget == IntPtr.Zero && ++_gameSeekTick >= 10)
        {
            _gameSeekTick = 0;
            AutoTargetEq();
            if (_grindTarget != IntPtr.Zero)
            {
                LaunchStatus.Text = "Game detected — already running.";
                LoginLogLine("Found the game running — connected to it.");
            }
        }
        // The watchdog for a game that has CLOSED. Every other check of the handle happens when
        // the user does something; this is the one that notices within a heartbeat and says so,
        // instead of the app insisting the game is open until someone tries to use it.
        // Stamped every tick the game is in front, so a close can say how long it had been sitting
        // unattended beforehand — the measurement that tests the idle-kick theory.
        if (_grindTarget != IntPtr.Zero && GetForegroundWindow() == _grindTarget) _gameFocusedAt = DateTime.UtcNow;
        if (GameWindowDied())
        {
            GrindTargetLabel.Text = "game not found — launch EQ, then click ◎";
            LaunchStatus.Text = "The game window closed. Launch is ready whenever you are.";
            GrindLogLine("The game window closed — it'll be re-detected when it's back. Checking in a moment "
                       + "whether the game itself is gone or has only rebuilt its window.");
            LoginLogLine("The game window closed. Launch is ready to run again. Checking in a moment whether "
                       + "the game itself is gone or has only rebuilt its window.");
        }
        // The verdict on the line above, once the client has had time to finish whatever it was
        // doing. See PollGameDeathVerdict for why it cannot be answered on the same tick.
        PollGameDeathVerdict();
        UpdateChip();
        if (PanelHome.Visibility == Visibility.Visible) RefreshHome();
    }

    /// <summary>Float above other apps (browser, etc.) but step aside for the game: when EverQuest is
    /// the foreground window we drop Topmost so it can cover us; otherwise we stay on top.</summary>
    /// <summary>
    /// True while the launch splash is covering the app.
    ///
    /// This is the reason the splash "sometimes wasn't there at all". Setting Topmost to true does
    /// not merely mark a window always-on-top, it RAISES it to the front of the topmost band — and
    /// UpdateTopmost runs from the 300 ms UI tick, so on any install with always-on-top enabled the
    /// main window jumped over the splash about a third of a second in, every launch. The splash is
    /// topmost too; it just showed first, and first loses.
    /// </summary>
    internal bool SplashUp { get; set; }

    private void UpdateTopmost()
    {
        // Not while the splash is up. Whatever the setting says, the answer for the next second and
        // a half is "stay where you are".
        if (SplashUp) return;
        // On top of everything (Hayden's two-monitor preference). Only step aside DURING an auto-login
        // so the app can't cover the launcher and read its own window with OCR.
        bool launching = _login is { Running: true };
        bool want = _settings.AlwaysOnTop && !launching;
        if (Topmost != want) Topmost = want;
    }

    private void TopmostBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = TopmostBox.IsChecked == true;
        _settings.Save();
        UpdateTopmost();
        ShowToast(_settings.AlwaysOnTop ? "Staying on top (except the game)" : "Normal window order");
    }

    private void RefreshHome()
    {
        var (role, a, k, x) = HubStats();
        TimeSpan up = DateTime.Now - _sessionStart;
        HomeSession.Text = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:00}m" : $"{up.Minutes}m";
        HomeActions.Text = a.ToString("N0");
        HomeKills.Text = k.ToString("N0");
        HomeXp.Text = x.ToString("N0");
        HomeRole.Text = role;
        bool running = _grind is { Running: true };
        bool paused = running && _grind!.Stats.Paused;
        HomeStatusPill.Text = !running ? "Idle" : (paused ? "Paused — EQ not focused" : "Grinding");
        HomeStatusPill.Foreground = (running && !paused) ? Hex("#B6F2C9") : Hex("#9AA7B4");
        HomeStatusDot.Fill = (running && !paused) ? Hex("#7CE38B") : Hex("#5D6878");
        HomeTarget.Text = _grindTarget == IntPtr.Zero ? "no target set" : "EverQuest targeted";
        HomeChar.Text = string.IsNullOrWhiteSpace(_settings.HubUsername) ? "—" : _settings.HubUsername;
        HomeTier.Text = (_hub.Last is { Authorized: true } l) ? (l.Tier ?? "—") : "not checked in";
        RefreshHomeDps();
    }

    private void UpdateChip()
    {
        // EVERYTHING HERE COMES OFF THE SCAN. It used to be assembled from _settings.HubClass /
        // HubLevel / HubServer -- fields left over from when you typed your character in by hand,
        // which nothing has written since the app learned to read the inventory window. That is
        // why the bio never matched the character: it was showing a form, not a character.
        string name = CharacterName();
        ChipName.Text = name.Length == 0 ? "Not signed in" : name;
        ChipAvatar.Text = name.Length == 0 ? "?" : name.Substring(0, 1).ToUpperInvariant();
        bool running = _grind is { Running: true } && !_grind.Stats.Paused;
        ChipDot.Fill = running ? Hex("#7CE38B") : Hex("#5D6878");

        // Best level only ever climbs: taking a fresh class drops the CURRENT loadout to that
        // class's level, but the loadout you levelled before still remembers its own.
        if (_loadouts.BestLevel > _settings.HubMaxLevel)
        { _settings.HubMaxLevel = _loadouts.BestLevel; _settings.Save(); }

        string charLine = CurrentLoadoutLine() ?? "";
        string? tier = (_hub.Last is { Authorized: true } l) ? l.Tier : null;
        if (tier != null)
        {
            ChipTierBadge.Visibility = Visibility.Visible;
            StyleTierPill(tier);
            ChipTierText.Text = tier.ToUpperInvariant();
            ChipSub.Text = charLine.Length > 0 ? charLine : CharacterServer();
        }
        else
        {
            ChipTierBadge.Visibility = Visibility.Collapsed;
            ChipSub.Text = charLine.Length > 0 ? charLine
                         : (name.Length == 0 ? "Read your inventory to fill this in" : "not checked in");
        }
        if (LicCharText != null)
            LicCharText.Text = charLine.Length > 0 ? charLine : "— read your inventory once";
        UpdateProfilePanel();
        RefreshLoadoutUi();               // loadout line + hover menu; no-op until one is recorded
    }

    /// <summary>
    /// The subscription pill, styled to match the one the website puts in its own header bar, so
    /// the app and the portal read as the same product.
    ///
    /// The site tints rather than fills: background is the tier colour at 13% alpha, the border is
    /// the same colour at 40%, and THE TEXT IS THE TIER COLOUR. The app had been filling the pill
    /// solid and printing near-black on top, which is the treatment the site reserves for Plaid
    /// alone — so every tier looked like the top tier. Plaid keeps its Spaceballs three-stop
    /// gradient with dark text, because on Plaid that IS the point.
    ///
    /// Colours come from <see cref="TierColor"/>, which mirrors the hub's $TIERS table; geometry
    /// (2x8 padding, 1px border, 10px bold) mirrors the site's .tier rule.
    /// </summary>
    private void StyleTierPill(string tier)
    {
        // Matched to the site's pill to the pixel: 2px/8px padding, 10px at weight 800, and the
        // same letter spacing. The app had 10px/3px padding, which is what made it read as a fat
        // oval next to the website's neat pill.
        ChipTierBadge.Padding = new Thickness(8, 2, 8, 2);
        ChipTierBadge.BorderThickness = new Thickness(1);
        ChipTierText.FontSize = 10;
        ChipTierText.FontWeight = FontWeights.ExtraBold;
        ChipTierText.FontFamily = new FontFamily("Segoe UI");

        Color c = TierColor(tier);

        // The rim light: a 1px border at full strength plus a soft bloom in the SAME colour, so
        // the pill says which tier you pay for from across the room rather than only up close.
        ChipTierBadge.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = c, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.75,
        };

        if (string.Equals(tier, "Plaid", StringComparison.OrdinalIgnoreCase))
        {
            ChipTierBadge.Background = PlaidBrush();
            ChipTierBadge.BorderBrush = Hex("#F2D9FF");
            ChipTierText.Foreground = Hex("#0B0F16");
            return;
        }

        ChipTierBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        ChipTierBadge.BorderBrush = new SolidColorBrush(c);          // full strength, not 0x66
        ChipTierText.Foreground = new SolidColorBrush(c);
    }

    /// <summary>Tier accent colours, mirroring the hub's $TIERS table.</summary>
    private static Color TierColor(string tier) => tier switch
    {
        "Plaid" => Color.FromRgb(0xE8, 0x79, 0xF9),
        "Hyper" => Color.FromRgb(0x7C, 0xE3, 0x8B),
        "Ludicrous" => Color.FromRgb(0xFF, 0xB7, 0x4D),
        "LDT Clan" => Color.FromRgb(0x4F, 0xC3, 0xF7),
        _ => Color.FromRgb(0x9A, 0xA7, 0xB4),
    };

    /// <summary>Brighter sibling of each tier's fill — the pill's rim light.</summary>
    private Brush TierBorder(string tier) => tier switch
    {
        "Plaid" => Hex("#F2D9FF"),
        "Hyper" => Hex("#D9FFE3"),
        "Ludicrous" => Hex("#FFE3B8"),
        "LDT Clan" => Hex("#B8E8FF"),
        _ => Hex("#3E5A78"),
    };

    private Brush TierFill(string tier) => tier switch
    {
        "Plaid" => PlaidBrush(),
        "Hyper" => Hex("#7CE38B"),
        "Ludicrous" => Hex("#FFB74D"),
        "LDT Clan" => Hex("#4FC3F7"),
        _ => Hex("#20303F"),
    };

    private static LinearGradientBrush PlaidBrush()
    {
        var g = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x79, 0xF9), 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x4F, 0xC3, 0xF7), 0.5));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x7C, 0xE3, 0x8B), 1));
        return g;
    }

    // ---------------- Tab 1: log reader ----------------

    private void BrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick ANY file inside your EQL log folder",
            CheckFileExists = false, CheckPathExists = true, ValidateNames = false,
            FileName = "Select this folder"
        };
        if (dlg.ShowDialog() == true)
        {
            string? dir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(dir)) LogFolderBox.Text = dir;
        }
    }

    private void BrowseIni_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate eqclient.ini",
            Filter = "eqclient.ini|eqclient.ini|INI files (*.ini)|*.ini|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) IniPathBox.Text = dlg.FileName;
    }

    private void FindNewest_Click(object sender, RoutedEventArgs e)
    {
        _currentLog = EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        StatusLog.Text = _currentLog is null ? "No eqlog_*.txt found in that folder." : "Newest log: " + _currentLog;
        TryAutoFillCharacter();
    }

    private void EnsureLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EqClientIni.Result r = EqClientIni.EnsureLoggingEnabled(IniPathBox.Text.Trim());
            StatusLog.Text = r.Message;
            AddSystem(r.Message + (r.BackupPath is null ? "" : $"  (backup: {r.BackupPath})"));
        }
        catch (Exception ex) { StatusLog.Text = "Error: " + ex.Message; }
    }

    private void ReadAll_Click(object sender, RoutedEventArgs e) => StartWatch(true);
    private void Tail_Click(object sender, RoutedEventArgs e) => StartWatch(false);

    private void StartWatch(bool fromStart)
    {
        StopWatch();
        _currentLog ??= EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        if (_currentLog is null) { StatusLog.Text = "No log selected — click 'Find newest log' first."; return; }
        _watcher = new EqLogWatcher(_currentLog);
        _watcher.LineRead += line => Dispatcher.Invoke(() => OnLine(line));
        _watcher.Info += info => Dispatcher.Invoke(() => StatusLog.Text = info);
        _watcher.Start(fromStart);
    }

    private void StopWatch() { _watcher?.Dispose(); _watcher = null; }
    private void StopTail_Click(object sender, RoutedEventArgs e) { StopWatch(); StatusLog.Text = "Stopped."; }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { LogList.Items.Clear(); _locSeen = 0; LocCount.Text = "0"; }

    private void OnLine(string raw)
    {
        LogEvent ev = LogEventParser.Parse(raw);
        if (ev.Kind == LogEventKind.Location) { _locSeen++; LocCount.Text = _locSeen.ToString(); }
        AddLine(ev);
    }

    private void AddLine(LogEvent ev)
    {
        string tag = ev.Kind == LogEventKind.Location && ev.X is not null
            ? $"[LOC x={ev.X:0.0} y={ev.Y:0.0} z={ev.Z:0.0}] "
            : $"[{ev.Kind}] ";
        var item = new ListBoxItem
        {
            Content = tag + ev.Text,
            Foreground = ColorFor(ev.Kind),
            FontWeight = ev.Kind == LogEventKind.Location ? FontWeights.Bold : FontWeights.Normal
        };
        LogList.Items.Add(item);
        if (LogList.Items.Count > 2000) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(item);
    }

    private void AddSystem(string text) =>
        LogList.Items.Add(new ListBoxItem { Content = "[app] " + text, Foreground = ColorFor(LogEventKind.System) });

    private static Brush ColorFor(LogEventKind kind) => kind switch
    {
        LogEventKind.Location => Hex("#7CE38B"),
        LogEventKind.Zone => Hex("#4FC3F7"),
        LogEventKind.Combat => Hex("#FF8A80"),
        LogEventKind.Experience => Hex("#FFCB6B"),
        LogEventKind.Loot => Hex("#B39DDB"),
        LogEventKind.Death => Hex("#FF5370"),
        LogEventKind.System => Hex("#4FC3F7"),
        _ => Hex("#9AA7B4"),
    };

    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    // ---------------- Tab 2: input probe ----------------

    private void InitElevationBanner()
    {
        bool admin = InputProbe.IsCurrentProcessElevated();
        if (admin)
        {
            ElevBorder.Background = Hex("#337CE38B");
            ElevBorder.BorderBrush = Hex("#7CE38B");
            ElevBanner.Text = "Running as administrator ✓  — if input still fails, the game likely uses DirectInput; try Attach+SendInput or the child control.";
        }
        else
        {
            ElevBorder.Background = Hex("#33FFCB6B");
            ElevBorder.BorderBrush = Hex("#FFCB6B");
            ElevBanner.Text = "NOT running as administrator.  If EQL runs elevated, Windows silently blocks our input — this is the most likely reason nothing happened. Relaunch as admin and retry. →";
        }
    }

    private void UpdateForeground()
    {
        IntPtr h = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        ForegroundLabel.Text = $"foreground: \"{sb}\"  (hwnd 0x{h.ToInt64():X})";
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        WinList.ItemsSource = WindowFinder.ListWindows();
        Log($"{WinList.Items.Count} visible windows listed.");
    }

    private void GuessEq_Click(object sender, RoutedEventArgs e)
    {
        if (WinList.ItemsSource is null) WinList.ItemsSource = WindowFinder.ListWindows();
        WindowInfo? eq = WindowFinder.GuessEverQuest();
        if (eq is null) { Log("Couldn't spot an EverQuest window. Is the game running?"); return; }
        WinList.SelectedItem = eq;
        WinList.ScrollIntoView(eq);
        Log("Guessed EverQuest: " + eq);
    }

    private void ListChildren_Click(object sender, RoutedEventArgs e)
    {
        if (WinList.SelectedItem is not WindowInfo w) { Log("Select a top-level window first."); return; }
        var kids = WindowFinder.ListChildren(w.Handle);
        ChildList.ItemsSource = kids;
        Log($"{kids.Count} child control(s) under {w.ProcessName}. If the frame ignores input, try posting to a child.");
    }

    /// <summary>The window we send to: a selected child control if any, else the selected top-level window.</summary>
    private WindowInfo? Target()
    {
        if (ChildList.SelectedItem is WindowInfo c) return c;
        if (WinList.SelectedItem is WindowInfo w) return w;
        Log("Pick the EverQuest window (and optionally a child control) first.");
        return null;
    }

    private void LogTargetElevation(WindowInfo t)
    {
        var (ok, elevated) = InputProbe.GetProcessElevation(t.ProcessId);
        string me = InputProbe.IsCurrentProcessElevated() ? "elevated" : "normal";
        string tgt = !ok ? "unknown (couldn't query — often means it's higher-integrity than us!)" : (elevated ? "elevated" : "normal");
        Log($"   target pid {t.ProcessId}: {tgt};  this app: {me}");
    }

    /// <summary>
    /// Every probe fires through here. The core problem: clicking a WPF button makes THIS app the
    /// focused window, so any foreground input lands on us, not the game. So we (optionally) count
    /// down to give you time to click into EQ, and we remember the action so Ctrl+Alt+F9 can re-fire
    /// it while EQ is focused — no need to alt-tab back here between tries.
    /// </summary>
    private async void RunProbe(string label, Action fire)
    {
        _lastProbe = fire;
        if (ProbeCountdownBox.IsChecked == true)
            for (int s = 3; s >= 1; s--) { Log($"Click into EQ now — '{label}' fires in {s}…"); await Task.Delay(800); }
        try { fire(); }
        catch (Exception ex) { Log($"'{label}' error: {ex.Message}"); return; }
        Log($"'{label}' fired. Tip: with EQ focused, tap Ctrl+Alt+F9 to repeat it without clicking back here.");
    }

    private void PostTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Target() is not WindowInfo t) return;
        if (!TryResolveVk(out ushort vk, out string label)) return;
        RunProbe($"PostMessage {label}", () =>
        {
            InputProbe.PostKey(t.Handle, vk, extended: ExtendedBox.IsChecked == true);
            Log($"   PostMessage → {t.ProcessName} hwnd 0x{t.Handle.ToInt64():X}. Character react?");
            LogTargetElevation(t);
        });
    }

    private void SendMsgTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Target() is not WindowInfo t) return;
        if (!TryResolveVk(out ushort vk, out string label)) return;
        RunProbe($"SendMessage {label}", () =>
        {
            InputProbe.SendKey(t.Handle, vk, extended: ExtendedBox.IsChecked == true);
            Log($"   SendMessage → {t.ProcessName} hwnd 0x{t.Handle.ToInt64():X}. Character react?");
        });
    }

    private void AttachTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Target() is not WindowInfo t) return;
        if (!TryResolveVk(out ushort vk, out string label)) return;
        RunProbe($"Attach+SendInput {label}", () =>
        {
            InputProbe.AttachedSendInputKey(t.Handle, vk);
            Log($"   Attach+SendInput → {t.ProcessName}. This often reaches DirectInput games. React?");
        });
    }

    private void SendInputFg_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveVk(out ushort vk, out string label)) return;
        RunProbe($"SendInput {label}", () =>
        {
            InputProbe.SendInputKey(vk);
            Log($"   SendInput '{label}' → the focused window. If EQ was focused and your guy reacted, this is exactly the method the Grind tab uses.");
        });
    }

    private void LocTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Target() is not WindowInfo t) return;
        IntPtr h = t.Handle;
        string name = t.ProcessName;
        RunProbe($"/loc → {name}", () =>
        {
            Log($"   Sending /loc to {name} (Enter, type /loc, Enter)…");
            Task.Run(() =>
            {
                InputProbe.PostKey(h, VK_RETURN); System.Threading.Thread.Sleep(150);
                foreach (char ch in "/loc") { InputProbe.PostChar(h, ch); System.Threading.Thread.Sleep(25); }
                System.Threading.Thread.Sleep(80);
                InputProbe.PostKey(h, VK_RETURN);
            }).ContinueWith(_ => Dispatcher.Invoke(() =>
                Log("   /loc sent. If it worked, a new [LOC …] line appears on tab 1 (start Live tail first).")));
        });
    }

    private void RelaunchAdmin_Click(object sender, RoutedEventArgs e)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "EQAvatar.Spike.exe");
        if (!File.Exists(exe)) exe = Environment.ProcessPath ?? exe;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Log("Relaunch as admin cancelled/failed: " + ex.Message); }
    }

    private bool TryResolveVk(out ushort vk, out string label)
    {
        vk = 0; label = "";
        string k = KeyBox.Text.Trim();
        if (string.IsNullOrEmpty(k)) { Log("Type a key to send."); return false; }
        if (k.Length >= 2 && (k[0] is 'F' or 'f') && int.TryParse(k.Substring(1), out int fn) && fn is >= 1 and <= 12)
        { vk = (ushort)(0x70 + (fn - 1)); label = "F" + fn; return true; }
        vk = InputProbe.VkFromChar(k[0]);
        label = k[0].ToString().ToUpperInvariant();
        return true;
    }

    private void Log(string msg)
    {
        ProbeLog.AppendText(msg + Environment.NewLine);
        ProbeLog.ScrollToEnd();
    }

    // ---------------- Maps: in-game overlay + PNG export ----------------

    /// <summary>Float the CURRENT zone map (walls + heat + trail + live marker) over the game.
    /// Click again to close. The overlay window's own "ghost" button toggles click-through.</summary>
    private void MapsOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay != null) { _overlay.Close(); _overlay = null; MapsOverlayBtn.Content = "Overlay in-game"; return; }
        _overlay = new MapOverlayWindow();
        _overlay.Closed += (_, _) => { _overlay = null; MapsOverlayBtn.Content = "Overlay in-game"; };
        SyncOverlay();
        _overlay.Show();
        MapsOverlayBtn.Content = "Close overlay";
    }

    /// <summary>Push the main Maps view's state (zone, layers, heat) into the floating overlay.</summary>
    private void SyncOverlay()
    {
        if (_overlay is null || _mapLib is null || _mapZone is null) return;
        MapData? data = _mapLib.Get(_mapZone, MapsPrefs());
        _overlay.ShowMap(data, ZoneTable.NameFor(_mapZone));
        _overlay.SetLayers(showHeat: MapsHeatBox.IsChecked == true, showTrail: MapsTrailBox.IsChecked == true);
        _overlay.SetHeat(MapsView.HeatPoints);
        PushTetherToMaps();
    }

    /// <summary>Paint (or clear) the live tether circle on the Maps page + in-game overlay while a
    /// tethered Hunt is running — the pen the bot has drawn itself.</summary>
    private void PushTetherToMaps()
    {
        if (_settings.HuntTetherEnabled && _hunt is { Running: true } h
            && h.AnchorEw is double ew && h.AnchorNs is double ns)
        {
            (double mx, double my) = EqMapParser.MapFromLoc(ns: ns, ew: ew);
            double r = Math.Max(10, _settings.HuntTetherRadius);
            MapsView.SetTether(mx, my, r, true);
            _overlay?.SetTether(mx, my, r, true);
        }
        else
        {
            MapsView.SetTether(0, 0, 0, false);
            _overlay?.SetTether(0, 0, 0, false);
        }
    }

    private void MapsExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"eqavatar-map-{_mapZone ?? "zone"}.png" };
            if (dlg.ShowDialog() != true) return;
            var rtb = new RenderTargetBitmap((int)Math.Max(320, MapsView.ActualWidth), (int)Math.Max(240, MapsView.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(MapsView);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
            MapsStatus.Text = "Exported → " + dlg.FileName;
        }
        catch (Exception ex) { MapsStatus.Text = "Export failed: " + ex.Message; }
    }

    // ---------------- Tab 4: grind role ----------------

    private void TargetEq_Click(object sender, RoutedEventArgs e)
    {
        WindowInfo? w = WinList.SelectedItem as WindowInfo ?? WindowFinder.GuessEverQuest();
        if (w is null) { GrindTargetLabel.Text = "target: — (pick EverQuest on the Input tab, then retry)"; return; }
        _grindTarget = w.Handle;
        GrindTargetLabel.Text = $"target: {w.ProcessName} \"{w.Title}\"  0x{w.Handle.ToInt64():X}";
    }

    private void StartGrind_Click(object sender, RoutedEventArgs e)
    {
        if (_grind is { Running: true } || _hunt is { Running: true }
            || _questRun is { Running: true } || _questStarting || _mergeRun is { Running: true })
        { ShowToast("Already running — Stop (F12) first"); return; }
        AutoTargetEq();   // also notices a game window that has CLOSED since it was detected     // the game may have launched after this page opened
        if (_grindTarget == IntPtr.Zero)
        {
            SetGrindBanner(1, "CAN'T START — EverQuest window not found. Launch the game, then press ◎.");
            ShowToast("EverQuest not found");
            GrindLogLine("Start blocked: no game window. Launch EverQuest, then press the ◎ button in the header.");
            return;
        }

        _runStartedAt = DateTime.UtcNow;      // for the close-time pattern, nothing else
        var rotation = GrindRole.ParseRotation(GrindRotation.Text);
        _currentLog ??= EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        var sink = new ForegroundSendInputSink(() => _grindTarget);

        if (HuntBox.IsChecked == true)
        {
            ApplyHuntFields();
            _settings.Save();
            _hunt = new HuntRole(sink, rotation, _currentLog, _settings, _heat, CompassSvc, VitalsSvc, () => _mapZone);
            _hunt.Log += m => Dispatcher.Invoke(() => GrindLogLine(m));
            _hunt.Stopped += () => Dispatcher.Invoke(() => { _grindTimer.Stop(); UpdateGrindStats(); EndRoleSession(); });
            _hunt.Start();
            FocusGameSoon();
            _grindTimer.Start();
            Recorder.Begin(GrindModeLabel(), SnapshotGrindSettings(hunt: true));
            if (_mapsWatcher is null) StartMapsWatcher();
            GrindLogLine("HUNT mode (EXPERIMENTAL). In-game: bind 'target nearest NPC' to your Hunt target key, keep a /loc macro running, walk the area once so bounds are known — and WATCH it. F12 or tab away to stop.");
            ReportPlanReadiness();
            return;
        }

        if (rotation.Count == 0)
        {
            SetGrindBanner(1, "CAN'T START — the combat rotation is empty (open COMBAT ROTATION below).");
            ShowToast("Rotation is empty");
            GrindLogLine("Rotation-only mode needs at least one 'key,delayMs' line.");
            return;
        }
        if (CastOnlyBox.IsChecked == true)
            GrindLogLine("Cast-only is already implied by rotation-only — there is no movement or facing to skip here.");
        _grind = new GrindRole(sink, rotation, StopOnDeathBox.IsChecked == true, _currentLog, _settings);
        _grind.Log += m => Dispatcher.Invoke(() => GrindLogLine(m));
        _grind.Stopped += () => Dispatcher.Invoke(() => { _grindTimer.Stop(); UpdateGrindStats(); EndRoleSession(); });
        _grind.Start();
        FocusGameSoon();
        _grindTimer.Start();
        Recorder.Begin("Grind", SnapshotGrindSettings(hunt: false));
        if (_mapsWatcher is null) StartMapsWatcher();
        if (_currentLog is null) GrindLogLine("No log found — kills/xp/death-safety are off until you set the log folder on the Log Reader panel.");
    }

    /// <summary>
    /// Hand the foreground to EverQuest, off the UI thread, without waiting for it.
    ///
    /// Called AFTER a role has actually started — never before its validation — so a start that
    /// refuses (empty rotation, missing pick) leaves its red banner on a window the user can still
    /// see, instead of yanking them into the game and explaining nothing.
    ///
    /// Fire-and-forget by design: the roles all wait for focus themselves, and the remote-control
    /// path calls the start handler and immediately reads _grind/_hunt to report whether the start
    /// took — an await here would make it answer "could not start" every time.
    /// </summary>
    private void FocusGameSoon()
    {
        if (!_settings.FocusGameOnStart) return;
        IntPtr h = _grindTarget;
        _ = Task.Run(async () =>
        {
            bool ok = await GameFocus.BringAndSettleAsync(h, settleMs: 0);
            if (ok) return;
            try
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                    GrindLogLine("Couldn't bring EverQuest to the front — click the game and it'll pick up from there.")));
            }
            catch { /* window closing */ }
        });
    }

    /// <summary>Read the Grind keybind boxes into settings (used before a run and by Save settings).</summary>
    private void ApplyHuntFields()
    {
        if (!string.IsNullOrWhiteSpace(HuntForwardKeyBox.Text)) _settings.HuntForwardKey = HuntForwardKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HuntBackKeyBox.Text)) _settings.HuntBackKey = HuntBackKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HuntLeftKeyBox.Text)) _settings.HuntLeftKey = HuntLeftKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HuntRightKeyBox.Text)) _settings.HuntRightKey = HuntRightKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HuntTargetKeyBox.Text)) _settings.HuntTargetKey = HuntTargetKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(HuntConsiderKeyBox.Text)) _settings.HuntConsiderKey = HuntConsiderKeyBox.Text.Trim();
        _settings.HuntLocKey = HuntLocKeyBox.Text.Trim();   // may be blank (optional)
        _settings.HuntAutoAttackKey = HuntAutoAttackKeyBox.Text.Trim();   // blank = never press it
        if (int.TryParse(HuntEngageBox.Text.Trim(), out int eng))
            _settings.HuntEngageMaxMs = Math.Clamp(eng, 200, 2000);
        if (int.TryParse(HuntRestBox.Text.Trim(), out int r)) _settings.HuntRestSeconds = Math.Clamp(r, 0, 600);
        _settings.HuntMode = HuntBox.IsChecked == true;
        _settings.GrindStance = StanceDef.IsChecked == true ? "defensive" : StanceDir.IsChecked == true ? "directive" : "aggressive";
        _settings.HuntHostileOnly = HostileSelBox.SelectedIndex == 1;
        _settings.HuntTetherEnabled = TetherBox.IsChecked == true;
        _settings.HuntTetherRadius = (int)TetherRope.Value;
        _settings.GrindRotationText = GrindRotation.Text;
        _settings.GrindTargetMobs = TargetMobsBox.Text;
        _settings.GrindBardMode = BardBox.IsChecked == true;
        _settings.GrindCastOnly = CastOnlyBox.IsChecked == true;
        _settings.LevEnabled = LevBox.IsChecked == true;
        _settings.LevCastKey = LevKeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(LevNameBox.Text)) _settings.LevBuffName = LevNameBox.Text.Trim();
        ApplyVitalsFields();
        _settings.GrindMode = GrindModeSetting();
        _settings.WaypointOrder = WaypointOrderBox.SelectedIndex switch { 1 => "random", 2 => "loop", _ => "sequence" };
    }

    /// <summary>Fill the Grind keybind boxes from saved settings on load.</summary>
    private void InitGrindTab()
    {
        HuntBox.IsChecked = _settings.HuntMode;
        HuntForwardKeyBox.Text = _settings.HuntForwardKey;
        HuntBackKeyBox.Text = _settings.HuntBackKey;
        HuntLeftKeyBox.Text = _settings.HuntLeftKey;
        HuntRightKeyBox.Text = _settings.HuntRightKey;
        HuntTargetKeyBox.Text = _settings.HuntTargetKey;
        HuntConsiderKeyBox.Text = _settings.HuntConsiderKey;
        HuntLocKeyBox.Text = _settings.HuntLocKey;
        HuntAutoAttackKeyBox.Text = _settings.HuntAutoAttackKey;
        HuntEngageBox.Text = _settings.HuntEngageMaxMs.ToString();
        HuntRestBox.Text = _settings.HuntRestSeconds.ToString();
        (_settings.GrindStance switch { "defensive" => StanceDef, "directive" => StanceDir, _ => StanceAggro }).IsChecked = true;
        HostileSelBox.SelectedIndex = _settings.HuntHostileOnly ? 1 : 0;
        TetherBox.IsChecked = _settings.HuntTetherEnabled;
        TetherRope.Value = Math.Clamp(_settings.HuntTetherRadius, 10, 1500);
        TetherLabel.Text = $"{(int)TetherRope.Value} units";
        GrindRotation.Text = _settings.GrindRotationText ?? "";
        TargetMobsBox.Text = _settings.GrindTargetMobs;
        BardBox.IsChecked = _settings.GrindBardMode;
        CastOnlyBox.IsChecked = _settings.GrindCastOnly;
        LevBox.IsChecked = _settings.LevEnabled;
        LevKeyBox.Text = _settings.LevCastKey;
        LevNameBox.Text = _settings.LevBuffName;
        GrindModeBox.SelectedIndex = !_settings.HuntMode ? 4 : (_settings.GrindMode ?? "hunt") switch
        { "camp" => 1, "zone" => 2, "waypoints" => 3, _ => 0 };
        WaypointOrderBox.SelectedIndex = (_settings.WaypointOrder ?? "sequence").Trim().ToLowerInvariant() switch
        { var o when o.StartsWith("rand") => 1, var o when o.StartsWith("loop") => 2, _ => 0 };
        UpdateCompassStatus();
        InitVitalsUi();
        InitArtUi();                               // mascot scenes, tether face, tile sync
        OcrAutoBox.IsChecked = _settings.OcrAutoScan;
        if (_settings.OcrAutoScan) StartOcrAuto();
        if (!string.IsNullOrWhiteSpace(_settings.HubServer)) LoginServerBox.Text = _settings.HubServer;
        LauncherPathBox.Text = _settings.LauncherPath;
        TopmostBox.IsChecked = _settings.AlwaysOnTop;
    }

    private void SaveGrind_Click(object sender, RoutedEventArgs e)
    {
        ApplyHuntFields();
        _settings.Save();
        GrindLogLine("Settings saved.");
        ShowToast("Grind settings saved");
    }

    // ---------------- Maps (Companion-style: default + Brewall packs, heat, live marker) ----------------

    private static readonly System.Text.RegularExpressions.Regex MapsZoneRe = new(
        @"You have entered\s+(?<z>.+?)\.?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private void InitMapsTab()
    {
        string root = _settings.EqRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            try { root = Path.GetDirectoryName(_settings.LauncherPath) ?? ""; } catch { root = ""; }
        }
        MapsEqRootBox.Text = root;
        MapsRescan();
        StartMapsWatcher();
        _mapsReady = true;
    }

    private void MapsRescan_Click(object sender, RoutedEventArgs e) { MapsRescan(); StartMapsWatcher(); }

    private void MapsRescan()
    {
        string root = MapsEqRootBox.Text.Trim();
        _settings.EqRootPath = root;
        _settings.Save();
        _mapLib = new MapLibrary(string.IsNullOrWhiteSpace(root) ? null : root);
        var packs = _mapLib.Packs();

        // pack pickers: Auto + every installed pack
        foreach (ComboBox box in new[] { MapsGeoPackBox, MapsLabelPackBox })
        {
            box.Items.Clear();
            box.Items.Add("Auto");
            foreach (PackIndex p in packs) box.Items.Add(p.Pack.Id);
            box.SelectedIndex = 0;
        }

        // zone list: table order first (nice display names), then any stems the table doesn't know
        var stems = new HashSet<string>(_mapLib.Zones());
        _mapsZoneStems.Clear();
        MapsZoneBox.Items.Clear();
        foreach (ZoneTable.Zone z in ZoneTable.Zones)
        {
            if (!stems.Remove(z.Short)) continue;
            _mapsZoneStems.Add(z.Short);
            MapsZoneBox.Items.Add($"{z.Name}  ({z.Short})");
        }
        foreach (string s in stems.OrderBy(s => s)) { _mapsZoneStems.Add(s); MapsZoneBox.Items.Add(s); }

        if (packs.Count == 0)
        {
            MapsStatus.Text = "No map packs found. Point 'EQ folder' at your EverQuest install (the folder that contains 'maps') and Rescan.";
            MapsView.SetMap(null);
            return;
        }
        MapsStatus.Text = $"Found {packs.Count} pack(s): {string.Join(", ", packs.Select(p => $"{p.Pack.Id} ({p.Pack.ZoneCount} zones)"))} — pick a zone.";

        // keep the open zone if it still exists, else follow the character, else Oasis-or-first
        string? want = _mapZone ?? _charZoneStem;
        if (want != null && _mapsZoneStems.Contains(want)) LoadMapZone(want);
        else if (_mapsZoneStems.Count > 0) LoadMapZone(_mapsZoneStems.Contains("oasis") ? "oasis" : _mapsZoneStems[0]);
    }

    private MapPackPrefs MapsPrefs()
    {
        string? geo = MapsGeoPackBox.SelectedIndex > 0 ? MapsGeoPackBox.SelectedItem as string : null;
        string? lab = MapsLabelPackBox.SelectedIndex > 0 ? MapsLabelPackBox.SelectedItem as string : null;
        return new MapPackPrefs(geo, lab);
    }

    private void LoadMapZone(string stem)
    {
        if (_mapLib is null) return;
        MapData? data = _mapLib.Get(stem, MapsPrefs());
        _mapZone = stem;
        MapsView.SetMap(data);
        if (_overlay != null) { _overlay.ShowMap(data, ZoneTable.NameFor(stem)); }
        ApplyMapsLayers();
        UpdateMapsFloorChip();
        RefreshMapsHeat();
        HookPlanEditor();
        RefreshPlanOverlay();                                // this zone's waypoints + hunting shape

        int idx = _mapsZoneStems.IndexOf(stem);
        if (idx >= 0 && MapsZoneBox.SelectedIndex != idx) { _mapsReady = false; MapsZoneBox.SelectedIndex = idx; _mapsReady = true; }

        if (data is null) { MapsStatus.Text = $"No map files for '{stem}' in the installed packs."; return; }
        string src = string.Join(" · ", data.Sources.Select(s => $"L{s.Layer}:{s.PackId}"));
        string credit = data.Credits.Count > 0 ? "  —  " + string.Join("; ", data.Credits.Take(2)) : "";
        string skipped = data.Skipped > 0 ? $"  ({data.Skipped} bad lines skipped)" : "";
        MapsStatus.Text = $"{ZoneTable.NameFor(stem)} — {data.SegmentCount:n0} segments, {data.Points.Count:n0} points [{src}]{credit}{skipped}";
    }

    private void MapsZone_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!_mapsReady || MapsZoneBox.SelectedIndex < 0 || MapsZoneBox.SelectedIndex >= _mapsZoneStems.Count) return;
        LoadMapZone(_mapsZoneStems[MapsZoneBox.SelectedIndex]);
    }

    private void MapsZone_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string text = MapsZoneBox.Text.Trim();
        if (text.Length == 0) return;
        string? stem = ZoneTable.ShortFor(text);
        if (stem is null && _mapsZoneStems.Contains(text.ToLowerInvariant())) stem = text.ToLowerInvariant();
        stem ??= _mapsZoneStems.FirstOrDefault(s => s.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || ZoneTable.NameFor(s).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (stem != null && _mapsZoneStems.Contains(stem)) LoadMapZone(stem);
        else MapsStatus.Text = $"No installed map matches '{text}'.";
        e.Handled = true;
    }

    private void MapsPack_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_mapsReady && _mapZone != null) LoadMapZone(_mapZone);
    }

    private void MapsLayer_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapsReady) { ApplyMapsLayers(); RefreshMapsHeat(); }
    }

    private void ApplyMapsLayers()
    {
        MapsView.SetLayers(MapsLabelsBox.IsChecked == true, MapsLegendBox.IsChecked == true, MapsExtraBox.IsChecked == true);
        MapsView.ShowHeat = MapsHeatBox.IsChecked == true;
        MapsView.ShowTrail = MapsTrailBox.IsChecked == true;
        MapsView.InvalidateVisual();
        _overlay?.SetLayers(showHeat: MapsHeatBox.IsChecked == true, showTrail: MapsTrailBox.IsChecked == true);
    }

    /// <summary>Push this session's /loc points for the open zone into the view, in map space.</summary>
    private void RefreshMapsHeat()
    {
        if (MapsHeatBox.IsChecked != true || _mapZone is null)
        { _sessionHeatPts = null; MapsView.SetHeat(Array.Empty<System.Windows.Point>()); return; }
        // A recorded session is being replayed — hold it on screen instead of the live heat.
        if (_sessionHeatPts != null) { MapsView.SetHeat(_sessionHeatPts); MapsStatus.Text = _sessionHeatLabel; return; }
        string? heatZone = _heat.Zones.FirstOrDefault(z => ZoneTable.ShortFor(z) == _mapZone);
        if (heatZone is null) { MapsView.SetHeat(Array.Empty<System.Windows.Point>()); return; }
        var pts = _heat.PointsFor(heatZone);
        var mapPts = new List<System.Windows.Point>(pts.Count);
        foreach (System.Windows.Point p in pts)
        {
            (double mx, double my) = EqMapParser.MapFromLoc(ns: p.Y, ew: p.X);   // heat stores X=ew, Y=ns
            mapPts.Add(new System.Windows.Point(mx, my));
        }
        MapsView.SetHeat(mapPts);
        _overlay?.SetHeat(mapPts.ToArray());
    }

    private void UpdateMapsFloorChip()
    {
        var bands = MapsView.Bands;
        bool many = bands.Count > 1;
        MapsFloorUp.IsEnabled = MapsFloorDown.IsEnabled = many;
        MapsFloorChip.Text = MapsView.ActiveBand is int b && b < bands.Count
            ? $"Floor {b + 1}/{bands.Count} · z {FloorSlice.BandLabel(bands[b])}"
            : many ? $"All levels ({bands.Count} floors)" : "All levels";
    }

    private void MapsFloorUp_Click(object sender, RoutedEventArgs e) => StepMapsFloor(+1);
    private void MapsFloorDown_Click(object sender, RoutedEventArgs e) => StepMapsFloor(-1);

    private void StepMapsFloor(int dir)
    {
        var bands = MapsView.Bands;
        if (bands.Count < 2) return;
        int? next = MapsView.ActiveBand is int b
            ? (b + dir < 0 || b + dir >= bands.Count ? null : b + dir)
            : (dir > 0 ? 0 : bands.Count - 1);
        MapsView.SetBand(next);
        UpdateMapsFloorChip();
    }

    private void MapsZoomIn_Click(object sender, RoutedEventArgs e) => MapsView.ZoomStep(1.35);
    private void MapsZoomOut_Click(object sender, RoutedEventArgs e) => MapsView.ZoomStep(1 / 1.35);
    private void MapsFit_Click(object sender, RoutedEventArgs e) => MapsView.Fit();

    /// <summary>The Maps panel's own quiet log tap: zone-follow, the live marker + trail, and
    /// (when the Heat panel's live mode isn't already doing it) feeding the session heat model.</summary>
    private void StartMapsWatcher()
    {
        _mapsWatcher?.Dispose();
        _mapsWatcher = null;
        _currentLog ??= EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        if (_currentLog is null) return;
        _mapsWatcher = new EqLogWatcher(_currentLog);
        _mapsWatcher.LineRead += line => Dispatcher.Invoke(() => OnMapsLogLine(line));
        _mapsWatcher.Start(fromStart: false);
    }

    private void OnMapsLogLine(string line)
    {
        LogEvent ev = LogEventParser.Parse(line);
        _heat.Feed(ev);                              // the one shared session heat model
        FeedRecorder(ev);                            // active role session: trail + xp/aa/kill/death
        FeedCombat(ev);                              // DPS meter + per-session damage totals

        if (ev.Kind == LogEventKind.Zone)
        {
            var m = MapsZoneRe.Match(ev.Text);
            if (!m.Success) return;
            _charZoneStem = ZoneTable.ShortFor(m.Groups["z"].Value.Trim());
            if (MapsFollowBox.IsChecked == true && _charZoneStem != null
                && _charZoneStem != _mapZone && _mapsZoneStems.Contains(_charZoneStem))
                LoadMapZone(_charZoneStem);
            return;
        }
        if (ev.Kind == LogEventKind.Location && ev.X is double x && ev.Y is double y)
        {
            _lastLocEw = x; _lastLocNs = y; _lastLocAt = DateTime.Now;   // live position for remote status
            // marker only when the map on screen is the zone the character is in (or unknown)
            if (_charZoneStem is null || _charZoneStem == _mapZone)
            {
                (double mx, double my) = EqMapParser.MapFromLoc(ns: y, ew: x);
                MapsView.PushLoc(mx, my);
                _overlay?.PushLoc(mx, my);
                PushTetherToMaps();                          // circle appears once the anchor locks
            }
            if (MapsHeatBox.IsChecked == true && ++_mapsHeatTick % 12 == 0) RefreshMapsHeat();
        }
    }

    // ---------------- Follower role (group play: follow + assist a leader) ----------------

    private void FollowerTargetEq_Click(object sender, RoutedEventArgs e)
    {
        WindowInfo? w = WindowFinder.GuessEverQuest();
        if (w is null) { FollowerLogLine("No EverQuest window found — start the game on this PC first."); return; }
        _grindTarget = w.Handle;
        FollowerTargetLabel.Text = $"target: {w.ProcessName} \"{w.Title}\"  0x{w.Handle.ToInt64():X}";
    }

    private void StartFollower_Click(object sender, RoutedEventArgs e)
    {
        if (_follower is { Running: true }) { FollowerLogLine("Already running."); return; }
        ApplyFollowerFields();
        _settings.Save();
        if (string.IsNullOrWhiteSpace(_settings.FollowerLeader))
        { FollowerLogLine("Enter the leader's character name first (e.g. Bryari)."); return; }

        if (_grindTarget == IntPtr.Zero && WindowFinder.GuessEverQuest() is { } w)
        {
            _grindTarget = w.Handle;
            FollowerTargetLabel.Text = $"target: {w.ProcessName} \"{w.Title}\"  0x{w.Handle.ToInt64():X}";
        }
        if (_grindTarget == IntPtr.Zero) { FollowerLogLine("Target EverQuest first (Target EverQuest)."); return; }

        _currentLog ??= EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim());
        if (_currentLog is null)
            FollowerLogLine("No log found — auto-assist can't see the leader's fights until the log folder is set on Log Reader. Follow still works.");

        var rotation = GrindRole.ParseRotation(FollowerRotation.Text);
        var sink = new ForegroundSendInputSink(() => _grindTarget);
        _follower = new FollowerRole(sink, rotation, _currentLog, _settings);
        _follower.Log += m => Dispatcher.Invoke(() => FollowerLogLine(m));
        _follower.Stopped += () => Dispatcher.Invoke(() => { _followerTimer.Stop(); UpdateFollowerStats(); EndRoleSession(); });
        _follower.Start();
        _followerTimer.Start();
        Recorder.Begin("Follower", SnapshotFollowerSettings());
        if (_mapsWatcher is null) StartMapsWatcher();
    }

    private void StopFollower_Click(object sender, RoutedEventArgs e)
    {
        _follower?.Stop();
        _followerTimer.Stop();
        UpdateFollowerStats();
    }

    private void SaveFollower_Click(object sender, RoutedEventArgs e)
    {
        ApplyFollowerFields();
        _settings.Save();
        FollowerLogLine("Settings saved.");
        ShowToast("Follower settings saved");
    }

    /// <summary>Read the Follower boxes into settings (used before a run and by Save settings).</summary>
    private void ApplyFollowerFields()
    {
        _settings.FollowerLeader = FollowerLeaderBox.Text.Trim();
        _settings.FollowerAutoAssist = FollowerAutoAssistBox.IsChecked == true;
        if (int.TryParse(FollowerRefollowBox.Text.Trim(), out int rf)) _settings.FollowerRefollowSeconds = Math.Clamp(rf, 10, 600);
        if (int.TryParse(FollowerAssistDelayBox.Text.Trim(), out int ad)) _settings.FollowerAssistDelayMs = Math.Clamp(ad, 200, 10000);
        if (int.TryParse(FollowerMaxFightBox.Text.Trim(), out int mf)) _settings.FollowerMaxFightSeconds = Math.Clamp(mf, 5, 600);
        if (int.TryParse(FollowerRestBox.Text.Trim(), out int rs)) _settings.FollowerRestSeconds = Math.Clamp(rs, 0, 600);
    }

    /// <summary>Fill the Follower boxes from saved settings on load.</summary>
    private void InitFollowerTab()
    {
        FollowerLeaderBox.Text = _settings.FollowerLeader;
        FollowerAutoAssistBox.IsChecked = _settings.FollowerAutoAssist;
        FollowerRefollowBox.Text = _settings.FollowerRefollowSeconds.ToString();
        FollowerAssistDelayBox.Text = _settings.FollowerAssistDelayMs.ToString();
        FollowerMaxFightBox.Text = _settings.FollowerMaxFightSeconds.ToString();
        FollowerRestBox.Text = _settings.FollowerRestSeconds.ToString();
    }

    private void UpdateFollowerStats()
    {
        if (_follower is { Running: true })
        {
            string st = _follower.Stats.State;
            bool paused = st.Contains("paused", StringComparison.OrdinalIgnoreCase);
            SetFollowerBanner(paused ? 1 : 2, paused ? $"PAUSED — {st}" : $"FOLLOWING {_settings.FollowerLeader} — {st}");
        }
        else SetFollowerBanner(0, "STOPPED — press Start follower");

        FollowerStats f = _follower?.Stats ?? new FollowerStats();
        FollowerStatsLabel.Text = _follower is { Running: true }
            ? $"[{f.State}] — assists {f.Assists} · kills {f.Kills} · re-follows {f.Refollows}"
            : $"idle — assists {f.Assists} · kills {f.Kills} · re-follows {f.Refollows}";
    }

    /// <summary>kind: 0 = stopped (gray), 1 = paused (amber), 2 = active (green).</summary>
    private void SetFollowerBanner(int kind, string text)
    {
        if (FollowerBanner is null) return;
        FollowerBannerText.Text = text;
        (string bg, string bd, string dot, string fg) = kind switch
        {
            2 => ("#12261B", "#2C8C55", "#7CE38B", "#B6F2C9"),
            1 => ("#2A2410", "#7A6320", "#FFCB6B", "#FFE1A6"),
            _ => ("#20303F", "#2A4A57", "#5D6878", "#C6D2DE"),
        };
        FollowerBanner.Background = Hex(bg);
        FollowerBanner.BorderBrush = Hex(bd);
        FollowerBannerDot.Fill = Hex(dot);
        FollowerBannerText.Foreground = Hex(fg);
    }

    private void FollowerLogLine(string msg)
    {
        Diag.BotLog.Log("follower", msg);
        FollowerLog.AppendText(msg + Environment.NewLine);
        FollowerLog.ScrollToEnd();
    }

    // ---------------- Character Sheet OCR (Licensing panel card) ----------------

    private Ocr.InventorySnapshot? _lastSnap;

    private async void OcrRead_Click(object sender, RoutedEventArgs e)
    {
        OcrStatus.Text = "reading…";
        OcrSendBtn.IsEnabled = false;
        IntPtr hwnd = _grindTarget;
        if (hwnd == IntPtr.Zero && WindowFinder.GuessEverQuest() is { } w) hwnd = w.Handle;
        if (hwnd == IntPtr.Zero) { OcrStatus.Text = "no game window found"; return; }

        Ocr.InventorySnapshot? snap = await Ocr.InventoryReader.ReadAsync(hwnd, m => LicLogLine("[ocr] " + m));
        if (snap is null) { OcrStatus.Text = "inventory not found — open it in-game and retry (or tick auto-scan and it catches it for you)"; return; }
        _lastSnap = snap;
        RenderOcrSnapshot(snap);
        OcrStatus.Text = snap.Warnings.Count == 0
            ? $"read OK at {snap.CapturedAt:HH:mm:ss}"
            : $"read with {snap.Warnings.Count} warning(s): {string.Join(" · ", snap.Warnings.Take(2))}";
        LicLogLine("[ocr] parsed " + snap.Fields.Count + " rows. Raw lines below:");
        LicLogLine(snap.RawSeen);
    }

    /// <summary>Paint one snapshot into the licensing card (shared by manual + auto reads).</summary>
    private void RenderOcrSnapshot(Ocr.InventorySnapshot snap)
    {
        // auto-fill the licensing character fields from the sheet (nothing entered by hand)
        if (snap.Level is int lv) LicLevelBox.Text = lv.ToString();
        if (snap.Classes is string cls)
        {
            string first = cls.Split('/')[0];
            string? full = EqClasses.FirstOrDefault(c => c.StartsWith(first, StringComparison.OrdinalIgnoreCase)
                          || Abbrev(c).Equals(first, StringComparison.OrdinalIgnoreCase));
            if (full != null) LicClassCombo.SelectedItem = full;
        }
        if (!string.IsNullOrWhiteSpace(snap.Name) && string.IsNullOrWhiteSpace(LicUserBox.Text))
            LicUserBox.Text = snap.Name;

        string hp = Pair(snap, "hp"), mana = Pair(snap, "mana"), end = Pair(snap, "end");
        string coins = snap.Plat is long p ? $"{p:n0}p {snap.Gold:n0}g {snap.Silver:n0}s {snap.Copper:n0}c" : "—";
        OcrSummary.Text =
            $"{snap.Name ?? "?"}  {snap.Level?.ToString() ?? "?"} {snap.Classes ?? "?"}\n" +
            $"HP {hp}   Mana {mana}   End {end}   AC {snap.First("ac")?.ToString("0") ?? "?"}   " +
            $"Atk {snap.First("attack")?.ToString("0") ?? "?"}   Spd {snap.First("attack speed")?.ToString("0") ?? "?"}%\n" +
            $"STR {Stat(snap, "strength")}  STA {Stat(snap, "stamina")}  AGI {Stat(snap, "agility")}  DEX {Stat(snap, "dexterity")}  " +
            $"WIS {Stat(snap, "wisdom")}  INT {Stat(snap, "intelligence")}  CHA {Stat(snap, "charisma")}\n" +
            $"MR {Stat(snap, "sv magic")}  FR {Stat(snap, "sv fire")}  CR {Stat(snap, "sv cold")}  " +
            $"DR {Stat(snap, "sv disease")}  PR {Stat(snap, "sv poison")}  VR {Stat(snap, "sv void")}\n" +
            $"Weight {Pair(snap, "weight")}   Worn {Stat(snap, "weight worn")}   Coin {coins}";
        OcrSendBtn.IsEnabled = snap.Fields.ContainsKey("hp");

        // The sheet the game just showed us is the most trustworthy character data there is, so
        // let it own the app's idea of who this is — not just the Licensing card it was read
        // into. Without this the boxes above update and the Profile page and the title-bar chip
        // keep showing whatever was there before, which is exactly how a stale 50 WAR/DRU/BRD
        // survives a fresh read of a level 16 PAL/MNK/ENC loadout.
        if (snap.Level is not null || snap.Classes is not null || snap.Name is not null)
        {
            RecordLoadout(snap);          // promotes this class combination to the current loadout
            ApplyLicensingFields();
            _settings.Save();
            UpdateChip();                 // repaints the title-bar bio and the Profile page
        }
    }

    private static string Pair(Ocr.InventorySnapshot s, string k) =>
        s.First(k) is double a ? (s.Nth(k, 1) is double b ? $"{a:0}/{b:0}" : $"{a:0}") : "?";
    private static string Stat(Ocr.InventorySnapshot s, string k) => s.First(k)?.ToString("0") ?? "?";
    private static string Abbrev(string cls) => cls.Length <= 3 ? cls.ToUpperInvariant()
        : cls.Replace(" ", "")[..3].ToUpperInvariant();

    private async void OcrSend_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSnap is not { } s) return;
        OcrStatus.Text = "sending…";
        ApplyLicensingFields();          // keep name/class/level in settings current before the post
        _settings.Save();

        object attrs = new
        {
            STR = s.First("strength"), STA = s.First("stamina"), AGI = s.First("agility"),
            DEX = s.First("dexterity"), WIS = s.First("wisdom"), INT = s.First("intelligence"),
            CHA = s.First("charisma"),
        };
        object res = new
        {
            MR = s.First("sv magic"), FR = s.First("sv fire"), CR = s.First("sv cold"),
            DR = s.First("sv disease"), PR = s.First("sv poison"), VR = s.First("sv void"),
        };
        object real = new
        {
            hp = s.First("hp"), hpmax = s.Nth("hp", 1),
            mana = s.First("mana"), manamax = s.Nth("mana", 1),
            end = s.First("end"), endmax = s.Nth("end", 1),
            ac = s.First("ac"),
            attack = s.First("attack"),
            atkspeed = s.First("attack speed"),
            hpregen = s.First("hp regen"), manaregen = s.First("mana regen"), endregen = s.First("end regen"),
            weight = s.First("weight"),
            attrs, res,
            coin = new { plat = s.Plat, gold = s.Gold, silver = s.Silver, copper = s.Copper },
            level = s.Level, classes = s.Classes,
            read_at = s.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        (bool ok, string msg) = await _hub.SendStats(real);
        OcrStatus.Text = ok ? "profile updated ✓" : "send failed: " + Trunc(msg, 60);
        LicLogLine("[ocr] " + msg);

        // Gear rides separately because it is keyed per LOADOUT — each one carries its own full
        // 23-slot set, so equipment stored against the character alone would smear them together.
        if (ok && s.Equipment is { } eq && s.Classes is { Length: > 0 } cls)
        {
            string[] classes = cls.Split('/', StringSplitOptions.RemoveEmptyEntries);
            (bool gok, string gmsg) = await _hub.SendEquipment(
                classes, s.Level ?? _settings.HubLevel, _settings.HubRace,
                eq.Slots.Select(q => (q.Id, q.Name, q.Occupied, q.IconPng, q.IconHash)));
            LicLogLine("[ocr] " + gmsg);
            if (gok) OcrStatus.Text = $"profile + equipment updated ✓ ({eq.OccupiedCount} slots)";
        }

        if (ok) ShowToast("Character sheet sent to profile");
    }

    // ---------------- Settings panel ----------------

    private void InitSettingsTab()
    {
        VarianceBox.Text = ((int)Math.Round(_settings.RandomVariancePercent)).ToString();
        TellPauseBox.Text = _settings.TellPauseMinutes.ToString();
        SettingsTopmostBox.IsChecked = _settings.AlwaysOnTop;
        TooltipOpacitySlider.Value = Math.Clamp(_settings.TooltipOpacity, 0.5, 1.0);
        UpdateTooltipOpacityLabel();
        ApplyTooltipOpacity();

        SyncSwatchUi();
    }

    /// <summary>Which tab of the Settings page is showing. Same shape as the Game Data page's tabs:
    /// one group of radio buttons, one handler, visibility only.</summary>
    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires DURING InitializeComponent for the tab that starts checked, and the view it
        // is about to touch has not been created yet. Guarding on the LAST-declared of the two.
        if (SettingsViewOcr is null) return;
        string tag = (sender as RadioButton)?.Tag as string ?? "General";
        SettingsViewGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        SettingsViewOcr.Visibility = tag == "Ocr" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Ocr") SyncSwatchUi();           // the plan, or the size itself, may have moved since
    }

    // ---------------- OCR & picking ----------------

    /// <summary>Debounce for the size slider. Dragging it fires on every one of up to 120 steps, and
    /// each save rewrites the whole settings file — so the write waits for the drag to stop.</summary>
    private DispatcherTimer? _settingsSaveTimer;

    /// <summary>True while the slider is being SET FROM settings rather than moved by the user, so
    /// the round trip can't be mistaken for a change and written back.</summary>
    private bool _swatchUiLoading;

    private void QueueSettingsSave()
    {
        _settingsSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsSaveTimer.Tick -= SettingsSaveTick;
        _settingsSaveTimer.Tick += SettingsSaveTick;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsSaveTick(object? sender, EventArgs e)
    {
        _settingsSaveTimer?.Stop();
        _settings.Save();
    }

    private void SwatchSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Setting Minimum on a Slider whose Value is still 0 coerces the Value and fires this, which
        // happens during InitializeComponent, before any of the labels exist.
        if (SwatchMismatchText is null || _swatchUiLoading) return;
        int px = Math.Clamp((int)Math.Round(e.NewValue), CompassPickWindow.MinSwatch, CompassPickWindow.MaxSwatch);
        if (px == _settings.IconSwatchPx) { RenderSwatchSetting(); return; }
        _settings.IconSwatchPx = px;
        RenderSwatchSetting();
        QueueSettingsSave();
    }

    /// <summary>
    /// Point the whole OCR tab at whatever the setting says RIGHT NOW.
    ///
    /// This is not just first-run wiring, and that was the bug: the slider is not the only thing
    /// that writes this number. Re-picking an icon and wheeling the square writes it too, from a
    /// different page — so a slider seeded once at startup sits at the old value beside a readout
    /// showing the new one, and the user's first nudge of a thumb they believe is already correct
    /// silently throws the picked size away. Anything that can show this tab re-seeds it.
    /// </summary>
    private void SyncSwatchUi()
    {
        if (SwatchMismatchText is null) return;
        // The ENDS come from the picker's own limits, never from XAML: a range written in two places
        // drifts, and a Minimum above a stored value would rewrite that value just by being shown.
        // Assigning Minimum also COERCES a Value still at its default 0, raising ValueChanged — so
        // the whole seeding runs under the flag, or opening the app would reset a 40 px square to 8.
        _swatchUiLoading = true;
        try
        {
            SwatchSizeSlider.Minimum = CompassPickWindow.MinSwatch;
            SwatchSizeSlider.Maximum = CompassPickWindow.MaxSwatch;
            SwatchSizeSlider.Value = SwatchSize;
        }
        finally { _swatchUiLoading = false; }
        RenderSwatchSetting();
    }

    /// <summary>Everything on the OCR tab that follows from the one number. Nothing here is stored:
    /// the compare count, the stride and the alignment radius are all worked out from the size, so
    /// what the user reads is what the sweep will actually do.</summary>
    private void RenderSwatchSetting()
    {
        if (SwatchMismatchText is null) return;
        int px = SwatchSize;
        SwatchSizeVal.Text = px + " px";

        // Drawn at ONE SCREEN PIXEL PER GAME PIXEL. WPF lays out in device-independent units, so on
        // a 150% display a Width of 40 is 60 real pixels and the square would be a lie — which is
        // the one thing this control exists not to be.
        double scale = 1.0;
        try { scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0; }
        catch { /* not composited yet; 1:1 is the right guess */ }
        if (scale <= 0) scale = 1.0;
        SwatchPreview.Width = SwatchPreview.Height = px / scale;
        SwatchPreviewNote.Text = Math.Abs(scale - 1.0) < 0.001
            ? "Hold this against one inventory slot in the game — it is drawn the same size the bot will use."
            : $"Hold this against one inventory slot in the game. Your display is at {scale * 100:0}%, so this is "
              + $"drawn {px / scale:0.#} units wide to land on {px} real pixels.";

        // Every number here is READ OFF THE SEARCH, not guessed at: the stride is a third of the
        // reference (QuestFind.FindAllIcons), and the radius is SearchPadFor. The cap test is the
        // one the finder itself exposes for this purpose — "pad >= px/2" was wrong twice over, since
        // the geometry only needs half a STEP, and it would have told the user at every selectable
        // size that their search was compromised.
        int pad = QuestFind.SearchPadFor(px);
        bool capped = pad < QuestFind.SearchPadWanted(px);
        SwatchDerived.Text =
            $"A reference picked at this size compares {px * px * 3:n0} numbers per candidate, is stepped "
            + $"{px / 3.0:0.#} px at a time across the bag area, and is searched ±{pad} px around each step for "
            + "the best fit. "
            + (capped
                ? $"⚠ That radius has hit the {QuestFind.SearchPadCap} px cost ceiling, so it no longer covers the "
                + "gap between steps — copies landing in those gaps will be missed. Use a smaller square."
                : $"Half a step is {px / 6.0:0.#} px, so every position between steps is covered.");

        // WHAT NOW DISAGREES. A reference is a picture, not a number — changing the size here cannot
        // resize one that has already been learned, and a reference that no longer matches the size
        // is the exact failure that found 5 of 14 copies. So say it, with the numbers.
        string? gripe = null;
        try
        {
            MergePlan p = MergePlan.Current;
            if (p.IconPixels is { Ok: true } patch && (patch.W != px || patch.H != px))
                gripe = $"Auto Merge's reference was learned at {patch.W} × {patch.H} px and still is — this number "
                      + $"does not resize it. Re-pick the copy's icon on the Auto Merge page to move it to {px} × {px}.";
        }
        catch { /* a settings page must never depend on a plan file loading */ }
        SwatchMismatchText.Text = gripe ?? "";
        SwatchMismatch.Visibility = gripe is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyTooltipOpacity() => Application.Current.Resources["TooltipOpacity"] = _settings.TooltipOpacity;

    private void UpdateTooltipOpacityLabel()
    {
        if (TooltipOpacityVal != null) TooltipOpacityVal.Text = $"{(int)Math.Round(_settings.TooltipOpacity * 100)}%";
    }

    private void TooltipOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.TooltipOpacity = Math.Clamp(e.NewValue, 0.5, 1.0);
        UpdateTooltipOpacityLabel();
        ApplyTooltipOpacity();
    }

    private void SettingsTopmost_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = SettingsTopmostBox.IsChecked == true;
        if (TopmostBox != null) TopmostBox.IsChecked = _settings.AlwaysOnTop;   // keep the Command Center toggle in sync
        _settings.Save();
        UpdateTopmost();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(VarianceBox.Text.Trim(), out double v)) _settings.RandomVariancePercent = Math.Clamp(v, 0, 60);
        if (int.TryParse(TellPauseBox.Text.Trim(), out int tp)) _settings.TellPauseMinutes = Math.Clamp(tp, 0, 120);
        _settings.TooltipOpacity = Math.Clamp(TooltipOpacitySlider.Value, 0.5, 1.0);
        _settings.AlwaysOnTop = SettingsTopmostBox.IsChecked == true;
        _settings.Save();
        ApplyTooltipOpacity();
        ShowToast("Settings saved");
    }

    private void StopGrind_Click(object sender, RoutedEventArgs e)
    {
        _grind?.Stop();
        _hunt?.Stop();
        _questRun?.Stop();          // F12 reaches every role that can send input, not just the grind ones
        _questStartCancelled = true;   // ...including one that has been ordered but isn't built yet
        _mergeRun?.Stop();
        _grindTimer.Stop();
        UpdateGrindStats();
    }

    /// <summary>The big colored ACTIVE/STOPPED banner at the top of the Grind panel.</summary>
    private void UpdateGrindBanner()
    {
        if (_hunt is { Running: true })
        {
            string st = _hunt.Stats.State;
            bool paused = st.Contains("paused", StringComparison.OrdinalIgnoreCase);
            SetGrindBanner(paused ? 1 : 2, paused ? $"PAUSED — {st}" : $"HUNTING — {st}");
        }
        else if (_grind is { Running: true })
        {
            bool paused = _grind.Stats.Paused;
            SetGrindBanner(paused ? 1 : 2, paused ? "PAUSED — EQ not focused" : "RUNNING — rotation");
        }
        else SetGrindBanner(0, "STOPPED — press Start grind");
        PushTetherToMaps();                                  // circle tracks role state (cheap no-op when unchanged)
    }

    /// <summary>kind: 0 = stopped (gray), 1 = paused (amber), 2 = active (green).</summary>
    private void SetGrindBanner(int kind, string text)
    {
        if (GrindBanner is null) return;
        GrindBannerText.Text = text;
        (string bg, string bd, string dot, string fg) = kind switch
        {
            2 => ("#12261B", "#2C8C55", "#7CE38B", "#B6F2C9"),
            1 => ("#2A2410", "#7A6320", "#FFCB6B", "#FFE1A6"),
            _ => ("#20303F", "#2A4A57", "#5D6878", "#C6D2DE"),
        };
        GrindBanner.Background = Hex(bg);
        GrindBanner.BorderBrush = Hex(bd);
        GrindBannerDot.Fill = Hex(dot);
        GrindBannerText.Foreground = Hex(fg);
    }

    private void UpdateGrindStats()
    {
        UpdateGrindBanner();
        if (_hunt is { Running: true })
        {
            HuntStats h = _hunt.Stats;
            GrindStatsLabel.Text = $"HUNT [{h.State}] — kills {h.Kills} · fights {h.Fights} · considered {h.MobsConsidered} · skipped {h.Skipped}";
            UpdateLicSessionLabel();
            return;
        }
        if (_grind is null) { GrindStatsLabel.Text = "idle — keys 0 · kills 0 · xp 0 · loops 0"; return; }
        GrindStats s = _grind.Stats;
        string state = !_grind.Running ? "stopped" : (s.Paused ? "PAUSED (game not focused)" : "running");
        GrindStatsLabel.Text = $"{state} — keys {s.KeysSent} · kills {s.Kills} · xp {s.XpGains} · loops {s.Loops}";
        UpdateLicSessionLabel();
    }

    /// <summary>
    /// The GRIND console — and only the grind's own work.
    ///
    /// Quest and merge lines used to be funnelled in here with a "[quest]"/"[merge]" prefix, which
    /// made this box useless for reading a grind and made those runs impossible to follow. Each
    /// role now records under its own name in <see cref="ActivityLog"/>; this console reads back
    /// one source, and the Activity Console page reads them all.
    /// </summary>
    private void GrindLogLine(string msg)
    {
        ActivityLog.Record(_hunt is { Running: true } ? "Hunt" : "Grind", msg);
        GrindLog.AppendText(msg + Environment.NewLine);
        GrindLog.ScrollToEnd();
    }

    // ---------------- Tab 5: auto-login ----------------

    private void StartLogin_Click(object sender, RoutedEventArgs e) => BeginLaunch(startLauncher: false);

    private void StopLogin_Click(object sender, RoutedEventArgs e)
    {
        _login?.Stop();
        LaunchStatus.Text = "Launch stopped.";
    }

    /// <summary>Command Center one-click Launch: start the launcher (if set), then auto-login. Stays on
    /// Command Center; every step streams into the Login Console.</summary>
    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        string path = LauncherPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var res = MessageBox.Show(
                "Before I can launch the game, I need to know where your EverQuest Legends launcher is " +
                "(usually LaunchPad.exe).\n\nPick it now?",
                "Set your launcher", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (res != MessageBoxResult.OK) { LaunchStatus.Text = "Launch cancelled — set the launcher path to enable it."; return; }
            var dlg = new OpenFileDialog { Title = "Pick the EQL launcher (LaunchPad.exe)", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            LauncherPathBox.Text = dlg.FileName;
            _settings.LauncherPath = dlg.FileName;
            _settings.Save();
            ShowToast("Launcher path saved");
        }
        BeginLaunch(startLauncher: true);
        LaunchStatus.Text = "Launching… watch the Login Console for each step.";
    }

    /// <summary>Guards BeginLaunch against itself: a double-click on Launch used to stop the
    /// healthy launch the first click had just started — and could start a SECOND LaunchPad
    /// process, because the fresh AutoLogin had no idea the first one had already run the exe.</summary>
    private bool _launchPending;

    private async void BeginLaunch(bool startLauncher)
    {
        if (_launchPending) { LoginLogLine("Launch already starting."); return; }
        _launchPending = true;
        try
        {
        _settings.LauncherPath = LauncherPathBox.Text.Trim();
        if (_login is { } old && old.Running)
        {
            // "Running" can be the last couple of seconds of a loop that is already unwinding —
            // its screen read takes no cancellation token, so a Stop lands only at the next await.
            // Refusing here made "press Stop, press Launch" fail with "already running", which is
            // the exact lockout this launch path was just cured of. Stop it and wait it out.
            // The wait watches OLD, the object it stopped — the field, were it reassigned under
            // this await, would be someone else's healthy launch reading as "didn't stop".
            old.Stop();
            LoginLogLine("A previous launch was still winding down — stopped it; starting fresh…");
            for (int i = 0; i < 40 && old.Running; i++) await Task.Delay(150);
            if (old.Running) { LoginLogLine("The previous launch didn't stop — try Launch again."); return; }
        }
        _login = new AutoLogin(LoginServerBox.Text, _settings) { LauncherPath = startLauncher ? _settings.LauncherPath : "" };
        _login.Log += m => Dispatcher.Invoke(() => { LoginLogLine(m); LaunchStatus.Text = m; });
        _login.Done += () => Dispatcher.Invoke(() => { LoginLogLine("Reached the game. Launch complete."); LaunchStatus.Text = "In the game. ▶"; });
        _login.Start();
        LoginLogLine(startLauncher ? "Launch requested from Command Center." : "Auto-login started.");
        }
        finally { _launchPending = false; }
    }

    private void PickLauncher_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Pick the EQL launcher (LaunchPad) exe", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            LauncherPathBox.Text = dlg.FileName;
            _settings.LauncherPath = dlg.FileName;
            _settings.Save();
            ShowToast("Launcher path saved");
        }
    }

    // ---------------- Auto-updater (GitHub Releases) — ambient chip, no popups ----------------
    // Modeled on EQ Legends Companion's UpdateChip: an update is a reward, not a nag. One calm
    // resting line ("v0.9.5 · checked 2m ago", click to check), a hairline bar while downloading,
    // and exactly one loud state — a gold "Restart to update" that glows once, then rests. Errors
    // fall back to the quiet line; a failed check is not the user's problem.

    private static readonly Color UpdGold = Color.FromRgb(0xD9, 0xB2, 0x5F);
    private static readonly Brush UpdGoldBrush = new SolidColorBrush(UpdGold);
    private DateTime? _updCheckedAt;
    private string? _updStagedDir;          // non-null once a build is downloaded & staged (ready state)
    private bool _updBusy;                   // a check/download is in flight
    private readonly DispatcherTimer _updAgeTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _updPeriodicTimer = new() { Interval = TimeSpan.FromHours(4) };

    private void InitUpdater()
    {
        SetUpdaterQuiet();
        _updAgeTimer.Tick += (_, _) => { if (_updStagedDir == null && !_updBusy) SetUpdaterQuiet(); };
        _updAgeTimer.Start();
        _updPeriodicTimer.Tick += async (_, _) => await RunUpdateCheck(false);
        _updPeriodicTimer.Start();
        _ = FirstUpdateCheckAsync();          // one quiet check shortly after launch
    }

    private async Task FirstUpdateCheckAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20)); } catch { /* ignore */ }
        await RunUpdateCheck(false);
    }

    /// <summary>Nav chip + Settings button share one action: install if a build is staged, else check.</summary>
    private void UpdaterChip_Click(object sender, MouseButtonEventArgs e) => TriggerUpdateAction();
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => TriggerUpdateAction();

    private void TriggerUpdateAction()
    {
        if (_updStagedDir != null)
        {
            try { Updater.ApplyAndRestart(_updStagedDir); Application.Current.Shutdown(); }
            catch (Exception ex) { SetUpdaterQuiet(tip: "Couldn't start the update — " + Trunc(ex.Message, 80)); }
            return;
        }
        if (!_updBusy) _ = RunUpdateCheck(manual: true);
    }

    private async Task RunUpdateCheck(bool manual)
    {
        if (_updBusy || _updStagedDir != null) return;
        _updBusy = true;
        try
        {
            SetUpdaterChecking();
            UpdateInfo info = await Updater.CheckAsync();
            _updCheckedAt = DateTime.Now;
            if (info.Error != null)
            {
                SetUpdaterQuiet(tip: "Last check didn't complete — " + Trunc(info.Error, 80) + ". Click to try again.");
                if (manual) ShowToast("Update check failed");
                return;
            }
            if (!info.Available)
            {
                SetUpdaterQuiet($"v{info.CurrentVersion} · up to date");
                if (manual) ShowToast("You're up to date");
                return;
            }
            var progress = new Progress<double>(p => SetUpdaterDownloading((int)Math.Round(p)));
            SetUpdaterDownloading(0);
            string dir = await Updater.DownloadAndStageAsync(info, progress);
            SetUpdaterReady(info.LatestVersion, dir, glow: true);
            if (manual) ShowToast($"v{info.LatestVersion} ready — click Restart to update");
        }
        catch (Exception ex) { SetUpdaterQuiet(tip: "Update error — " + Trunc(ex.Message, 80)); }
        finally { _updBusy = false; }
    }

    // ---- chip state rendering ----

    private void SetUpdaterQuiet(string? overrideText = null, string? tip = null)
    {
        _updStagedDir = null;
        UpdaterChip.Effect = null;
        UpdaterChip.Background = Brushes.Transparent;
        UpdaterChip.BorderBrush = Brushes.Transparent;
        UpdaterChipIcon.Visibility = Visibility.Collapsed;
        UpdaterChipSub.Visibility = Visibility.Collapsed;
        UpdaterChipBar.Visibility = Visibility.Collapsed;
        UpdaterChipText.Foreground = (Brush)FindResource("TextDim");
        UpdaterChipText.FontWeight = FontWeights.Normal;
        string v = "v" + AppSettings.AppVersion;
        UpdaterChipText.Text = overrideText
            ?? (_updCheckedAt is DateTime t ? $"{v} · checked {FormatAge(t)}" : $"{v} · not checked yet");
        UpdaterChip.ToolTip = tip ?? "Click to check for updates";
    }

    private void SetUpdaterChecking()
    {
        UpdaterChip.Effect = null;
        UpdaterChip.Background = Brushes.Transparent;
        UpdaterChip.BorderBrush = Brushes.Transparent;
        UpdaterChipIcon.Visibility = Visibility.Collapsed;
        UpdaterChipSub.Visibility = Visibility.Collapsed;
        UpdaterChipBar.Visibility = Visibility.Collapsed;
        UpdaterChipText.Foreground = (Brush)FindResource("TextDim");
        UpdaterChipText.FontWeight = FontWeights.Normal;
        UpdaterChipText.Text = "Checking for updates…";
        UpdaterChip.ToolTip = "Checking for updates…";
    }

    private void SetUpdaterDownloading(int pct)
    {
        UpdaterChip.Effect = null;
        UpdaterChip.Background = Brushes.Transparent;
        UpdaterChip.BorderBrush = Brushes.Transparent;
        UpdaterChipIcon.Visibility = Visibility.Collapsed;
        UpdaterChipSub.Visibility = Visibility.Collapsed;
        UpdaterChipText.Foreground = (Brush)FindResource("TextDim");
        UpdaterChipText.FontWeight = FontWeights.Normal;
        UpdaterChipText.Text = $"Downloading update · {pct}%";
        UpdaterChipBar.Visibility = Visibility.Visible;
        UpdaterChipBar.Value = pct;
        UpdaterChip.ToolTip = "Downloading the new version…";
    }

    private void SetUpdaterReady(string ver, string dir, bool glow)
    {
        _updStagedDir = dir;
        UpdaterChipBar.Visibility = Visibility.Collapsed;
        UpdaterChipIcon.Visibility = Visibility.Visible;
        UpdaterChipText.Text = "Restart to update";
        UpdaterChipText.Foreground = UpdGoldBrush;
        UpdaterChipText.FontWeight = FontWeights.SemiBold;
        UpdaterChipSub.Text = "v" + ver;
        UpdaterChipSub.Visibility = Visibility.Visible;
        UpdaterChip.Background = new SolidColorBrush(Color.FromArgb(0x1A, UpdGold.R, UpdGold.G, UpdGold.B));
        UpdaterChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x8C, UpdGold.R, UpdGold.G, UpdGold.B));
        UpdaterChip.ToolTip = $"Restart to update to v{ver}";
        if (glow)
        {
            var eff = new DropShadowEffect { Color = UpdGold, ShadowDepth = 0, BlurRadius = 0, Opacity = 0.85 };
            UpdaterChip.Effect = eff;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 13,
                Duration = new Duration(TimeSpan.FromSeconds(1.5)),
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
            };
            anim.Completed += (_, _) => { if (UpdaterChip.Effect is DropShadowEffect d) d.BlurRadius = 0; };
            eff.BeginAnimation(DropShadowEffect.BlurRadiusProperty, anim);
        }
    }

    private static string FormatAge(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

    private void LoadMascot()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri("pack://application:,,,/assets/mascot.jpg", UriKind.Absolute);
            bmp.EndInit();
            MascotImg.Source = bmp;
        }
        catch { /* no bundled mascot — the glow shows instead */ }
    }

    private void LoginLogLine(string msg)
    {
        LoginLog.AppendText(msg + Environment.NewLine);
        LoginLog.ScrollToEnd();
    }

    // ---------------- Tab 6: humanized mouse ----------------

    private void ApplyMouse_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(MouseSpeedBox.Text.Trim(), out double sp)) _settings.MouseSpeedPxPerSec = Math.Clamp(sp, 120, 4000);
        if (double.TryParse(MouseArcBox.Text.Trim(), out double arc)) _settings.MouseArc = Math.Clamp(arc, 0, 0.6);
        if (double.TryParse(MouseAngleBox.Text.Trim(), out double ang)) _settings.MouseAngleJitterDegrees = Math.Clamp(ang, 0, 45);
        _settings.Save();
        MouseLogLine($"Applied: speed {_settings.MouseSpeedPxPerSec:0} px/s, arc {_settings.MouseArc:0.00}, angle jitter {_settings.MouseAngleJitterDegrees:0}°.");
        ShowToast("Mouse settings saved");
    }

    private void StartMouseDemo_Click(object sender, RoutedEventArgs e)
    {
        if (_mouseCts is { IsCancellationRequested: false }) { MouseLogLine("Demo already running."); return; }
        _mouseCts = new CancellationTokenSource();
        CancellationToken ct = _mouseCts.Token;
        MouseLogLine("Fluid demo started. STOP it with: Esc, F12, or fling the cursor into any screen corner. Auto-stops after 30s.");

        // Watchdog: because the demo owns the cursor, give reliable escape hatches that don't
        // need you to click the (moving) Stop button — Esc/F12 anytime, or a corner during a pause.
        DateTime started = DateTime.Now;
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool esc = (GetAsyncKeyState(0x1B) & 0x8000) != 0;   // ESC
                    bool f12 = (GetAsyncKeyState(0x7B) & 0x8000) != 0;   // F12
                    var (cx, cy) = HumanizedMouse.CursorPos();
                    var (vx, vy, vw, vh) = HumanizedMouse.VirtualScreen();
                    bool corner = (cx <= vx + 3 || cx >= vx + vw - 4) && (cy <= vy + 3 || cy >= vy + vh - 4);
                    if (esc || f12 || corner || (DateTime.Now - started).TotalSeconds > 30)
                    {
                        Dispatcher.Invoke(() => MouseLogLine(esc ? "Esc — stopping." : f12 ? "F12 — stopping." : corner ? "Corner — stopping." : "30s limit — stopping."));
                        _mouseCts?.Cancel();
                        break;
                    }
                    await Task.Delay(40, ct);
                }
            }
            catch (OperationCanceledException) { }
        });

        Task.Run(async () =>
        {
            try
            {
                var (vx, vy, vw, vh) = HumanizedMouse.VirtualScreen();
                while (!ct.IsCancellationRequested)
                {
                    double tx = vx + 40 + _mouseRng.NextDouble() * Math.Max(1, vw - 80);
                    double ty = vy + 40 + _mouseRng.NextDouble() * Math.Max(1, vh - 80);
                    await HumanizedMouse.MoveTo(tx, ty, _settings, _mouseRng, ct);
                    await Task.Delay(_settings.Vary(500, _mouseRng), ct);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopMouseDemo_Click(object sender, RoutedEventArgs e) => StopMouseDemo();

    private void StopMouseDemo()
    {
        _mouseCts?.Cancel();
        MouseLogLine("Mouse demo stopped.");
    }

    private void MouseLogLine(string msg)
    {
        MouseLog.AppendText(msg + Environment.NewLine);
        MouseLog.ScrollToEnd();
    }

    // ---------------- Tab 8: licensing / hub ----------------

    private void InitLicensingTab()
    {
        LicUserBox.Text = _settings.HubUsername;
        LicUrlBox.Text = _settings.HubUrl;
        LicKeyBox.Text = _settings.HubApiKey;
        LicIntervalBox.Text = _settings.HubCheckInSeconds.ToString();
        LicMachineLabel.Text = _hub.Machine;
        LicClassCombo.ItemsSource = EqClasses;
        LicRaceCombo.ItemsSource = EqRaces;
        if (!string.IsNullOrWhiteSpace(_settings.HubClass)) LicClassCombo.SelectedItem = _settings.HubClass;
        LicRaceCombo.SelectedItem = string.IsNullOrWhiteSpace(_settings.HubRace) ? "Human" : _settings.HubRace;
        LicLevelBox.Text = Math.Max(1, _settings.HubLevel).ToString();
        LicServerBox.Text = string.IsNullOrWhiteSpace(_settings.HubServer) ? "Rivervale" : _settings.HubServer;
        LicAutoBox.IsChecked = _settings.HubAutoCheckIn;
        UpdateLicSessionLabel();
    }

    /// <summary>If the newest log names a character (eqlog_Name_server.txt), fill it in when blank.</summary>
    private void TryAutoFillCharacter()
    {
        if (EqLogWatcher.CharacterFromLog(_currentLog) is not { } who) return;
        if (string.IsNullOrWhiteSpace(LicUserBox.Text))
        {
            LicUserBox.Text = who.name;
            _settings.HubUsername = who.name;
            if (!string.IsNullOrWhiteSpace(who.server)) { LicServerBox.Text = who.server; _settings.HubServer = who.server; }
            LicLogLine($"Character detected from log: {who.name}" + (who.server.Length > 0 ? $" · {who.server}" : ""));
            UpdateChip();
        }
    }

    /// <summary>What this install would report right now: role + cumulative counters.</summary>
    private (string role, int actions, int kills, int xp) HubStats()
    {
        if (_hunt is { Running: true })
        {
            HuntStats h = _hunt.Stats;
            return ("Hunt", h.Fights, h.Kills, 0);
        }
        if (_grind is { Running: true })
        {
            GrindStats s = _grind.Stats;
            return ("Grind", s.KeysSent, s.Kills, s.XpGains);
        }
        return ("Idle", 0, 0, 0);
    }

    private void UpdateLicSessionLabel()
    {
        if (LicSessionText is null) return;   // before the tab is loaded
        var (role, a, k, x) = HubStats();
        LicSessionText.Text = $"reporting: role {role} · actions {a} · kills {k} · xp {x}";
    }

    private void ApplyLicensingFields()
    {
        _settings.HubUsername = LicUserBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(LicUrlBox.Text)) _settings.HubUrl = LicUrlBox.Text.Trim();
        _settings.HubApiKey = LicKeyBox.Text.Trim();
        if (int.TryParse(LicIntervalBox.Text.Trim(), out int iv))
            _settings.HubCheckInSeconds = Math.Clamp(iv, 15, 3600);
        _settings.HubClass = LicClassCombo.SelectedItem as string ?? _settings.HubClass;
        _settings.HubRace = LicRaceCombo.SelectedItem as string ?? _settings.HubRace;
        if (int.TryParse(LicLevelBox.Text.Trim(), out int lv)) _settings.HubLevel = Math.Clamp(lv, 1, 120);
        _settings.HubMaxLevel = Math.Max(_settings.HubMaxLevel, _settings.HubLevel);
        if (!string.IsNullOrWhiteSpace(LicServerBox.Text)) _settings.HubServer = LicServerBox.Text.Trim();
    }

    private async Task DoCheckIn(bool manual)
    {
        ApplyLicensingFields();
        if (string.IsNullOrWhiteSpace(_settings.HubUsername))
        {
            LicStatusText.Text = "Enter a character/account name to check in as.";
            if (manual) LicLogLine("No name set — nothing sent.");
            return;
        }
        UpdateLicSessionLabel();
        var (role, actions, kills, xp) = HubStats();
        HubResponse r = await _hub.CheckIn(role, actions, kills, xp);
        RenderHubResponse(r);
    }

    private void RenderHubResponse(HubResponse r)
    {
        if (!r.NetworkOk)
        {
            SetTierBadge(null);
            LicStatusText.Text = "Couldn't reach the hub — " + (r.Error ?? "network error") +
                                 ".  (The hub is IP-restricted to the GCI network.)";
            LicLogLine("× check-in failed: " + (r.Error ?? "network"));
            return;
        }
        if (!r.Authorized)
        {
            SetTierBadge(null);
            LicStatusText.Text = "Not authorized — " + (r.Message ?? "check the API key.");
            LicLogLine("× unauthorized: " + (r.Message ?? ""));
            return;
        }
        SetTierBadge(r.Tier);
        LicStatusText.Text = $"Online as {_settings.HubUsername}  ·  {r.Message}";
        LicRolesText.Text = "unlocked roles: " + r.RolesText;
        LicLastText.Text = $"last check-in: {r.When:g}  ·  next in ~{r.Interval}s";
        LicLogLine($"✓ {r.Tier} — {r.RolesText}");
        UpdateChip();
        _ = RefreshConnections();   // keep the "last 10 connections" card current
    }

    private void SetTierBadge(string? tier)
    {
        LicTierText.Text = tier ?? "not checked in";
        if (tier is null)
        {
            LicTierBadge.Background = Hex("#20303F");
            LicTierText.Foreground = Hex("#E6EDF3");
            return;
        }
        if (tier == "Plaid")   // the top tier gets the plaid gradient, matching the dashboard
        {
            var g = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x79, 0xF9), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x4F, 0xC3, 0xF7), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x7C, 0xE3, 0x8B), 1));
            LicTierBadge.Background = g;
            LicTierText.Foreground = Hex("#0B0F16");
            return;
        }
        string col = tier switch { "Hyper" => "#7CE38B", "Ludicrous" => "#FFB74D", _ => "#4FC3F7" };
        LicTierBadge.Background = Hex(col);
        LicTierText.Foreground = Hex("#0B0F16");
    }

    private async void CheckInNow_Click(object sender, RoutedEventArgs e)
    {
        LicLogLine("checking in…");
        await DoCheckIn(true);
    }

    private void SaveLicensing_Click(object sender, RoutedEventArgs e)
    {
        ApplyLicensingFields();
        _settings.Save();
        LicMachineLabel.Text = _hub.Machine;
        LicLogLine("Saved. This install now checks in as " +
                   (string.IsNullOrWhiteSpace(_settings.HubUsername) ? "(no name yet)" : _settings.HubUsername) + ".");
        ShowToast("Account settings saved");
    }

    private void LicAuto_Click(object sender, RoutedEventArgs e)
    {
        _settings.HubAutoCheckIn = LicAutoBox.IsChecked == true;
        if (_settings.HubAutoCheckIn)
        {
            ApplyLicensingFields();
            _hubTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.HubCheckInSeconds, 15, 3600));
            _hubTimer.Start();
            LicLogLine($"Auto check-in on — every {_settings.HubCheckInSeconds}s (persists across restarts).");
            _ = DoCheckIn(false);
        }
        else
        {
            _hubTimer.Stop();
            LicLogLine("Auto check-in off.");
        }
        _settings.Save();
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        string url = _settings.HubUrl;
        int i = url.IndexOf("api.php", StringComparison.OrdinalIgnoreCase);
        string dash = i >= 0 ? url.Substring(0, i) : url;
        try { Process.Start(new ProcessStartInfo(dash) { UseShellExecute = true }); }
        catch (Exception ex) { LicLogLine("Couldn't open browser: " + ex.Message); }
    }

    private void LicLogLine(string msg)
    {
        Diag.BotLog.Log("hub", msg);
        LicLog.AppendText(msg + Environment.NewLine);
        LicLog.ScrollToEnd();
    }

    // ---------------- Launch method ----------------

    private void OnLoadedInit()
    {
        InitElevationBanner();
        InitLicensingTab();
        InitGrindTab();
        InitFollowerTab();
        InitMapsTab();
        InitCombat();
        InitSettingsTab();
        UpdateLaunchLabel();
        VersionRun.Text = "v" + AppSettings.AppVersion;
        LoadMascot();
        // The game may already be RUNNING — the app restarts far more often than the game does
        // (updates alone restart it), and until now nothing looked for an existing window until
        // the user visited a page that happened to need one. Look right away, and say so.
        AutoTargetEq();
        if (_grindTarget != IntPtr.Zero)
        {
            LaunchStatus.Text = "Game detected — already running.";
            LoginLogLine("Found the game already running — connected to it.");
        }
        _ready = true;
        UpdateChip();
        RefreshHome();
        InitUpdater();   // ambient update chip + background check (no popups)

        // Detect the character from the newest log, then check in on launch so the dashboard
        // shows you online immediately — and resume auto check-in if it was left on.
        try { _currentLog ??= EqLogWatcher.FindNewestLog(LogFolderBox.Text.Trim()); TryAutoFillCharacter(); } catch { }
        if (!string.IsNullOrWhiteSpace(_settings.HubUsername))
        {
            _ = DoCheckIn(false);
            if (_settings.HubAutoCheckIn)
            {
                _hubTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.HubCheckInSeconds, 15, 3600));
                _hubTimer.Start();
            }
        }
        // First launch: no method chosen yet → show the picker, but let the splash finish first.
        if (string.IsNullOrEmpty(_settings.LaunchMethod))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            t.Tick += (s, e) => { t.Stop(); OpenLaunchPicker(); };
            t.Start();
        }
    }

    private void LaunchMethod_Click(object sender, RoutedEventArgs e) => OpenLaunchPicker();

    private void OpenLaunchPicker()
    {
        var picker = new LaunchMethodPicker(_settings.LaunchMethod) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedId != null)
        {
            _settings.LaunchMethod = picker.SelectedId;
            _settings.Save();
            UpdateLaunchLabel();
        }
    }

    private void UpdateLaunchLabel()
    {
        var m = LaunchMethods.ById(_settings.LaunchMethod);
        LaunchMethodLabel.Text = m is null ? "method: not set" : $"method: {m.Title}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        { _settings.WindowWidth = ActualWidth; _settings.WindowHeight = ActualHeight; }
        _settings.Save();
        _fgTimer.Stop();
        _grindTimer.Stop();
        _followerTimer.Stop();
        _combatTimer.Stop();
        _hubTimer.Stop();
        _remote?.Stop();
        _grind?.Stop();
        _hunt?.Stop();
        _questRun?.Stop();
        _mergeRun?.Stop();
        _follower?.Stop();
        EndRoleSession();                 // persist a session cut short by closing the app
        _login?.Stop();
        _mouseCts?.Cancel();
        _mapsWatcher?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, PANIC_HOTKEY_ID);
            UnregisterHotKey(_hwnd, PROBE_HOTKEY_ID);
            UnregisterHotKey(_hwnd, MERGE_HOTKEY_ID);
        }
        Input.PanicKey.Uninstall();
        StopWatch();
        ActivityLog.Added -= OnActivityAdded;      // static event: leaving this attached leaks the window
        _overlay?.Close();
        base.OnClosed(e);
    }
}
