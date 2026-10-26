namespace AllMyTasks.Core.Startup;

/// <summary>Options read from the command line that change how the app starts.</summary>
public sealed record StartupOptions
{
    public const string TrayOptionName = "--tray";

    /// <summary>Start with only the tray icon; the main window is shown on request.</summary>
    public bool StartInTray { get; init; }

    /// <summary>Reads the options from the command line. Unknown arguments are ignored.</summary>
    public static StartupOptions FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new StartupOptions { StartInTray = args.Contains(TrayOptionName, StringComparer.Ordinal) };
    }
}
