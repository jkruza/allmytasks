namespace AllMyTasks.Core.Reminders;

/// <summary>What the user did with a notification.</summary>
public enum NotificationAction
{
    /// <summary>Clicked the notification, or its Open button.</summary>
    Open,
    Snooze,
    Dismiss,
}

/// <summary>Raised when the user acts on a notification.</summary>
public sealed class NotificationActivatedEventArgs(Reminder reminder, NotificationAction action) : EventArgs
{
    public Reminder Reminder { get; } = reminder;

    public NotificationAction Action { get; } = action;
}

/// <summary>A notifier cannot show notifications on this machine (no daemon, no program). The caller may try another one.</summary>
public sealed class NotifierUnavailableException : Exception
{
    public NotifierUnavailableException()
    {
    }

    public NotifierUnavailableException(string message)
        : base(message)
    {
    }

    public NotifierUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Shows a reminder to the user, one implementation per delivery mechanism.</summary>
public interface INotifier
{
    /// <summary>True when Snooze and Dismiss can be offered on the notification itself.</summary>
    bool SupportsActions { get; }

    /// <summary>Raised on any thread when the user clicks, snoozes or dismisses.</summary>
    event EventHandler<NotificationActivatedEventArgs>? Activated;

    /// <summary>Shows the notification and returns once it is on screen, not once it is closed.</summary>
    /// <exception cref="NotifierUnavailableException">This mechanism is not available here.</exception>
    Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default);
}

/// <summary>Uses <paramref name="primary"/> and falls back to <paramref name="fallback"/> when the primary is unavailable.</summary>
public sealed class FallbackNotifier : INotifier
{
    private readonly INotifier _primary;
    private readonly INotifier _fallback;
    private volatile bool _primaryUnavailable;

    public FallbackNotifier(INotifier primary, INotifier fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _primary.Activated += (_, e) => Activated?.Invoke(this, e);
        _fallback.Activated += (_, e) => Activated?.Invoke(this, e);
    }

    public event EventHandler<NotificationActivatedEventArgs>? Activated;

    public bool SupportsActions => _primaryUnavailable ? _fallback.SupportsActions : _primary.SupportsActions;

    public async Task ShowAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        if (!_primaryUnavailable)
        {
            try
            {
                await _primary.ShowAsync(reminder, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (NotifierUnavailableException)
            {
                _primaryUnavailable = true;
            }
        }

        await _fallback.ShowAsync(reminder, cancellationToken).ConfigureAwait(false);
    }
}
