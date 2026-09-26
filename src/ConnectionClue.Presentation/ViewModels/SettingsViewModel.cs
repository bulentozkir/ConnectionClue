using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Localization;

namespace ConnectionClue.Presentation.ViewModels;

public sealed record Choice(int Value, string Label);

/// <summary>A theme in the Settings selector.</summary>
public sealed record ThemeChoice(ConnectionClue.Presentation.Theming.AppTheme Value, string Label);

/// <summary>User preferences for background checks and alert limits. Values outside the offered choices snap to defaults.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public static readonly int[] IntervalOptions = [10, 15, 30, 60, 120];
    public static readonly int[] DelayOptions = [50, 100, 150, 200, 300];
    public static readonly int[] LossOptions = [1, 2, 5, 10];
    public static readonly int[] VariationOptions = [10, 20, 30, 50];
    public static readonly int[] LongCaptureOptions = [60, 120, 240, 480];
    public const int DefaultLongCaptureMinutes = 60, MaxLongCaptureMinutes = 480;
    public const int MinCheckSeconds = 10, MaxCheckSeconds = 600, DefaultCheckSeconds = 30;
    private string _checkSecondsText;
    private string _planDownloadText = "";
    private string _planUploadText = "";

    public SettingsViewModel(Localizer l, CultureInfo ui, bool backgroundEnabled = true, int intervalMinutes = 15,
        int delayLimitMs = 100, int lossLimitPercent = 2, int variationLimitMs = 30, int checkSeconds = DefaultCheckSeconds, bool measureSpeed = true,
        bool aiReview = true, ConnectionClue.Presentation.Theming.AppTheme theme = ConnectionClue.Presentation.Theming.AppTheme.Dark,
        bool startWithWindows = false, double planDownloadMbps = 0, double planUploadMbps = 0,
        string gamingTarget = "", string videoTarget = "", string callsTarget = "", string disconnectTarget = "",
        int longCaptureMinutes = DefaultLongCaptureMinutes, bool backgroundOnMobileEnabled = false)
    {
        Themes = [.. Enum.GetValues<ConnectionClue.Presentation.Theming.AppTheme>().Select(t => new ThemeChoice(t, l.Get($"Theme_{t}", ui)))];
        Theme = Enum.IsDefined(theme) ? theme : ConnectionClue.Presentation.Theming.AppTheme.Dark;
        var f = CultureInfo.CurrentCulture;
        Intervals = [.. IntervalOptions.Select(m => new Choice(m, l.Plural("Duration_Minutes", m, ui)))];
        DelayLimits = [.. DelayOptions.Select(v => new Choice(v, string.Format(f, "{0} ms", v)))];
        LossLimits = [.. LossOptions.Select(v => new Choice(v, (v / 100.0).ToString("P0", f)))];
        VariationLimits = [.. VariationOptions.Select(v => new Choice(v, string.Format(f, "{0} ms", v)))];
        LongCaptureLengths = [.. LongCaptureOptions.Select(m => new Choice(m, string.Format(f, "{0} h", m / 60)))];
        BackgroundEnabled = backgroundEnabled;
        IntervalMinutes = Snap(intervalMinutes, IntervalOptions, 15);
        DelayLimitMs = Snap(delayLimitMs, DelayOptions, 100);
        LossLimitPercent = Snap(lossLimitPercent, LossOptions, 2);
        VariationLimitMs = Snap(variationLimitMs, VariationOptions, 30);
        CheckSeconds = Math.Clamp(checkSeconds, MinCheckSeconds, MaxCheckSeconds);
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

    /// <summary>Download and upload phase after the latency phase of manual checks (never in background checks).</summary>
    [ObservableProperty]
    public partial bool MeasureSpeed { get; set; }

    /// <summary>Quick check length for manual and background checks.</summary>
    [ObservableProperty]
    public partial int CheckSeconds { get; set; }

    /// <summary>Text field for <see cref="CheckSeconds"/>; applied only when it parses to a whole number in range.</summary>
    public string CheckSecondsText
    {
        get => _checkSecondsText;
        set
        {
            if (!SetProperty(ref _checkSecondsText, value)) return;
            bool valid = int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int seconds)
                && seconds is >= MinCheckSeconds and <= MaxCheckSeconds;
            IsCheckSecondsInvalid = !valid;
            if (valid) CheckSeconds = seconds;
        }
    }

    [ObservableProperty]
    public partial bool IsCheckSecondsInvalid { get; set; }

    public string CheckLengthHint { get; }

    /// <summary>When the field loses focus, show the value in effect again (discarding an invalid entry).</summary>
    public void CommitCheckSecondsText()
    {
        _checkSecondsText = CheckSeconds.ToString(CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(CheckSecondsText));
        IsCheckSecondsInvalid = false;
    }

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
