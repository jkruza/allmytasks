using System.Text;

namespace AllMyTasks.Core.Autostart;

/// <summary>Builds the command line stored in the Windows Run key. Platform-neutral so it can be tested anywhere.</summary>
public static class WindowsCommandLine
{
    /// <summary>The command line stored in the registry: the program always quoted, arguments quoted when needed.</summary>
    public static string Build(AutostartLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return string.Join(' ', new[] { Quote(launch.ExecutablePath, always: true) }.Concat(launch.Arguments.Select(a => Quote(a, always: false))));
    }

    private static string Quote(string argument, bool always)
    {
        if (!always && argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        // Backslashes only need doubling in front of a quote (an embedded one or the closing one).
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }
}
