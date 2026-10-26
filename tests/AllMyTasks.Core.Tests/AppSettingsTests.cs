using System.Globalization;
using AllMyTasks.Core.Settings;

namespace AllMyTasks.Core.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData(WeekStartSetting.Sunday, "pl-PL", DayOfWeek.Sunday)]
    [InlineData(WeekStartSetting.Monday, "en-US", DayOfWeek.Monday)]
    [InlineData(WeekStartSetting.System, "en-US", DayOfWeek.Sunday)]
    [InlineData(WeekStartSetting.System, "pl-PL", DayOfWeek.Monday)]
    [InlineData(WeekStartSetting.System, "en-GB", DayOfWeek.Monday)]
    public void Resolve_uses_explicit_choice_or_the_culture(WeekStartSetting setting, string culture, DayOfWeek expected) =>
        Assert.Equal(expected, WeekStart.Resolve(setting, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void System_falls_back_to_Monday_when_the_culture_starts_on_another_day()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Saturday;
        Assert.Equal(DayOfWeek.Monday, WeekStart.Resolve(WeekStartSetting.System, culture));
    }

    [Theory]
    [InlineData(WeekStartSetting.System)]
    [InlineData(WeekStartSetting.Sunday)]
    [InlineData(WeekStartSetting.Monday)]
    public void Round_trips(WeekStartSetting setting)
    {
        var json = new AppSettings { WeekStart = setting }.ToJson();
        Assert.Equal(setting, AppSettings.Parse(json).WeekStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"weekStart\":\"Tuesday\"}")]
    [InlineData("{\"weekStart\":7}")]
    [InlineData("{\"weekStart\":\"7\"}")]
    public void Missing_or_invalid_values_give_the_default(string? json) =>
        Assert.Equal(WeekStartSetting.System, AppSettings.Parse(json).WeekStart);

    [Fact]
    public void Unknown_fields_are_ignored() =>
        Assert.Equal(WeekStartSetting.Monday, AppSettings.Parse("{\"weekStart\":\"monday\",\"theme\":\"dark\"}").WeekStart);

    [Fact]
    public void Close_to_tray_defaults_to_on() => Assert.True(new AppSettings().CloseToTray);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Close_to_tray_round_trips_with_the_week_start(bool closeToTray)
    {
        var parsed = AppSettings.Parse(new AppSettings { WeekStart = WeekStartSetting.Monday, CloseToTray = closeToTray }.ToJson());
        Assert.Equal(closeToTray, parsed.CloseToTray);
        Assert.Equal(WeekStartSetting.Monday, parsed.WeekStart);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"closeToTray\":\"false\"}")]
    [InlineData("{\"closeToTray\":0}")]
    [InlineData("{\"closeToTray\":null}")]
    public void Missing_or_non_boolean_close_to_tray_gives_the_default(string json) =>
        Assert.True(AppSettings.Parse(json).CloseToTray);
}
