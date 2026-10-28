using AllMyTasks.Core.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllMyTasks.Core.Reminders;

/// <summary>
/// Owns the clock for reminders: plans a window, sleeps until the next reminder is due (one timer), delivers what
/// is due and records it in the <see cref="IReminderLog"/> so nothing is shown twice. See docs/design/reminders.md.
/// </summary>
public sealed partial class ReminderScheduler : IDisposable
{
    /// <summary>Reminders that became due this long ago or less are still delivered after a start or a sleep.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(15);

    /// <summary>How far ahead reminders are planned.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(36);

    /// <summary>The longest the timer sleeps, so a wake from system sleep or a clock change is noticed within this time.</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(2);

    /// <summary>More than this many skipped reminders are announced with one summary notification.</summary>
    internal const int MissedSummaryThreshold = 3;

    private static readonly TimeSpan LookBackLimit = TimeSpan.FromHours(24);
    private static readonly TimeSpan KeepAfterEnd = TimeSpan.FromDays(1);

    private readonly IAgendaService _agenda;
    private readonly IReminderLog _log;
    private readonly INotifier _notifier;
    private readonly Func<ReminderSettings> _settings;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;
    private int _disposed;

    public ReminderScheduler(
        IAgendaService agenda,
        IReminderLog log,
        INotifier notifier,
        Func<ReminderSettings> settings,
        TimeZoneInfo displayZone,
        TimeProvider clock,
        ILogger? logger = null)
    {
        _agenda = agenda;
        _log = log;
        _notifier = notifier;
        _settings = settings;
        _zone = displayZone;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _notifier.Activated += OnActivated;
    }

    /// <summary>The user asked to open the item of a reminder. Raised on any thread.</summary>
    public event EventHandler<Reminder>? OpenRequested;

    /// <summary>When the timer is set to fire next, or null before the first <see cref="TickAsync"/>.</summary>
    public DateTimeOffset? NextWake { get; private set; }

    /// <summary>Starts the timer and runs the first check (the catch-up after a start).</summary>
    public async Task StartAsync()
    {
        _timer ??= _clock.CreateTimer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await TickAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Re-plans and delivers whatever is due, then sets the timer for the next reminder. Called by the timer, and
    /// by the app after a sync that changed items and after a settings change. Never throws.
    /// </summary>
    public async Task TickAsync()
    {
        try
        {
            await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await CheckAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed check must not stop reminders for good: try again at the next wake.
            LogCheckFailed(_logger, ex);
            Arm(_clock.GetUtcNow() + MaxSleep);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _notifier.Activated -= OnActivated;
        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var settings = _settings();
        var now = _clock.GetUtcNow();
        if (!settings.Enabled)
        {
            // Nothing is due while off; coming back on starts from now rather than reporting the gap as missed.
            await _log.SetLastCheckAsync(now, ct).ConfigureAwait(false);
            Arm(now + MaxSleep);
            return;
        }

        var lastCheck = await _log.GetLastCheckAsync(ct).ConfigureAwait(false);
        var entries = await _log.GetAllAsync(ct).ConfigureAwait(false);
        var logged = entries.ToDictionary(e => e.Reminder.Id);

        // Look back as far as the last check (bounded), so reminders that came due while the app was not running are
        // seen and counted, and back to the oldest snooze, so a snoozed reminder is still validated against the item.
        var from = now - CatchUp;
        if (lastCheck is { } last && last < from)
        {
            from = last < now - LookBackLimit ? now - LookBackLimit : last;
        }

        var snoozed = entries.Where(e => e.State == ReminderState.Snoozed).ToList();
        if (snoozed.Count > 0)
        {
            from = new[] { from, snoozed.Min(e => e.Reminder.FireAt) }.Min();
        }

        var to = now + Horizon;
        var planned = ReminderPlanner.Plan(await LoadAsync(from, to, ct).ConfigureAwait(false), settings, _zone, from, to);

        var due = new List<Reminder>();
        var skipped = new List<Reminder>();
        DateTimeOffset? next = null;
        foreach (var reminder in planned)
        {
            logged.TryGetValue(reminder.Id, out var entry);
            switch (entry)
            {
                case { State: ReminderState.Shown or ReminderState.Dismissed }:
                    break;
                case { State: ReminderState.Snoozed, SnoozedUntil: { } until }:
                    if (until <= now)
                    {
                        due.Add(reminder);
                    }
                    else
                    {
                        next = Earliest(next, until);
                    }

                    break;
                case null when reminder.FireAt > now:
                    next = Earliest(next, reminder.FireAt);
                    break;
                case null when reminder.EndsAt <= now:
                    // The event is over: nothing to be reminded of, and nothing was missed either.
                    await _log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Dismissed), ct).ConfigureAwait(false);
                    break;
                case null when now - reminder.FireAt <= CatchUp:
                    due.Add(reminder);
                    break;
                default:
                    skipped.Add(reminder);
                    break;
            }
        }

        foreach (var reminder in skipped)
        {
            await _log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Dismissed), ct).ConfigureAwait(false);
        }

        // Log first, then show: a crash in between loses one notification instead of repeating it on every start.
        foreach (var reminder in due)
        {
            await _log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Shown), ct).ConfigureAwait(false);
            await ShowAsync(reminder, ct).ConfigureAwait(false);
        }

        if (skipped.Count > MissedSummaryThreshold)
        {
            await ShowAsync(Reminder.Missed(skipped.Count, now), ct).ConfigureAwait(false);
        }

        await _log.SetLastCheckAsync(now, ct).ConfigureAwait(false);
        await _log.PruneAsync(now - KeepAfterEnd, ct).ConfigureAwait(false);
        Arm(next ?? now + MaxSleep);
    }

    private async Task<IReadOnlyList<AgendaEntry>> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, _zone).DateTime).AddDays(2);
        return await _agenda.GetEntriesAsync(new ViewQuery(first, last, _zone, new ViewFilter(ShowEvents: true, ShowTasks: true, ShowCompleted: false)), ct).ConfigureAwait(false);
    }

    private async Task ShowAsync(Reminder reminder, CancellationToken ct)
    {
        try
        {
            await _notifier.ShowAsync(reminder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Already logged as shown, so a notifier that keeps failing cannot make this repeat every few minutes.
            LogShowFailed(_logger, reminder.Kind, ex);
        }
    }

    private void Arm(DateTimeOffset wake)
    {
        var now = _clock.GetUtcNow();
        var latest = now + MaxSleep;
        var target = wake > latest ? latest : wake;
        var delay = target - now;
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }

        NextWake = now + delay;
        _timer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } c && c < candidate ? c : candidate;

    private void OnActivated(object? sender, NotificationActivatedEventArgs e) => _ = HandleAsync(e);

    private async Task HandleAsync(NotificationActivatedEventArgs e)
    {
        var reminder = e.Reminder;
        try
        {
            switch (e.Action)
            {
                case NotificationAction.Open:
                    OpenRequested?.Invoke(this, reminder);
                    break;
                case NotificationAction.Dismiss when reminder.CanSnooze:
                    await _log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Dismissed), _stopping.Token).ConfigureAwait(false);
                    break;
                case NotificationAction.Snooze when reminder.CanSnooze:
                    var until = _clock.GetUtcNow() + _settings().SnoozeLength;
                    await _log.SaveAsync(new ReminderLogEntry(reminder, ReminderState.Snoozed, until), _stopping.Token).ConfigureAwait(false);
                    await TickAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogActionFailed(_logger, e.Action, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking reminders failed; trying again at the next wake.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Showing a {Kind} reminder failed.")]
    private static partial void LogShowFailed(ILogger logger, ReminderKind kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling the {Action} action on a reminder failed.")]
    private static partial void LogActionFailed(ILogger logger, NotificationAction action, Exception exception);
}
