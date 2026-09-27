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
        Assert.Equal([15, 30, 45, 60, 120, 240, 480], settings.LongCaptureLengths.Select(c => c.Value));
        Assert.Equal(["15 min", "30 min", "45 min", "1 h", "2 h", "4 h", "8 h"], settings.LongCaptureLengths.Select(c => c.Label));
        Assert.Equal(15, settings.LongCaptureMinutes);
    }

    [Fact]
    public void Long_capture_duration_offers_minutes_and_hours_and_rejects_unknown_values()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var settings = new SettingsViewModel(Localizer.Default, culture, longCaptureMinutes: 240);
        var legacy = new SettingsViewModel(Localizer.Default, culture, longCaptureMinutes: 90);

        Assert.Equal(240, settings.LongCaptureMinutes);
        Assert.Equal(45, new SettingsViewModel(Localizer.Default, culture, longCaptureMinutes: 45).LongCaptureMinutes);
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

    [Fact]
    public void Default_targets_fill_only_empty_symptoms_with_real_services()
    {
        var settings = NewSettings();
        settings.SetTarget(Symptom.Gaming, "game.example:3074");
        settings.ApplyDefaultTargets();

        Assert.Equal("game.example:3074", settings.TargetFor(Symptom.Gaming)); // the user's own server is kept
        Assert.Equal("www.primevideo.com:443", settings.TargetFor(Symptom.Video));
        Assert.Equal("discord.com:443", settings.TargetFor(Symptom.Calls));
        Assert.Equal("www.microsoft.com:443", settings.TargetFor(Symptom.Disconnects));
        Assert.All(Enum.GetValues<Symptom>(), symptom =>
        {
            var preset = ConnectionClue.Presentation.Diagnostics.SymptomServices.DefaultTarget(symptom);
            Assert.True(ConnectionClue.Presentation.Diagnostics.ServiceTargetParser.TryParse(
                ConnectionClue.Presentation.Diagnostics.SymptomServices.DefaultTargetText(symptom), out var parsed));
            Assert.Equal(preset.Target, parsed);
            // A default adds a service; it never repeats a built-in one.
            Assert.DoesNotContain(ConnectionClue.Presentation.Diagnostics.SymptomServices.For(symptom), s => s.Target.Host == preset.Target.Host);
        });
    }

    [Fact]
    public void Check_length_hint_states_the_real_duration_with_and_without_the_speed_test()
    {
        var settings = NewSettings();
        settings.CheckSecondsText = "10";
        Assert.Equal("Measures delay for 10 seconds, then runs the speed test: about 26 seconds in all. The services for your symptom are tested right after.",
            settings.CheckTotalHint);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.MeasureSpeed = false;
        Assert.Contains(nameof(SettingsViewModel.CheckTotalHint), changed);
        Assert.Equal("Measures delay for 10 seconds. The services for your symptom are tested right after.", settings.CheckTotalHint);
    }

    private static SettingsViewModel NewSettings()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        return new SettingsViewModel(Localizer.Default, culture, backgroundEnabled: false);
    }
}
