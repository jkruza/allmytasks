namespace AllMyTasks.Core.Startup;

/// <summary>Options read from the command line that change how the app starts.</summary>
public sealed record StartupOptions
{
    public const string TrayOptionName = "--tray";
    public const string VersionOptionName = "--version";

    /// <summary>Start with only the tray icon; the main window is shown on request.</summary>
    public bool StartInTray { get; init; }

    /// <summary>Print the version and exit without starting the UI (used by the packaging smoke tests).</summary>
    public bool ShowVersion { get; init; }

    /// <summary>Reads the options from the command line. Unknown arguments are ignored.</summary>
    public static StartupOptions FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new StartupOptions
        {
            StartInTray = args.Contains(TrayOptionName, StringComparer.Ordinal),
            ShowVersion = args.Contains(VersionOptionName, StringComparer.Ordinal),
        };
    }
}
