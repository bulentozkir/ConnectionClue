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

namespace ConnectionClue.App;

/// <summary>Composition root and window/tray lifecycle.</summary>
public partial class App : Application
{
    private MainViewModel _vm = null!;
    private MainWindow _window = null!;
    private TrayIcon _tray = null!;
    private SingleInstanceGate? _singleInstance;
    private string? _language;
    private bool _exiting, _trayHintShown, _offline, _suppressStartupChange;
    private WindowState _restoreState = WindowState.Maximized;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A visual check (--snapshot) only renders the window to a file and exits: it never hands off to a running copy.
        if (Arg(e.Args, "--snapshot") is null)
        {
            _singleInstance = SingleInstanceGate.Acquire(e.Args.Contains("--start"), out bool activatedExisting);
            if (_singleInstance is null)
            {
                if (!activatedExisting)
                    MessageBox.Show("ConnectionClue is already running, but Windows could not activate its window. Use its notification-area icon to open it.",
                        "ConnectionClue", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        _offline = e.Args.Contains("--offline");
        var saved = SettingsStore.Load();
        _language = saved.Language;
        // Theme before any window exists, so nothing flashes in the wrong colours (--theme for testing).
        var theme = Enum.TryParse<AppTheme>(Arg(e.Args, "--theme"), ignoreCase: true, out var forced) ? forced : saved.Theme;
        ThemeManager.Apply(this, theme);
        // English unless the user picked another language (saved setting, or --lang for testing).
        var ui = LanguageResolver.Resolve([], Arg(e.Args, "--lang") ?? saved.Language);
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = ui;
        var l = Localizer.Default;

        var settings = new SettingsViewModel(l, ui, saved.BackgroundEnabled, saved.IntervalMinutes,
            saved.DelayLimitMs, saved.LossLimitPercent, saved.VariationLimitMs, saved.CheckSeconds, saved.MeasureSpeed, saved.AiReview, theme,
            saved.StartWithWindows, saved.PlanDownloadMbps, saved.PlanUploadMbps, saved.GamingTarget, saved.VideoTarget,
            saved.CallsTarget, saved.DisconnectTarget, saved.LongCaptureMinutes, saved.BackgroundOnMobileEnabled)
        {
            WindowsHighContrast = ThemeManager.WindowsHighContrast,
        };
        // New install, or settings from before version 2: fill empty targets once (a later clear is kept), and move the old
        // 1-hour Capture longer default to the new 15-minute one.
        if (saved.SettingsVersion < AppSettings.CurrentVersion)
        {
            settings.ApplyDefaultTargets();
            if (saved.LongCaptureMinutes == 60) settings.LongCaptureMinutes = SettingsViewModel.DefaultLongCaptureMinutes;
        }
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
            ConnectionCost.IsMobileNetwork, new NetworkDiagnostics(), new SupportReportExporter(), new ResultsExporter(() => _window.ResultVisuals));
        _window = new MainWindow(_vm, ui);
        _tray = new TrayIcon(l.Get("Tray_Open"), l.Get("Action_QuickCheck"), l.Get("Background_Enable"), l.Get("Tray_Exit"),
            ui.TextInfo.IsRightToLeft);

        settings.PropertyChanged += (_, _) => { Persist(); RefreshTray(); };
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.AlertRaised += (_, a) =>
        {
            if (!_window.IsActive && _tray.Visible) _tray.Notify(a.Title, a.Body, a.Kind != AlertKind.Recovered);
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

        // With background checks on, minimize and close hide to the notification area; Exit (tray menu) quits.
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState != WindowState.Minimized) _restoreState = _window.WindowState;
            else if (settings.BackgroundEnabled) HideToTray();
        };
        _window.Closing += (_, a) =>
        {
            if (_exiting || !settings.BackgroundEnabled) return;
            a.Cancel = true;
            HideToTray();
        };

        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested += (_, quickCheck) => Dispatcher.BeginInvoke(() =>
            {
                ShowWindow();
                if (quickCheck && _vm.QuickCheckCommand.CanExecute(null)) _vm.QuickCheckCommand.Execute(null);
            });
            _singleInstance.Listen();
        }
        _window.Show();
        SetJumpList();
        if (e.Args.Contains("--startup") && settings.BackgroundEnabled)
        {
            HideToTray();
            if (!ConnectionCost.IsMobileNetwork() || settings.BackgroundOnMobileEnabled)
                _ = _vm.RunCheckAsync(measureSpeed: false);
        }
        RefreshTray();
        // Live connectivity: with no network at all the status and tray warn, and no check starts.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(RefreshConnectivity);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(RefreshConnectivity);
        global::Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged +=
            _ => Dispatcher.BeginInvoke(RefreshConnectivity);
        RefreshConnectivity();
        if (e.Args.Contains("--start")) _vm.QuickCheckCommand.Execute(null);
        if (Enum.TryParse<AppPage>(Arg(e.Args, "--page"), ignoreCase: true, out var page)) _vm.Page = page; // test hook
        if (Arg(e.Args, "--snapshot") is { } snapshot) SnapshotWhenSettled(snapshot, Arg(e.Args, "--size"), e.Args.Contains("--start"));
    }

    /// <summary>
    /// Test hook for visual checks without a visible desktop (CI agents, a locked PC): --snapshot out.png [--size 1240x768]
    /// [--start] renders the window with WPF's software renderer once it settles (after the check with --start), then exits.
    /// </summary>
    private async void SnapshotWhenSettled(string path, string? size, bool afterCheck)
    {
        if (size?.Split('x') is [var w, var h] && double.TryParse(w, CultureInfo.InvariantCulture, out var width)
            && double.TryParse(h, CultureInfo.InvariantCulture, out var height))
            (_window!.WindowState, _window.Width, _window.Height) = (WindowState.Normal, width, height);
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
        Snapshot.Save(_window!, path);
        ExitApp();
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
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        _window.Activate();
        _vm.RevealRecommendations();
        RefreshTray();
    }

    private void HideToTray()
    {
        _window.Hide();
        RefreshTray();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.Notify("ConnectionClue", Localizer.Default.Get("Tray_Hidden"), warning: false);
    }

    private void ExitApp()
    {
        _exiting = true;
        _window.Close();
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
        _tray.Visible = s.BackgroundEnabled || !_window.IsVisible;
        var state = _vm.IsDisconnected ? TrayState.Warning : _vm.LastLevel switch
        {
            HealthLevel.Unhealthy => TrayState.Problem,
            HealthLevel.Degraded => TrayState.Warning,
            _ => TrayState.Normal,
        };
        string detail = _vm.IsRunning ? _vm.TimeLeft : _vm.IsDisconnected ? Localizer.Default.Get("Hero_Disconnected") : _vm.LastResult;
        _tray.Update(state, detail.Length == 0 ? "ConnectionClue" : $"ConnectionClue · {detail}", !_vm.IsRunning, s.BackgroundEnabled);
    }

    private void Persist()
    {
        var s = _vm.Settings;
        SettingsStore.Save(new AppSettings(_language, s.BackgroundEnabled, s.IntervalMinutes, s.DelayLimitMs,
            s.LossLimitPercent, s.VariationLimitMs, s.CheckSeconds, s.MeasureSpeed, s.AiReview, s.Theme,
            s.StartWithWindows, s.PlanDownloadMbps, s.PlanUploadMbps, s.GamingTarget, s.VideoTarget, s.CallsTarget, s.DisconnectTarget,
            s.LongCaptureMinutes, s.BackgroundOnMobileEnabled, AppSettings.CurrentVersion));
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
    private static async Task<PreviewProbes> StartSessionAsync(SessionClock clock, CancellationToken ct)
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
        // Read-only settings snapshot (registry, WMI, power APIs) runs off the UI thread while the check runs.
        var facts = Task.Run(() => ToFacts(SystemInspector.Inspect(path), context));
        var links = path is null ? null : new LinkWatch(path.InterfaceLuid, path.Medium == InterfaceMedium.WiFi);
        return new PreviewProbes(new IcmpProbe(clock, targets), new TcpProbe(clock, targets),
            new SystemDnsProbe(clock, targets), new HttpsProbe(clock, targets), context,
            // LAB throughput endpoint (public test service); needs a product-owned or licensed service before release (handoff §8).
            new ThroughputProbe(clock.Time, "https://speed.cloudflare.com/__down?bytes={0}", new Uri("https://speed.cloudflare.com/__up")),
            facts, path is null ? null : () => SystemInspector.ReadCounters(path.InterfaceLuid), AppTrafficAsync,
            LinkEvents: links is null ? null : links.Snapshot, Monitors: links);
    }

    private static SystemFacts ToFacts(SystemSnapshot s, CheckContext context)
    {
        string? dns = s.DnsServers is { Count: > 0 } servers ? servers[0] : null;
        return new(
            s.Medium is null ? null : new AdapterFacts(context.Medium, s.LinkMbps, s.DriverDate, s.PowerOffAllowed, s.EnergyEfficientEthernet,
                s.SignalBars, s.IsUsb, s.UsbSelectiveSuspend, s.AdapterName, s.DriverVersion, s.DriverProvider),
            context.Metered, s.ProxyConfigured, context.TunnelSuspected, s.Ipv6Disabled, s.TcpAutoTuningLimited, s.OnBattery,
            s.PowerSaverActive, s.WifiPowerSavingOnBattery, s.WifiPowerSavingOnAc,
            NetworkName: s.NetworkName, DnsServer: dns, DnsIsRouter: dns is not null && dns == context.Gateway, ProxyAddress: s.ProxyAddress,
            EnergySaverOn: s.EnergySaverOn, BestEfficiency: s.BestEfficiencyMode, TcpAutoTuningLevel: s.TcpAutoTuningLevel,
            Ipv6DisabledComponents: s.Ipv6DisabledComponents, Ipv6UnboundOnAdapter: s.Ipv6UnboundOnAdapter, TunnelName: s.TunnelName);
    }

    private static async Task<IReadOnlyList<AppTraffic>> AppTrafficAsync(DateTimeOffset from, DateTimeOffset to) =>
        [.. (await AppNetworkUsage.TopAsync(from, to)).Select(u => new AppTraffic(u.Name, System.IO.Path.GetFileName(u.Path), u.Sent / 1e6, u.Received / 1e6))];

    private sealed class StaticTargets(params ResolvedTarget[] targets) : ITargetResolver
    {
        public ResolvedTarget? Resolve(StreamKey stream) => targets.FirstOrDefault(t => t.TargetId == stream.TargetId);
    }
}
