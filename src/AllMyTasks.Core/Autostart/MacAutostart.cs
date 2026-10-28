using System.Security;
using System.Text;

namespace AllMyTasks.Core.Autostart;

/// <summary>A launchd user agent: <c>~/Library/LaunchAgents/com.jkruza.allmytasks.plist</c> with <c>RunAtLoad</c>.</summary>
public sealed class MacAutostart(string launchAgentsDirectory) : IAutostart
{
    /// <summary>The agent's launchd label, also its file name without <c>.plist</c>.</summary>
    public const string Label = "com.jkruza.allmytasks";

    private string EntryPath => Path.Combine(launchAgentsDirectory, Label + ".plist");

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public bool IsEnabled() => File.Exists(EntryPath);

    public void Enable(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        Directory.CreateDirectory(launchAgentsDirectory);
        File.WriteAllText(EntryPath, Render(launch), new UTF8Encoding(false));
    }

    public void Disable()
    {
        if (File.Exists(EntryPath))
        {
            File.Delete(EntryPath);
        }
    }

    /// <summary>The contents of the property list.</summary>
    public static string Render(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var arguments = string.Concat(new[] { launch.ExecutablePath }.Concat(launch.Arguments)
            .Select(a => $"    <string>{SecurityElement.Escape(a)}</string>\n"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
            + "<plist version=\"1.0\">\n<dict>\n"
            + $"  <key>Label</key>\n  <string>{Label}</string>\n"
            + $"  <key>ProgramArguments</key>\n  <array>\n{arguments}  </array>\n"
            + "  <key>RunAtLoad</key>\n  <true/>\n"
            + "</dict>\n</plist>\n";
    }
}
