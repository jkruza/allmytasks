using AllMyTasks.Core.Startup;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Autostart;

/// <summary>The program and arguments the OS runs at login.</summary>
public sealed record AutostartLaunch(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Builds the login command. <paramref name="startInTray"/> adds <c>--tray</c>; <paramref name="dataDirectory"/>
    /// (the <c>--data-dir</c> this run was started with, if any) is passed on so the login launch uses the same data.
    /// </summary>
    public static AutostartLaunch Create(string executablePath, bool startInTray, string? dataDirectory)
    {
        var args = new List<string>();
        if (startInTray)
        {
            args.Add(StartupOptions.TrayOptionName);
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            args.Add(DataDirectory.OptionName);
            args.Add(Path.GetFullPath(dataDirectory));
        }

        return new AutostartLaunch(executablePath, args);
    }

    /// <summary>
    /// The file the OS should start, or null when there is none worth registering: an AppImage is started through
    /// the <c>APPIMAGE</c> path (the process path points into a temporary mount), and a run through the
    /// <c>dotnet</c> host (for example <c>dotnet run</c>) has no executable of its own.
    /// </summary>
    public static string? ResolveExecutable(string? processPath, string? appImagePath)
    {
        if (!string.IsNullOrWhiteSpace(appImagePath))
        {
            return appImagePath;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : processPath;
    }
}
