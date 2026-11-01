using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Alerts;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.Theming;
using ConnectionClue.Presentation.Updates;
using ConnectionClue.Presentation.ViewModels;
using ConnectionClue.Windows;
using ConnectionClue.Windows.Probes;
using System.Windows.Threading;

namespace ConnectionClue.App;

/// <summary>Composition root and window/tray lifecycle. The window exists only while it is open: in the notification area the
/// app holds no window, so it keeps no visual tree, no render surface and no bindings.</summary>
public partial class App : Application
{
    // After a check ends with no window open, memory is given back at most this often.
    private static readonly TimeSpan TrimEvery = TimeSpan.FromSeconds(30);
    private MainViewModel _vm = null!;
    private MainWindow? _window;
    private TrayIcon _tray = null!;
    private SingleInstanceGate? _singleInstance;
    private CultureInfo _ui = CultureInfo.InvariantCulture;
    private string? _language;
    private bool _exiting, _trayHintShown, _offline, _suppressStartupChange, _started;
    private int _connectivityRefreshQueued;
    private long? _trimmedAt;
    private bool _windowReleased;
    private readonly NotificationPolicy _notifications = new();
    private WindowState _restoreState = WindowState.Maximized;
    private Rect _restoreBounds = Rect.Empty;

    public App()
    {
        // Set before InitializeComponent loads resources or any window can render. The app never uses the graphics card.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // A tray app runs for days: a stray exception on the UI thread must not end it or raise Windows' crash dialog. One
        // thrown before start-up finished would leave a process with no icon and no way out (that still holds the instance
        // lock), so it ends the app instead.
        DispatcherUnhandledException += (_, a) =>
        {
            if (a.Exception is OutOfMemoryException) return;
            a.Handled = true;
            if (!_started) Shutdown(1);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A visual check (--snapshot) only renders the window to a file and exits: it never hands off to a running copy.
        bool snapshotRun = Arg(e.Args, "--snapshot") is not null;
        // Started by Windows at sign-in: the registry entry passes --startup, an installed package starts through its startup task.
        bool atSignIn = !snapshotRun && (e.Args.Contains("--startup") || StartupManager.LaunchedByStartupTask());
        if (!snapshotRun)
        {
            _singleInstance = SingleInstanceGate.Acquire(e.Args.Contains("--start"), out bool activatedExisting);
            if (_singleInstance is null)
            {
                // A copy started at sign-in must never put a message in front of the user.
                if (!activatedExisting && !atSignIn)
                    MessageBox.Show("ConnectionClue is already running, but Windows could not activate its window. Use its notification-area icon to open it.",
                        "ConnectionClue", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        _offline = e.Args.Contains("--offline");
        var saved = SettingsStore.Load();
        _language = saved.Language;
        _trayHintShown = saved.TrayHintShown;
        // Theme before any window exists, so nothing flashes in the wrong colours (--theme for testing).
        var theme = Enum.TryParse<AppTheme>(Arg(e.Args, "--theme"), ignoreCase: true, out var forced) ? forced : saved.Theme;
        ThemeManager.Apply(this, theme);
        // English unless the user picked another language (saved setting, or --lang for testing).
        var ui = LanguageResolver.Resolve([], Arg(e.Args, "--lang") ?? saved.Language);
        _ui = ui;
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = ui;
        var l = Localizer.Default;

        var settings = new SettingsViewModel(l, ui, saved.BackgroundEnabled, saved.IntervalMinutes,
            saved.DelayLimitMs, saved.LossLimitPercent, saved.VariationLimitMs, saved.CheckSeconds, saved.MeasureSpeed, saved.AiReview, theme,
            saved.StartWithWindows, saved.PlanDownloadMbps, saved.PlanUploadMbps, saved.GamingTarget, saved.VideoTarget,
            saved.CallsTarget, saved.DisconnectTarget, saved.LongCaptureMinutes, saved.BackgroundOnMobileEnabled, saved.BackgroundCheckSeconds)
        {
            WindowsHighContrast = ThemeManager.WindowsHighContrast,
        };
        // New install, or settings from an older version: move values still at an old default to the new one (a later
        // choice of the user, such as a cleared target, is kept).
        if (saved.SettingsVersion < AppSettings.CurrentVersion) settings.Upgrade(saved.SettingsVersion);
        settings.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SettingsViewModel.Theme)) ThemeManager.Apply(this, settings.Theme);
            if (a.PropertyName == nameof(SettingsViewModel.StartWithWindows) && !_suppressStartupChange)
                _ = ApplyStartupSettingAsync(settings);
            if (a.PropertyName == nameof(SettingsViewModel.BackgroundEnabled) && !settings.BackgroundEnabled && settings.StartWithWindows)
                settings.StartWithWindows = false;
        };
        _ = SyncStartupStateAsync(settings);
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName != nameof(SystemParameters.HighContrast)) return;
            settings.WindowsHighContrast = ThemeManager.WindowsHighContrast;
            ThemeManager.Apply(this, settings.Theme); // Windows high contrast on or off: follow it at once
        };
        var reviewer = new OnlineAdviceReviewer(ReviewHttp, Reviewers.Load(), new ReviewCacheStore(), TimeProvider.System);
        var updateChecker = new ReleaseUpdateChecker(UpdateHttp, typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0));
        _vm = new MainViewModel(l, ui, TimeProvider.System, StartSessionAsync, new AccessibilityPreferences(), settings, new ResultStore(), new Shell(),
            IsConnected, reviewer, new CheckHistoryStore(), updateChecker, StartupManager.IsStoreManaged(), new TcpServiceTargetProbe(),
            ConnectionCost.IsMobileNetwork, new NetworkDiagnostics(), new SupportReportExporter(), new ResultsExporter(() => _window?.ResultVisuals ?? []));
        _tray = new TrayIcon(l.Get("Tray_Open"), l.Get("Action_QuickCheck"), l.Get("Background_Enable"), l.Get("Tray_Exit"),
            ui.TextInfo.IsRightToLeft);

        settings.PropertyChanged += (_, _) => { Persist(); RefreshTray(); };
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.CheckFinished += (_, _) => TrimMemory();
        _vm.AlertRaised += (_, a) =>
        {
            if (!a.Automatic && _window?.IsActive == true) return; // The user is watching the result of their own check.
            Notify(a.Title, a.Body, a.Warning);
        };
        _vm.LanguageChangeRequested += (_, name) =>
        {
            _language = name;
            Persist();
            System.Diagnostics.Process.Start(Environment.ProcessPath!);
            ExitApp();
        };

        _tray.Open += (_, _) => ShowWindow();
        _tray.CheckNow += (_, _) => _vm.QuickCheckCommand.Execute(null);
        _tray.ToggleBackground += (_, _) => settings.BackgroundEnabled = !settings.BackgroundEnabled;
        _tray.Exit += (_, _) => ExitApp();

        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested += (_, quickCheck) => Dispatcher.BeginInvoke(() =>
            {
                ShowWindow();
                if (quickCheck && _vm.QuickCheckCommand.CanExecute(null)) _vm.QuickCheckCommand.Execute(null);
            });
            _singleInstance.Listen();
        }
        // Started by Windows at sign-in with background checks on: no window is created at all, only the tray icon. (With
        // background checks on, minimize and close later release the window the same way; Exit in the tray menu quits.)
        bool startHidden = atSignIn && settings.BackgroundEnabled;
        if (startHidden)
        {
            _vm.SetWindowVisible(false); // no window will show what a check does
            ShowTrayHintOnce();
            if (!ConnectionCost.IsMobileNetwork() || settings.BackgroundOnMobileEnabled)
                _ = _vm.RunCheckAsync(measureSpeed: settings.MeasureSpeed);
        }
        else
        {
            OpenWindow();
        }
        SetJumpList();
        RefreshTray();
        // Live connectivity: with no network at all the status and tray warn, and no check starts.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => QueueConnectivityRefresh();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => QueueConnectivityRefresh();
        global::Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ => QueueConnectivityRefresh();
        RefreshConnectivity();
        if (e.Args.Contains("--start")) _vm.QuickCheckCommand.Execute(null);
        if (Enum.TryParse<AppPage>(Arg(e.Args, "--page"), ignoreCase: true, out var page)) _vm.Page = page; // test hook
        _started = true;
        if (Arg(e.Args, "--snapshot") is { } snapshot) SnapshotWhenSettled(snapshot, Arg(e.Args, "--size"), e.Args.Contains("--start"));
    }

    /// <summary>
    /// Test hook for visual checks without a visible desktop (CI agents, a locked PC): --snapshot out.png [--size 1240x768]
    /// [--start] renders the window with WPF's software renderer once it settles (after the check with --start), then exits.
    /// </summary>
    private async void SnapshotWhenSettled(string path, string? size, bool afterCheck)
    {
        try
        {
            var window = OpenWindow();
            if (size?.Split('x') is [var w, var h] && double.TryParse(w, CultureInfo.InvariantCulture, out var width)
                && double.TryParse(h, CultureInfo.InvariantCulture, out var height))
                (window.WindowState, window.Width, window.Height) = (WindowState.Normal, width, height);
            await Task.Delay(TimeSpan.FromSeconds(3));
            // Test hook: switch theme at run time, through the same setting the Settings page changes.
            if (Enum.TryParse<AppTheme>(Arg(Environment.GetCommandLineArgs(), "--switch-theme"), ignoreCase: true, out var switched))
            {
                _vm!.Settings.Theme = switched;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            while (afterCheck && _vm!.IsRunning) await Task.Delay(500);
            for (int i = 0; i < 120 && _vm!.IsReviewing; i++) await Task.Delay(500); // the online review, when on
            await Task.Delay(TimeSpan.FromSeconds(1));
            Snapshot.Save(window, path);
        }
        catch
        {
            Shutdown(1); // a failed render ends with an error code, so a script checking it notices
            throw;
        }
        finally
        {
            ExitApp(); // and never leaves a hidden process behind
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _vm?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.TimeLeft)
            or nameof(MainViewModel.LastLevel) or nameof(MainViewModel.LastResult) or nameof(MainViewModel.IsDisconnected))
            RefreshTray();
    }

    private void ShowWindow()
    {
        var window = OpenWindow();
        if (window.WindowState == WindowState.Minimized) window.WindowState = _restoreState;
        window.Activate();
        _vm.RevealRecommendations();
        RefreshTray();
    }

    /// <summary>Creates the window if none is open, where and how the user last left it, and shows it.</summary>
    private MainWindow OpenWindow()
    {
        if (_window is { } open)
        {
            open.Show();
            return open;
        }
        var window = _window = new MainWindow(_vm, _ui);
        MainWindow = window;
        if (OnScreen(_restoreBounds))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            (window.Left, window.Top, window.Width, window.Height) = (_restoreBounds.Left, _restoreBounds.Top, _restoreBounds.Width, _restoreBounds.Height);
        }
        window.WindowState = _restoreState;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized) _restoreState = window.WindowState;
            else if (_vm.Settings.BackgroundEnabled) // minimize goes to the tray too
                Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_window, window)) window.Close(); });
        };
        window.Closing += (_, _) =>
        {
            if (window.RestoreBounds is { IsEmpty: false } bounds) _restoreBounds = bounds;
        };
        window.Closed += (_, _) => OnWindowClosed(window);
        window.Show();
        return window;
    }

    // With background checks on, closing or minimizing sends the app to the notification area and the window is released
    // rather than hidden, so nothing of it (visual tree, render surface, bindings) stays in memory. Exit in the tray menu quits.
    private void OnWindowClosed(MainWindow window)
    {
        if (!ReferenceEquals(_window, window)) return;
        _window = null;
        if (ReferenceEquals(MainWindow, window)) MainWindow = null;
        _vm.SetWindowVisible(false);
        if (_exiting || !_vm.Settings.BackgroundEnabled)
        {
            Shutdown();
            return;
        }
        RefreshTray();
        ShowTrayHintOnce();
        _windowReleased = true; // its native memory is given back by the next trim, whenever that runs
        TrimMemory(force: true);
    }

    // A window placed on a monitor that is gone since would open out of reach.
    private static bool OnScreen(Rect bounds)
    {
        if (bounds.IsEmpty) return false;
        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        visible.Intersect(bounds);
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 50;
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown || !Notify("ConnectionClue", Localizer.Default.Get("Tray_Hidden"), warning: false)) return;
        _trayHintShown = true;
        Persist();
    }

    private bool Notify(string title, string body, bool warning)
    {
        if (!_notifications.TryNotify()) return false;
        RefreshTray();
        _tray.Notify(title, body, warning);
        return true;
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_window is { } window) window.Close(); // its Closed handler ends the app
        else Shutdown();
    }

    /// <summary>With no window open, compacts the heap and returns the pages the process is not using (Windows brings back
    /// what a check touches). Skipped while a window is open or a check runs (the next check that ends does it), and when it
    /// ran recently, unless forced.</summary>
    private void TrimMemory(bool force = false)
    {
        if (_exiting || _window is not null) return;
        if (!force && _trimmedAt is { } last && Environment.TickCount64 - last < TrimEvery.TotalMilliseconds) return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_exiting || _window is not null || _vm.IsRunning) return;
            MemoryTrim.Now(afterWindow: _windowReleased);
            (_trimmedAt, _windowReleased) = (Environment.TickCount64, false);
        });
    }

    // Windows usually raises several network events for one change; one refresh reads the state they all describe.
    private void QueueConnectivityRefresh()
    {
        if (Interlocked.Exchange(ref _connectivityRefreshQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _connectivityRefreshQueued, 0);
            RefreshConnectivity();
        });
    }

    // One client for the app's lifetime; per-request timeouts come from each reviewer's setting.
    private static readonly System.Net.Http.HttpClient ReviewHttp = new() { Timeout = TimeSpan.FromSeconds(60), DefaultRequestHeaders = { { "User-Agent", "ConnectionClue" } } };
    private static readonly System.Net.Http.HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(10), DefaultRequestHeaders = { { "User-Agent", "ConnectionClue" }, { "Accept", "application/vnd.github+json" } } };

    private void RefreshConnectivity() => _vm?.UpdateConnectivity(IsConnected(), ConnectionCost.IsMobileNetwork());

    // Test hook: --offline pretends there is no network (visual checks of the warning without unplugging anything).
    private bool IsConnected() => !_offline && NetworkStatus.IsConnected(new RouteProvider());

    private void RefreshTray()
    {
        var s = _vm.Settings;
        _tray.Visible = s.BackgroundEnabled || _vm.IsDisconnected || _window?.IsVisible != true || _notifications.HasNotified;
        var state = _vm.IsDisconnected ? TrayState.Warning : _vm.LastLevel switch
        {
            HealthLevel.Unhealthy => TrayState.Problem,
            HealthLevel.Degraded => TrayState.Warning,
            _ => TrayState.Normal,
        };
        // The countdown is not kept while no window is open (nothing else shows it), so the tooltip just says a check runs.
        string detail = _vm.IsDisconnected ? Localizer.Default.Get("Hero_Disconnected")
            : _vm.IsRunning ? (_vm.TimeLeft.Length > 0 ? _vm.TimeLeft : Localizer.Default.Get("Hero_Checking"))
            : _vm.LastResult;
        _tray.Update(state, detail.Length == 0 ? "ConnectionClue" : $"ConnectionClue · {detail}", !_vm.IsRunning, s.BackgroundEnabled);
    }

    private void Persist()
    {
        var s = _vm.Settings;
        SettingsStore.Save(new AppSettings(_language, s.BackgroundEnabled, s.IntervalMinutes, s.DelayLimitMs,
            s.LossLimitPercent, s.VariationLimitMs, s.CheckSeconds, s.MeasureSpeed, s.AiReview, s.Theme,
            s.StartWithWindows, s.PlanDownloadMbps, s.PlanUploadMbps, s.GamingTarget, s.VideoTarget, s.CallsTarget, s.DisconnectTarget,
            s.LongCaptureMinutes, s.BackgroundOnMobileEnabled, s.BackgroundCheckSeconds, AppSettings.CurrentVersion, _trayHintShown));
    }

    private async Task ApplyStartupSettingAsync(SettingsViewModel settings)
    {
        try
        {
            bool requested = settings.StartWithWindows;
            bool enabled = await StartupManager.SetEnabledAsync(requested, Environment.ProcessPath!);
            SetStartupValue(settings, enabled);
            settings.StartupStatus = Localizer.Default.Get(enabled == requested ? "Startup_Enabled" : "Startup_NotEnabled", CultureInfo.CurrentUICulture);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or COMException or InvalidOperationException)
        {
            SetStartupValue(settings, false);
            settings.StartupStatus = Localizer.Default.Get("Startup_Failed", CultureInfo.CurrentUICulture);
        }
    }

    private async Task SyncStartupStateAsync(SettingsViewModel settings)
    {
        try
        {
            bool enabled = await StartupManager.IsEnabledAsync(Environment.ProcessPath!);
            SetStartupValue(settings, enabled);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or COMException or InvalidOperationException)
        {
            SetStartupValue(settings, false);
            settings.StartupStatus = Localizer.Default.Get("Startup_Failed", CultureInfo.CurrentUICulture);
        }
    }

    private void SetStartupValue(SettingsViewModel settings, bool value)
    {
        if (settings.StartWithWindows == value) return;
        _suppressStartupChange = true;
        try { settings.StartWithWindows = value; }
        finally { _suppressStartupChange = false; }
    }

    private static string? Arg(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    /// <summary>
    /// Taskbar jump list (right-click the taskbar button): "Quick check" starts a second copy with --start, which hands the
    /// request to this one through the single-instance gate.
    /// </summary>
    private void SetJumpList()
    {
        var jump = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        jump.JumpItems.Add(new System.Windows.Shell.JumpTask
        {
            Title = Localizer.Default.Get("Action_QuickCheck"),
            Description = Localizer.Default.Get("Jump_QuickCheckHelp"),
            ApplicationPath = Environment.ProcessPath,
            Arguments = "--start",
            IconResourcePath = Environment.ProcessPath,
            IconResourceIndex = 0,
        });
        System.Windows.Shell.JumpList.SetJumpList(this, jump);
        jump.Apply();
    }

    // LAB MANIFEST (never ships): example.com stands in until product-owned endpoints exist (handoff §8).
    private async Task<PreviewProbes> StartSessionAsync(SessionClock clock, CancellationToken ct)
    {
        var path = new RouteProvider().GetDefaultPath(IpFamily.IPv4);
        var gateway = path?.NextHop;
        var context = new CheckContext(path?.Medium switch
        {
            InterfaceMedium.WiFi => ConnectionMedium.WiFi,
            InterfaceMedium.Ethernet => ConnectionMedium.Ethernet,
            _ => ConnectionMedium.Unknown,
        }, path?.TunnelSuspected ?? false, ConnectionCost.IsMetered(), Shell.RouterAddress(gateway));
        IPAddress[] web;
        try { web = await Dns.GetHostAddressesAsync("example.com", AddressFamily.InterNetwork, ct); }
        catch (SocketException) { web = []; }

        var targets = new StaticTargets(
            new ResolvedTarget("gateway", "local", null, 0, "/", gateway is null ? [] : [gateway]),
            new ResolvedTarget("web", "lab", "example.com", 443, "/", web, 200, "Example Domain"));
        // Read-only settings snapshot (registry, WMI, power APIs) runs off the UI thread while the check runs. A scheduled
        // check reuses the facts that are slow to read and rarely change; a check the user starts reads everything fresh.
        bool scheduled = _vm.CurrentCheckKind == CheckKind.Background;
        var facts = Task.Run(() => SystemFactsMapper.ToFacts(SystemInspector.Inspect(path, reuseSlowFacts: scheduled), context));
        var links = path is null ? null : new LinkWatch(path.InterfaceLuid, path.Medium == InterfaceMedium.WiFi);
        return new PreviewProbes(new IcmpProbe(clock, targets), new TcpProbe(clock, targets),
            new SystemDnsProbe(clock, targets), new HttpsProbe(clock, targets), context,
            // LAB throughput endpoint (public test service); needs a product-owned or licensed service before release (handoff §8).
            new ThroughputProbe(clock.Time, "https://speed.cloudflare.com/__down?bytes={0}", new Uri("https://speed.cloudflare.com/__up")),
            facts, path is null ? null : () => SystemInspector.ReadCounters(path.InterfaceLuid), AppTrafficAsync,
            LinkEvents: links is null ? null : links.Snapshot, Monitors: links);
    }

    private static async Task<IReadOnlyList<AppTraffic>> AppTrafficAsync(DateTimeOffset from, DateTimeOffset to) =>
        [.. (await AppNetworkUsage.TopAsync(from, to)).Select(u => new AppTraffic(u.Name, System.IO.Path.GetFileName(u.Path), u.Sent / 1e6, u.Received / 1e6))];

    private sealed class StaticTargets(params ResolvedTarget[] targets) : ITargetResolver
    {
        public ResolvedTarget? Resolve(StreamKey stream) => targets.FirstOrDefault(t => t.TargetId == stream.TargetId);
    }
}
