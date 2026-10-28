using AllMyTasks.Core.Autostart;
using AllMyTasks.Core.Settings;

namespace AllMyTasks.Core.Tests;

public sealed class AutostartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amt-autostart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Launch_adds_tray_only_when_asked_and_passes_the_data_directory_on()
    {
        Assert.Empty(AutostartLaunch.Create("/app/amt", startInTray: false, dataDirectory: null).Arguments);
        Assert.Equal(["--tray"], AutostartLaunch.Create("/app/amt", true, null).Arguments);
        var args = AutostartLaunch.Create("/app/amt", true, "/data").Arguments;
        Assert.Equal(["--tray", "--data-dir", Path.GetFullPath("/data")], args);
    }

    [Theory]
    [InlineData("/usr/lib/dotnet/dotnet", null, null)]
    [InlineData("/opt/dotnet/DOTNET.exe", null, null)]
    [InlineData(null, null, null)]
    [InlineData("/opt/amt/AllMyTasks", null, "/opt/amt/AllMyTasks")]
    [InlineData("/tmp/.mount_x/AllMyTasks", "/home/me/AllMyTasks.AppImage", "/home/me/AllMyTasks.AppImage")]
    public void ResolveExecutable_picks_the_file_to_start(string? process, string? appImage, string? expected) =>
        Assert.Equal(expected, AutostartLaunch.ResolveExecutable(process, appImage));

    [Fact]
    public void Windows_command_line_quotes_the_program_and_arguments_with_spaces()
    {
        var launch = new AutostartLaunch("C:\\Program Files\\All My Tasks\\AllMyTasks.exe", ["--tray", "--data-dir", "D:\\my data\\"]);
        Assert.Equal("\"C:\\Program Files\\All My Tasks\\AllMyTasks.exe\" --tray --data-dir \"D:\\my data\\\\\"", WindowsCommandLine.Build(launch));
    }

    [Fact]
    public void Linux_entry_runs_the_program_with_its_arguments()
    {
        var text = LinuxAutostart.Render(new AutostartLaunch("/opt/amt/AllMyTasks", ["--tray"]));
        Assert.Contains("Exec=/opt/amt/AllMyTasks --tray\n", text, StringComparison.Ordinal);
        Assert.Contains("Type=Application", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/plain", "/opt/plain")]
    [InlineData("/opt/my app/amt", "\"/opt/my app/amt\"")]
    [InlineData("/opt/100%/amt", "/opt/100%%/amt")]
    [InlineData("/opt/$x/amt", "\"/opt/\\\\$x/amt\"")]
    [InlineData("/opt/a\\b", "\"/opt/a\\\\\\\\b\"")]
    public void Linux_exec_arguments_are_quoted_and_escaped(string argument, string expected) =>
        Assert.Equal(expected, LinuxAutostart.QuoteExecArgument(argument));

    [Fact]
    public void Mac_agent_runs_at_load_and_escapes_xml()
    {
        var text = MacAutostart.Render(new AutostartLaunch("/Applications/A&B/AllMyTasks", ["--tray"]));
        Assert.Contains("<string>/Applications/A&amp;B/AllMyTasks</string>", text, StringComparison.Ordinal);
        Assert.Contains("<string>--tray</string>", text, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>\n  <true/>", text, StringComparison.Ordinal);
        Assert.Contains("<string>com.jkruza.allmytasks</string>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void File_backends_create_replace_and_remove_the_entry()
    {
        foreach (IAutostart backend in new IAutostart[] { new LinuxAutostart(Path.Combine(_dir, "l", "autostart")), new MacAutostart(Path.Combine(_dir, "m")) })
        {
            Assert.False(backend.IsEnabled());
            backend.Disable();

            backend.Enable(new AutostartLaunch("/a/amt", []));
            Assert.True(backend.IsEnabled());
            backend.Enable(new AutostartLaunch("/b/amt", ["--tray"]));
            Assert.True(backend.IsEnabled());

            backend.Disable();
            Assert.False(backend.IsEnabled());
        }
    }

    [Fact]
    public void Manager_enables_with_tray_only_when_the_suboption_is_on_and_disables()
    {
        var backend = new FakeAutostart();
        var manager = new AutostartManager(backend, "/a/amt", null);

        Assert.Null(manager.Apply(launchAtLogin: true, startInTray: false));
        Assert.Empty(backend.Launch!.Arguments);

        Assert.Null(manager.Apply(true, true));
        Assert.Equal(["--tray"], backend.Launch!.Arguments);

        Assert.Null(manager.Apply(false, true));
        Assert.Null(backend.Launch);
    }

    [Fact]
    public void Manager_reports_a_refusal_instead_of_throwing()
    {
        var manager = new AutostartManager(new FakeAutostart { Fail = new UnauthorizedAccessException("denied") }, "/a/amt", null);
        Assert.Contains("denied", manager.Apply(true, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Manager_without_an_executable_or_backend_is_unsupported()
    {
        var noExe = new AutostartManager(new FakeAutostart(), null, null);
        Assert.False(noExe.IsSupported);
        Assert.NotNull(noExe.Apply(true, false));
        Assert.Null(noExe.Apply(false, false));

        var noBackend = new AutostartManager(null, "/a/amt", null);
        Assert.False(noBackend.IsSupported);
        Assert.NotNull(noBackend.UnsupportedReason);
    }

    [Fact]
    public void Settings_default_off_and_round_trip()
    {
        var defaults = AppSettings.Parse(null);
        Assert.False(defaults.LaunchAtLogin);
        Assert.False(defaults.StartInTrayAtLogin);

        var back = AppSettings.Parse(new AppSettings { LaunchAtLogin = true, StartInTrayAtLogin = true }.ToJson());
        Assert.True(back.LaunchAtLogin);
        Assert.True(back.StartInTrayAtLogin);
        Assert.False(AppSettings.Parse("{\"launchAtLogin\":\"yes\"}").LaunchAtLogin);
    }

    private sealed class FakeAutostart : IAutostart
    {
        public AutostartLaunch? Launch { get; private set; }

        public Exception? Fail { get; init; }

        public bool IsEnabled() => Launch is not null;

        public void Enable(AutostartLaunch launch)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Launch = launch;
        }

        public void Disable() => Launch = null;
    }
}
