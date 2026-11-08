using AllMyTasks.Core.Settings;

namespace AllMyTasks.Core.Tests;

public class DetailsPaneWidthTests
{
    [Theory]
    [InlineData(340, 0, 340)]
    [InlineData(100, 0, 260)]
    [InlineData(900, 0, 640)]
    [InlineData(500, 1400, 500)]
    [InlineData(500, 800, 440)]
    [InlineData(500, 500, 260)]
    [InlineData(300, -1, 300)]
    public void Clamp_keeps_the_width_within_limits(double width, double window, double expected) =>
        Assert.Equal(expected, DetailsPaneWidth.Clamp(width, window));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Clamp_gives_the_default_for_bad_values(double width) =>
        Assert.Equal(DetailsPaneWidth.Default, DetailsPaneWidth.Clamp(width));
}
