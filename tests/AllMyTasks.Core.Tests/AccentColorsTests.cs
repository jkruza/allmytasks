using AllMyTasks.Core.Settings;

namespace AllMyTasks.Core.Tests;

public class AccentColorsTests
{
    [Theory]
    [InlineData(null, "#3b82f6")]
    [InlineData("", "#3b82f6")]
    [InlineData("nonsense", "#3b82f6")]
    [InlineData("blue", "#3b82f6")]
    [InlineData("GREEN", "#10b981")]
    [InlineData("purple", "#8b5cf6")]
    public void Resolve_maps_a_choice_to_its_colour_and_unknown_to_blue(string? id, string expected) =>
        Assert.Equal(expected, AccentColors.Resolve(id, "#112233"));

    [Fact]
    public void System_choice_uses_the_platform_colour()
    {
        Assert.Equal("#112233", AccentColors.Resolve("system", "#112233"));
        Assert.Equal("#112233", AccentColors.Resolve("system", "112233"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a colour")]
    public void System_choice_falls_back_to_blue_without_a_usable_platform_colour(string? systemHex) =>
        Assert.Equal(AccentColors.BlueHex, AccentColors.Resolve("system", systemHex));

    [Fact]
    public void Settings_round_trip_the_accent_and_ignore_unknown_values()
    {
        var saved = new AppSettings { Accent = "teal" };
        Assert.Equal("teal", AppSettings.Parse(saved.ToJson()).Accent);
        Assert.Equal("blue", AppSettings.Parse("""{"accent":"chartreuse"}""").Accent);
        Assert.Equal("blue", AppSettings.Parse("{}").Accent);
    }
}
