using System.Security;

namespace AllMyTasks.Core.Autostart;

/// <summary>
/// Applies the "launch at login" settings to the OS entry. Settings stay the source of truth; the entry is
/// written from them, so <see cref="Apply"/> can also be called at startup to repair an entry that points at a
/// moved executable.
/// </summary>
public sealed class AutostartManager
{
    private readonly IAutostart? _backend;
    private readonly string? _executablePath;
    private readonly string? _dataDirectory;

    /// <param name="backend">The OS entry, or null on a platform without one.</param>
    /// <param name="executablePath">What to start at login, or null when this run has no suitable executable.</param>
    /// <param name="dataDirectory">The <c>--data-dir</c> this run was started with, passed on to the login launch.</param>
    public AutostartManager(IAutostart? backend, string? executablePath, string? dataDirectory)
    {
        _backend = backend;
        _executablePath = executablePath;
        _dataDirectory = dataDirectory;
        UnsupportedReason = backend is null
            ? "Launching at login is not supported on this operating system."
            : executablePath is null
                ? "Not available when the app is started through the dotnet host. Use an installed or published build."
                : null;
    }

    /// <summary>Why the setting cannot be used, or null when it can.</summary>
    public string? UnsupportedReason { get; }

    public bool IsSupported => UnsupportedReason is null;

    /// <summary>The manager for the current OS and process.</summary>
    public static AutostartManager ForCurrentProcess(string? dataDirectory)
    {
        IAutostart? backend = null;
        if (OperatingSystem.IsWindows())
        {
            backend = new WindowsAutostart();
        }
        else if (OperatingSystem.IsMacOS())
        {
            backend = new MacAutostart(MacAutostart.DefaultDirectory());
        }
        else if (OperatingSystem.IsLinux())
        {
            backend = new LinuxAutostart(LinuxAutostart.DefaultDirectory());
        }

        var exe = AutostartLaunch.ResolveExecutable(Environment.ProcessPath, Environment.GetEnvironmentVariable("APPIMAGE"));
        return new AutostartManager(backend, exe, dataDirectory);
    }

    /// <summary>
    /// Creates or removes the login entry. Returns null on success, or a message for the user when the OS
    /// refused (for example a read-only home folder or a policy-locked registry key).
    /// </summary>
    public string? Apply(bool launchAtLogin, bool startInTray)
    {
        if (_backend is null || _executablePath is null)
        {
            return launchAtLogin ? UnsupportedReason : null;
        }

        try
        {
            if (launchAtLogin)
            {
                _backend.Enable(AutostartLaunch.Create(_executablePath, startInTray, _dataDirectory));
            }
            else
            {
                _backend.Disable();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Could not {(launchAtLogin ? "set up" : "remove")} the login entry: {ex.Message}";
        }
    }
}
