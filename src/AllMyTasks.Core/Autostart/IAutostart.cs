namespace AllMyTasks.Core.Autostart;

/// <summary>The per-user "run at login" entry of one operating system.</summary>
public interface IAutostart
{
    /// <summary>Whether this app's login entry exists.</summary>
    bool IsEnabled();

    /// <summary>Creates the login entry, or replaces it when it already exists.</summary>
    void Enable(AutostartLaunch launch);

    /// <summary>Removes the login entry. Does nothing when there is none.</summary>
    void Disable();
}
