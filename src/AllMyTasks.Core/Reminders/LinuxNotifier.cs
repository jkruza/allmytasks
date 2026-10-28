using System.ComponentModel;
using System.Text;

namespace AllMyTasks.Core.Reminders;

/// <summary>
/// Desktop notifications on Linux through <c>notify-send</c> (libnotify), which talks to the desktop's
/// <c>org.freedesktop.Notifications</c> daemon. With libnotify 0.8 or newer the notification carries Open, Snooze
/// and Dismiss actions; with an older one it is shown without actions. No notify-send, or no daemon to talk to,
/// means <see cref="NotifierUnavailableException"/> and the caller falls back to the in-app window.
/// </summary>
public sealed class LinuxNotifier(IProcessLauncher launcher, TimeSpan? startupGrace = null) : INotifier
{
    internal const string Program = "notify-send";
    internal const string OpenAction = "default";
    internal const string SnoozeAction = "snooze";
    internal const string DismissAction = "dismiss";

    private readonly TimeSpan _grace = startupGrace ?? TimeSpan.FromSeconds(2);
    private bool? _actionsWork;

    public event EventHandler<NotificationActivatedEventArgs>? Activated;

    /// <summary>True until a <c>notify-send</c> without <c>--action</c> support is detected.</summary>
    public bool SupportsActions => _actionsWork ?? true;

    public async Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        if (_actionsWork != false && await TryStartAsync(Arguments(reminder, withActions: true), reminder, cancellationToken).ConfigureAwait(false))
        {
            _actionsWork = true;
            return;
        }

        if (_actionsWork is null)
        {
            _actionsWork = false;
        }

        if (!await TryStartAsync(Arguments(reminder, withActions: false), reminder, cancellationToken).ConfigureAwait(false))
        {
            throw new NotifierUnavailableException("notify-send could not show the notification (no notification daemon?).");
        }
    }

    /// <summary>The command line for a reminder. The text follows <c>--</c>, so a title that starts with a dash is not an option.</summary>
    internal static List<string> Arguments(Reminder reminder, bool withActions)
    {
        var arguments = new List<string> { "--app-name=All My Tasks", "--urgency=normal", "--category=reminder" };
        if (withActions)
        {
            arguments.Add($"--action={OpenAction}=Open");
            if (reminder.CanSnooze)
            {
                arguments.Add($"--action={SnoozeAction}=Snooze");
            }

            arguments.Add($"--action={DismissAction}=Dismiss");
            arguments.Add("--wait");
        }

        arguments.Add("--");
        arguments.Add(reminder.Title);
        if (!string.IsNullOrEmpty(reminder.Detail))
        {
            arguments.Add(EscapeMarkup(reminder.Detail));
        }

        return arguments;
    }

    /// <summary>The notification body may be rendered as a small markup language; text from calendars must stay text.</summary>
    internal static string EscapeMarkup(string text) =>
        new StringBuilder(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").ToString();

    internal static NotificationAction? ParseAction(string output) => output.Trim() switch
    {
        OpenAction => NotificationAction.Open,
        SnoozeAction => NotificationAction.Snooze,
        DismissAction => NotificationAction.Dismiss,
        _ => null,
    };

    /// <summary>
    /// Starts notify-send and waits briefly. A quick failure means it cannot be used this way; still running means
    /// the notification is on screen, and the process is watched in the background for the user's choice.
    /// </summary>
    private async Task<bool> TryStartAsync(List<string> arguments, Reminder reminder, CancellationToken cancellationToken)
    {
        Task<ProcessResult> run;
        try
        {
            run = launcher.RunAsync(Program, arguments, CancellationToken.None);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new NotifierUnavailableException("notify-send is not installed.", ex);
        }

        var finished = await Task.WhenAny(run, Task.Delay(_grace, cancellationToken)).ConfigureAwait(false);
        if (finished == run)
        {
            ProcessResult result;
            try
            {
                result = await run.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException)
            {
                throw new NotifierUnavailableException("notify-send is not installed.", ex);
            }

            if (result.ExitCode != 0)
            {
                return false;
            }

            Report(result.Output, reminder);
            return true;
        }

        _ = WatchAsync(run, reminder);
        return true;
    }

    private async Task WatchAsync(Task<ProcessResult> run, Reminder reminder)
    {
        try
        {
            var result = await run.ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                Report(result.Output, reminder);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // The program went away while the notification was open: nothing to report.
        }
    }

    private void Report(string output, Reminder reminder)
    {
        if (ParseAction(output) is { } action)
        {
            Activated?.Invoke(this, new NotificationActivatedEventArgs(reminder, action));
        }
    }
}
