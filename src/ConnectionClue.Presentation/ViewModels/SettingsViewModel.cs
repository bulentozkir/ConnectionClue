using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;

namespace ConnectionClue.Presentation.ViewModels;

public sealed record Choice(int Value, string Label);

/// <summary>A theme in the Settings selector.</summary>
public sealed record ThemeChoice(ConnectionClue.Presentation.Theming.AppTheme Value, string Label);

/// <summary>
/// User preferences: quick check length and speed test, background schedule and check length (including mobile networks and
/// Windows startup), alert limits, online review, theme, plan speeds and per-symptom targets. Values outside the offered
/// choices snap to defaults.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public static readonly int[] IntervalOptions = [3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480];
    public static readonly int[] DelayOptions = [50, 100, 150, 200, 300];
    public static readonly int[] LossOptions = [1, 2, 5, 10];
    public static readonly int[] VariationOptions = [10, 20, 30, 50];
    public static readonly int[] LongCaptureOptions = [15, 30, 45, 60, 120, 240, 480];
    public const int DefaultIntervalMinutes = 5;
    public const int DefaultLongCaptureMinutes = 15, MaxLongCaptureMinutes = 480;
    /// <summary>Quick checks and background checks share this length range.</summary>
    public const int MinCheckSeconds = 10, MaxCheckSeconds = 60, DefaultCheckSeconds = 30, DefaultBackgroundCheckSeconds = 10;
    /// <summary>
    /// Settings format version. 2: symptom targets have defaults and Capture longer defaults to 15 minutes instead of 1 hour.
    /// 3: background checks default to every 5 minutes instead of 15.
    /// </summary>
    public const int CurrentVersion = 3;
    private readonly Localizer _l;
    private readonly CultureInfo _ui;
    private string _checkSecondsText, _backgroundCheckSecondsText;
    private string _planDownloadText = "";
    private string _planUploadText = "";

    public SettingsViewModel(Localizer l, CultureInfo ui, bool backgroundEnabled = true, int intervalMinutes = DefaultIntervalMinutes,
        int delayLimitMs = 100, int lossLimitPercent = 2, int variationLimitMs = 30, int checkSeconds = DefaultCheckSeconds, bool measureSpeed = true,
        bool aiReview = true, ConnectionClue.Presentation.Theming.AppTheme theme = ConnectionClue.Presentation.Theming.AppTheme.Dark,
        bool startWithWindows = false, double planDownloadMbps = 0, double planUploadMbps = 0,
        string gamingTarget = "", string videoTarget = "", string callsTarget = "", string disconnectTarget = "",
        int longCaptureMinutes = DefaultLongCaptureMinutes, bool backgroundOnMobileEnabled = false,
        int backgroundCheckSeconds = DefaultBackgroundCheckSeconds)
    {
        (_l, _ui) = (l, ui);
        Themes = [.. Enum.GetValues<ConnectionClue.Presentation.Theming.AppTheme>().Select(t => new ThemeChoice(t, l.Get($"Theme_{t}", ui)))];
        Theme = Enum.IsDefined(theme) ? theme : ConnectionClue.Presentation.Theming.AppTheme.Dark;
        var f = CultureInfo.CurrentCulture;
        Intervals = [.. IntervalOptions.Select(m => new Choice(m, m % 60 == 0
            ? l.Plural("Duration_Hours", m / 60, ui) : l.Plural("Duration_Minutes", m, ui)))];
        DelayLimits = [.. DelayOptions.Select(v => new Choice(v, string.Format(f, "{0} ms", v)))];
        LossLimits = [.. LossOptions.Select(v => new Choice(v, (v / 100.0).ToString("P0", f)))];
        VariationLimits = [.. VariationOptions.Select(v => new Choice(v, string.Format(f, "{0} ms", v)))];
        LongCaptureLengths = [.. LongCaptureOptions.Select(m => new Choice(m, m < 60 ? string.Format(f, "{0} min", m) : string.Format(f, "{0} h", m / 60)))];
        BackgroundEnabled = backgroundEnabled;
        IntervalMinutes = Snap(intervalMinutes, IntervalOptions, DefaultIntervalMinutes);
        DelayLimitMs = Snap(delayLimitMs, DelayOptions, 100);
        LossLimitPercent = Snap(lossLimitPercent, LossOptions, 2);
        VariationLimitMs = Snap(variationLimitMs, VariationOptions, 30);
        CheckSeconds = Math.Clamp(checkSeconds, MinCheckSeconds, MaxCheckSeconds);
        BackgroundCheckSeconds = Math.Clamp(backgroundCheckSeconds, MinCheckSeconds, MaxCheckSeconds);
        MeasureSpeed = measureSpeed;
        AiReview = aiReview;
        StartWithWindows = startWithWindows;
        LongCaptureMinutes = Snap(longCaptureMinutes, LongCaptureOptions, DefaultLongCaptureMinutes);
        BackgroundOnMobileEnabled = backgroundOnMobileEnabled;
        PlanDownloadMbps = ValidPlanSpeed(planDownloadMbps) ? planDownloadMbps : 0;
        PlanUploadMbps = ValidPlanSpeed(planUploadMbps) ? planUploadMbps : 0;
        (GamingTarget, VideoTarget, CallsTarget, DisconnectTarget) =
            (gamingTarget, videoTarget, callsTarget, disconnectTarget);
        _checkSecondsText = CheckSeconds.ToString(f);
        _backgroundCheckSecondsText = BackgroundCheckSeconds.ToString(f);
        _planDownloadText = PlanText(PlanDownloadMbps, f);
        _planUploadText = PlanText(PlanUploadMbps, f);
        CheckLengthHint = string.Format(f, l.Get("Settings_CheckLengthHint", ui), MinCheckSeconds, MaxCheckSeconds);
    }

    /// <summary>Review recommendations with free online LLMs before showing them (generic text only; see OnlineAdviceReviewer).</summary>
    [ObservableProperty]
    public partial bool AiReview { get; set; }

    /// <summary>Colour theme; Dark by default. Applied at once, without a restart.</summary>
    [ObservableProperty]
    public partial ConnectionClue.Presentation.Theming.AppTheme Theme { get; set; }

    public IReadOnlyList<ThemeChoice> Themes { get; }

    /// <summary>Windows high contrast is on: its colours apply whatever theme is chosen (shown as a note in Settings).</summary>
    [ObservableProperty]
    public partial bool WindowsHighContrast { get; set; }

    /// <summary>Starts the app with Windows; disabled by default and starts it in the notification area.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string StartupStatus { get; set; } = "";

    [ObservableProperty]
    public partial string GamingTarget { get; set; } = "";

    [ObservableProperty]
    public partial string VideoTarget { get; set; } = "";

    [ObservableProperty]
    public partial string CallsTarget { get; set; } = "";

    [ObservableProperty]
    public partial string DisconnectTarget { get; set; } = "";

    public string TargetFor(Symptom? symptom) => symptom switch
    {
        Symptom.Video => VideoTarget,
        Symptom.Calls => CallsTarget,
        Symptom.Disconnects => DisconnectTarget,
        _ => GamingTarget,
    };

    public void SetTarget(Symptom? symptom, string value)
    {
        switch (symptom)
        {
            case Symptom.Video: VideoTarget = value; break;
            case Symptom.Calls: CallsTarget = value; break;
            case Symptom.Disconnects: DisconnectTarget = value; break;
            default: GamingTarget = value; break;
        }
    }

    [ObservableProperty]
    public partial double PlanDownloadMbps { get; set; }

    [ObservableProperty]
    public partial double PlanUploadMbps { get; set; }

    [ObservableProperty]
    public partial bool IsPlanSpeedInvalid { get; set; }

    [ObservableProperty]
    public partial bool IsPlanDownloadSpeedInvalid { get; set; }

    [ObservableProperty]
    public partial bool IsPlanUploadSpeedInvalid { get; set; }

    public string PlanDownloadText
    {
        get => _planDownloadText;
        set
        {
            if (!SetProperty(ref _planDownloadText, value)) return;
            if (TryPlanSpeed(value, out double parsed))
                PlanDownloadMbps = parsed;
            RefreshPlanValidation();
        }
    }

    public string PlanUploadText
    {
        get => _planUploadText;
        set
        {
            if (!SetProperty(ref _planUploadText, value)) return;
            if (TryPlanSpeed(value, out double parsed))
                PlanUploadMbps = parsed;
            RefreshPlanValidation();
        }
    }

    public void CommitPlanSpeedText()
    {
        if (IsPlanDownloadSpeedInvalid)
        {
            _planDownloadText = PlanText(PlanDownloadMbps, CultureInfo.CurrentCulture);
            OnPropertyChanged(nameof(PlanDownloadText));
        }
        if (IsPlanUploadSpeedInvalid)
        {
            _planUploadText = PlanText(PlanUploadMbps, CultureInfo.CurrentCulture);
            OnPropertyChanged(nameof(PlanUploadText));
        }
        RefreshPlanValidation();
    }

    /// <summary>Download and upload phase after the latency phase of quick checks; background checks take a light sample about
    /// once an hour instead (<see cref="MainViewModel.SpeedSampleEvery"/>).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CheckTotalHint))]
    public partial bool MeasureSpeed { get; set; }

    /// <summary>Length of the delay-measurement phase of quick checks; the speed test comes on top.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CheckTotalHint))]
    public partial int CheckSeconds { get; set; }

    /// <summary>Length of each regular background check's delay phase. Background checks run no service tests; about once an
    /// hour a light speed sample of a few seconds follows.</summary>
    [ObservableProperty]
    public partial int BackgroundCheckSeconds { get; set; }

    /// <summary>The speed test's two phases (download, then upload) that follow the delay phase of a manual check.</summary>
    public static int SpeedTestSeconds => (int)(2 * MainViewModel.SpeedPhase.TotalSeconds);

    /// <summary>What a quick check takes with these settings, so the length field never promises less than the check lasts.</summary>
    public string CheckTotalHint => MeasureSpeed
        ? string.Format(CultureInfo.CurrentCulture, _l.Get("Settings_CheckTotalSpeed", _ui), CheckSeconds, CheckSeconds + SpeedTestSeconds)
        : string.Format(CultureInfo.CurrentCulture, _l.Get("Settings_CheckTotal", _ui), CheckSeconds);

    /// <summary>Gives every empty symptom target its default service (first start, or settings saved before defaults existed).</summary>
    public void ApplyDefaultTargets()
    {
        foreach (var symptom in Enum.GetValues<Symptom>())
            if (string.IsNullOrWhiteSpace(TargetFor(symptom))) SetTarget(symptom, SymptomServices.DefaultTargetText(symptom));
    }

    /// <summary>Moves settings saved by an older version (see <see cref="CurrentVersion"/>) to the defaults introduced since;
    /// only values still at an old default change, so a later choice of the user is kept.</summary>
    public void Upgrade(int savedVersion)
    {
        if (savedVersion < 2)
        {
            ApplyDefaultTargets();
            if (LongCaptureMinutes == 60) LongCaptureMinutes = DefaultLongCaptureMinutes;
        }
        if (savedVersion < 3 && IntervalMinutes == 15) IntervalMinutes = DefaultIntervalMinutes;
    }

    /// <summary>Text field for <see cref="CheckSeconds"/>; applied only when it parses to a whole number in range.</summary>
    public string CheckSecondsText
    {
        get => _checkSecondsText;
        set
        {
            if (!SetProperty(ref _checkSecondsText, value)) return;
            IsCheckSecondsInvalid = !TryCheckSeconds(value, out int seconds);
            if (!IsCheckSecondsInvalid) CheckSeconds = seconds;
        }
    }

    [ObservableProperty]
    public partial bool IsCheckSecondsInvalid { get; set; }

    /// <summary>The allowed range, shared by both length fields.</summary>
    public string CheckLengthHint { get; }

    /// <summary>When the field loses focus, show the value in effect again (discarding an invalid entry).</summary>
    public void CommitCheckSecondsText()
    {
        _checkSecondsText = CheckSeconds.ToString(CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(CheckSecondsText));
        IsCheckSecondsInvalid = false;
    }

    /// <summary>Text field for <see cref="BackgroundCheckSeconds"/>; applied only when it parses to a whole number in range.</summary>
    public string BackgroundCheckSecondsText
    {
        get => _backgroundCheckSecondsText;
        set
        {
            if (!SetProperty(ref _backgroundCheckSecondsText, value)) return;
            IsBackgroundCheckSecondsInvalid = !TryCheckSeconds(value, out int seconds);
            if (!IsBackgroundCheckSecondsInvalid) BackgroundCheckSeconds = seconds;
        }
    }

    [ObservableProperty]
    public partial bool IsBackgroundCheckSecondsInvalid { get; set; }

    public void CommitBackgroundCheckSecondsText()
    {
        _backgroundCheckSecondsText = BackgroundCheckSeconds.ToString(CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(BackgroundCheckSecondsText));
        IsBackgroundCheckSecondsInvalid = false;
    }

    private static bool TryCheckSeconds(string text, out int seconds) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out seconds)
        && seconds is >= MinCheckSeconds and <= MaxCheckSeconds;

    public IReadOnlyList<Choice> Intervals { get; }
    public IReadOnlyList<Choice> DelayLimits { get; }
    public IReadOnlyList<Choice> LossLimits { get; }
    public IReadOnlyList<Choice> VariationLimits { get; }
    public IReadOnlyList<Choice> LongCaptureLengths { get; }

    [ObservableProperty]
    public partial int LongCaptureMinutes { get; set; } = DefaultLongCaptureMinutes;

    [ObservableProperty]
    public partial bool BackgroundEnabled { get; set; }

    /// <summary>Allows lightweight scheduled connectivity checks over cellular networks; disabled by default.</summary>
    [ObservableProperty]
    public partial bool BackgroundOnMobileEnabled { get; set; }

    [ObservableProperty]
    public partial int IntervalMinutes { get; set; }

    [ObservableProperty]
    public partial int DelayLimitMs { get; set; }

    [ObservableProperty]
    public partial int LossLimitPercent { get; set; }

    [ObservableProperty]
    public partial int VariationLimitMs { get; set; }

    public HealthThresholds Thresholds => new(DelayLimitMs, LossLimitPercent, VariationLimitMs);

    private static int Snap(int value, int[] options, int fallback) => options.Contains(value) ? value : fallback;
    private static bool ValidPlanSpeed(double value) => double.IsFinite(value) && value is > 0 and <= 1_000_000;

    private void RefreshPlanValidation()
    {
        IsPlanDownloadSpeedInvalid = !TryPlanSpeed(PlanDownloadText, out _);
        IsPlanUploadSpeedInvalid = !TryPlanSpeed(PlanUploadText, out _);
        IsPlanSpeedInvalid = IsPlanDownloadSpeedInvalid || IsPlanUploadSpeedInvalid;
    }

    private static bool TryPlanSpeed(string text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return true;
        }
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && ValidPlanSpeed(value);
    }

    private static string PlanText(double value, CultureInfo culture) => value > 0 ? value.ToString("0.##", culture) : "";
}
