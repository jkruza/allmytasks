using System.Text;

namespace AllMyTasks.Core.Autostart;

/// <summary>An XDG autostart entry: <c>$XDG_CONFIG_HOME/autostart/allmytasks.desktop</c>.</summary>
public sealed class LinuxAutostart(string autostartDirectory) : IAutostart
{
    internal const string FileName = "allmytasks.desktop";

    private string EntryPath => Path.Combine(autostartDirectory, FileName);

    /// <summary>The user's autostart directory, honouring <c>XDG_CONFIG_HOME</c>.</summary>
    public static string DefaultDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "autostart");
    }

    public bool IsEnabled() => File.Exists(EntryPath);

    public void Enable(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        Directory.CreateDirectory(autostartDirectory);
        File.WriteAllText(EntryPath, Render(launch), new UTF8Encoding(false));
    }

    public void Disable()
    {
        if (File.Exists(EntryPath))
        {
            File.Delete(EntryPath);
        }
    }

    /// <summary>The contents of the <c>.desktop</c> file.</summary>
    public static string Render(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var exec = string.Join(' ', new[] { launch.ExecutablePath }.Concat(launch.Arguments).Select(QuoteExecArgument));
        return $"[Desktop Entry]\nType=Application\nName=All My Tasks\nComment=Calendars and tasks from many providers\nExec={exec}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>
    /// Quotes one word of an <c>Exec</c> line: double quotes around anything with a reserved character, a backslash
    /// before <c>" ` $ \</c>, then every backslash doubled because the file format unescapes the value first, and
    /// <c>%</c> doubled so it is not read as a field code.
    /// </summary>
    internal static string QuoteExecArgument(string argument)
    {
        var text = argument.Replace("%", "%%", StringComparison.Ordinal);
        if (text.Length > 0 && text.IndexOfAny([' ', '\t', '\n', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0)
        {
            return text;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c == '\\')
            {
                quoted.Append("\\\\\\\\");
            }
            else
            {
                if (c is '"' or '`' or '$')
                {
                    quoted.Append("\\\\");
                }

                quoted.Append(c);
            }
        }

        return quoted.Append('"').ToString();
    }
}
