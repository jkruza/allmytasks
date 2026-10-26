using AllMyTasks.Core.Startup;

namespace AllMyTasks.Core.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void No_arguments_start_normally() => Assert.False(StartupOptions.FromArguments([]).StartInTray);

    [Fact]
    public void Tray_argument_starts_in_the_tray() => Assert.True(StartupOptions.FromArguments(["--tray"]).StartInTray);

    [Fact]
    public void Tray_argument_works_next_to_other_options() =>
        Assert.True(StartupOptions.FromArguments(["--data-dir", "/tmp/x", "--tray", "--other"]).StartInTray);

    [Theory]
    [InlineData("--TRAY")]
    [InlineData("-tray")]
    [InlineData("--tray=true")]
    public void Only_the_exact_option_counts(string arg) => Assert.False(StartupOptions.FromArguments([arg]).StartInTray);
}
