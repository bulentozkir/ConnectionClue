using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Theming;
using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.Presentation.Tests;

public sealed class SettingsFeatureTests
{
    [Fact]
    public void New_settings_are_opt_in_and_theme_stays_dark_by_default()
    {
        var settings = NewSettings();
        Assert.False(settings.StartWithWindows);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(0, settings.PlanDownloadMbps);
        Assert.Equal(0, settings.PlanUploadMbps);
        Assert.Equal("", settings.GamingTarget);
        Assert.Equal([60, 120, 240, 480], settings.LongCaptureLengths.Select(c => c.Value));
        Assert.Equal(["1 h", "2 h", "4 h", "8 h"], settings.LongCaptureLengths.Select(c => c.Label));
        Assert.Equal(SettingsViewModel.DefaultLongCaptureMinutes, settings.LongCaptureMinutes);
    }

    [Fact]
    public void Long_capture_duration_uses_hour_choices_and_rejects_legacy_values()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var settings = new SettingsViewModel(Localizer.Default, culture, longCaptureMinutes: 240);
        var legacy = new SettingsViewModel(Localizer.Default, culture, longCaptureMinutes: 15);

        Assert.Equal(240, settings.LongCaptureMinutes);
        Assert.Equal(SettingsViewModel.DefaultLongCaptureMinutes, legacy.LongCaptureMinutes);
    }

    [Fact]
    public void Plan_speed_inputs_accept_blank_or_positive_finite_values()
    {
        var settings = NewSettings();
        settings.PlanDownloadText = "300";
        settings.PlanUploadText = "35.5";
        Assert.Equal(300, settings.PlanDownloadMbps);
        Assert.Equal(35.5, settings.PlanUploadMbps);
        Assert.False(settings.IsPlanSpeedInvalid);

        settings.PlanDownloadText = "";
        Assert.Equal(0, settings.PlanDownloadMbps);
        Assert.False(settings.IsPlanSpeedInvalid);
    }

    [Fact]
    public void Invalid_plan_speed_is_not_applied_and_commit_restores_only_invalid_input()
    {
        var settings = NewSettings();
        settings.PlanDownloadText = "300";
        settings.PlanUploadText = "35";
        settings.PlanDownloadText = "-1";
        Assert.True(settings.IsPlanSpeedInvalid);
        Assert.Equal(300, settings.PlanDownloadMbps);
        settings.PlanUploadText = "40";
        Assert.True(settings.IsPlanSpeedInvalid);
        settings.CommitPlanSpeedText();

        Assert.Equal("300", settings.PlanDownloadText);
        Assert.Equal("40", settings.PlanUploadText);
        Assert.False(settings.IsPlanSpeedInvalid);
        Assert.Equal(300, settings.PlanDownloadMbps);
        Assert.Equal(40, settings.PlanUploadMbps);
    }

    [Fact]
    public void Symptom_targets_are_kept_separately()
    {
        var settings = NewSettings();
        settings.SetTarget(Symptom.Gaming, "game.example:443");
        settings.SetTarget(Symptom.Video, "video.example:443");
        settings.SetTarget(Symptom.Calls, "call.example:443");
        settings.SetTarget(Symptom.Disconnects, "router.example:443");

        Assert.Equal("game.example:443", settings.TargetFor(Symptom.Gaming));
        Assert.Equal("video.example:443", settings.TargetFor(Symptom.Video));
        Assert.Equal("call.example:443", settings.TargetFor(Symptom.Calls));
        Assert.Equal("router.example:443", settings.TargetFor(Symptom.Disconnects));
    }

    private static SettingsViewModel NewSettings()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        return new SettingsViewModel(Localizer.Default, culture, backgroundEnabled: false);
    }
}
