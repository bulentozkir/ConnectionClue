namespace ConnectionClue.Presentation.Tests;

/// <summary>
/// The shell lives in WPF, which these tests cannot start, so they read its source: the policies that keep a tray app
/// light (and quiet) must not be edited out unnoticed.
/// </summary>
public sealed class AppLifecycleTests
{
    [Fact]
    public void Msi_replaces_older_and_same_version_installations_with_rollback_protection()
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ConnectionClue.wxs"));
        System.Xml.Linq.XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        var package = Assert.Single(document.Descendants(wix + "Package"));
        Assert.Equal("7C6F1D5E-3B8A-4F2C-9D41-5A0E8B2C6F13", (string?)package.Attribute("UpgradeCode"));
        var upgrade = Assert.Single(package.Elements(wix + "MajorUpgrade"));
        Assert.Equal("yes", (string?)upgrade.Attribute("AllowSameVersionUpgrades"));
        Assert.Equal("afterInstallInitialize", (string?)upgrade.Attribute("Schedule"));
        Assert.NotEqual("yes", (string?)upgrade.Attribute("AllowDowngrades"));
        Assert.NotEqual("yes", (string?)upgrade.Attribute("IgnoreRemoveFailure"));
    }

    [Fact]
    public void Quick_check_requires_control_f5_and_help_matches_the_binding()
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var bindings = document.Descendants().Where(e => e.Name.LocalName == "KeyBinding");
        var quick = Assert.Single(bindings, e => (string?)e.Attribute("Command") == "{Binding QuickCheckCommand}");
        Assert.Equal("F5", (string?)quick.Attribute("Key"));
        Assert.Equal("Control", (string?)quick.Attribute("Modifiers"));
        Assert.DoesNotContain(bindings, e => (string?)e.Attribute("Key") == "F5"
            && string.IsNullOrEmpty((string?)e.Attribute("Modifiers")));
        foreach (var culture in ConnectionClue.Presentation.Localization.SupportedLanguages.All)
        {
            Assert.Contains("Ctrl+F5", ConnectionClue.Presentation.Localization.Localizer.Default.Get("Settings_ShortcutCheck", culture));
            Assert.Contains("Ctrl+F5", ConnectionClue.Presentation.Localization.Localizer.Default.Get("ButtonHelp_QuickCheck", culture));
        }
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    [Fact]
    public void The_window_is_released_not_hidden_when_the_app_goes_to_the_tray()
    {
        string code = Read("App.xaml.cs");
        Assert.Contains("ShutdownMode=\"OnExplicitShutdown\"", Read("AppStyles.xaml"), StringComparison.Ordinal);
        Assert.Contains("window.Closed += (_, _) => OnWindowClosed(window);", code, StringComparison.Ordinal);
        Assert.Equal(1, Count(code, "new MainWindow(")); // one place creates it: OpenWindow
        Assert.DoesNotContain(".Hide()", code, StringComparison.Ordinal);
        // A released window must not stay reachable from the view model, which outlives it.
        Assert.Contains("vm.Announce -= announce", Read("MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_at_sign_in_creates_no_window_and_never_shows_a_message()
    {
        string code = Read("App.xaml.cs");
        Assert.Contains("bool startHidden = atSignIn && settings.BackgroundEnabled;", code, StringComparison.Ordinal);
        Assert.Contains("if (!activatedExisting && !atSignIn)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_at_sign_in_is_recognised_for_the_registry_entry_and_for_an_installed_package()
    {
        Assert.Contains("bool atSignIn = !snapshotRun && (e.Args.Contains(\"--startup\") || StartupManager.LaunchedByStartupTask());",
            Read("App.xaml.cs"), StringComparison.Ordinal);
        // The package's startup task cannot carry a flag, so Windows' own activation record says how the copy was started.
        Assert.Contains("AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask", Read("StartupManager.cs"), StringComparison.Ordinal);
        Assert.Contains("run.SetValue(RunValue, $\"\\\"{executablePath}\\\" --startup\"", Read("StartupManager.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stray_exception_on_the_ui_thread_does_not_end_the_app_once_it_is_running()
    {
        string code = Read("App.xaml.cs");
        Assert.Contains("if (a.Exception is OutOfMemoryException) return;", code, StringComparison.Ordinal);
        Assert.Contains("a.Handled = true;", code, StringComparison.Ordinal);
        // One thrown while starting would leave a process with no icon and no way out, so it ends the app.
        Assert.Contains("if (!_started) Shutdown(1);", code, StringComparison.Ordinal);
        Assert.Matches(@"_started = true;\s+if \(Arg\(e\.Args, ""--snapshot""\) is \{ \} snapshot\)", code);
    }

    [Fact]
    public void A_visual_check_always_exits_and_a_failed_one_exits_with_an_error_code()
    {
        // Success and failure both end the app (no hidden process left behind); a failure does so with a non-zero exit code.
        Assert.Matches(@"catch\s*\{\s*Shutdown\(1\);[^}]*throw;\s*\}\s*finally\s*\{\s*ExitApp\(\);", Read("App.xaml.cs"));
    }

    [Fact]
    public void Failing_to_read_how_a_package_was_started_shows_the_window_instead_of_ending_the_app()
    {
        // The probe runs before start-up has finished, where an escaping exception ends the app: it must catch everything.
        Assert.Matches(@"LaunchedByStartupTask\(\)[\s\S]*?catch \(Exception e\) when \(e is not OutOfMemoryException\)\s*\{\s*return false;",
            Read("StartupManager.cs"));
    }

    [Fact]
    public void Memory_is_given_back_only_while_no_window_is_open()
    {
        string code = Read("App.xaml.cs");
        Assert.Contains("_vm.CheckFinished += (_, _) => TrimMemory();", code, StringComparison.Ordinal);
        Assert.Contains("if (_exiting || _window is not null) return;", code, StringComparison.Ordinal);
        Assert.Contains("if (_exiting || _window is not null || _vm.IsRunning) return;", code, StringComparison.Ordinal);
        Assert.Contains("MemoryTrim.Now(afterWindow: _windowReleased);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_is_set_up_for_a_small_idle_process()
    {
        string project = Read("ConnectionClue.App.csproj");
        Assert.Contains("<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>", project, StringComparison.Ordinal);
        Assert.Contains("<TieredPGO>false</TieredPGO>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_is_drawn_without_a_desktop_compositor_backdrop()
    {
        Assert.Contains("Include=\"Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop\" Value=\"true\"",
            Read("ConnectionClue.App.csproj"), StringComparison.Ordinal);
    }
}
