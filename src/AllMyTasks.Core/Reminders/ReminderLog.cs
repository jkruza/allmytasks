namespace AllMyTasks.Core.Reminders;

/// <summary>What happened to a reminder.</summary>
public enum ReminderState
{
    /// <summary>Delivered; will not be delivered again.</summary>
    Shown,

    /// <summary>Waiting for <see cref="ReminderLogEntry.SnoozedUntil"/>, then delivered once more.</summary>
    Snoozed,

    /// <summary>Dismissed by the user, or skipped because it was too old to be useful.</summary>
    Dismissed,
}

/// <summary>One row of the reminder log. Planning skips every reminder that has a row, which prevents duplicates.</summary>
/// <param name="Reminder">The reminder as delivered, kept whole so a snoozed one can be shown again without re-reading the item.</param>
/// <param name="State">Shown, snoozed or dismissed.</param>
/// <param name="SnoozedUntil">Set while <paramref name="State"/> is <see cref="ReminderState.Snoozed"/>.</param>
public sealed record ReminderLogEntry(Reminder Reminder, ReminderState State, DateTimeOffset? SnoozedUntil = null);

/// <summary>Remembers which reminders were delivered, across restarts.</summary>
public interface IReminderLog
{
    Task<IReadOnlyList<ReminderLogEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the row for the entry's reminder.</summary>
    Task SaveAsync(ReminderLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes rows whose reminder stopped being relevant before <paramref name="endedBefore"/>.</summary>
    Task PruneAsync(DateTimeOffset endedBefore, CancellationToken cancellationToken = default);

    /// <summary>When the scheduler last looked, or null if it never did. Anything due since then was missed.</summary>
    Task<DateTimeOffset?> GetLastCheckAsync(CancellationToken cancellationToken = default);

    Task SetLastCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>A log kept in memory, for tests.</summary>
public sealed class InMemoryReminderLog : IReminderLog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ReminderId, ReminderLogEntry> _entries = [];
    private DateTimeOffset? _lastCheck;

    public Task<IReadOnlyList<ReminderLogEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ReminderLogEntry>>([.. _entries.Values]);
        }
    }

    public Task SaveAsync(ReminderLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[entry.Reminder.Id] = entry;
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTimeOffset endedBefore, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Where(e => e.Value.Reminder.EndsAt < endedBefore).Select(e => e.Key).ToList())
            {
                _entries.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLastCheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_lastCheck);
        }
    }

    public Task SetLastCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _lastCheck = at;
        }

        return Task.CompletedTask;
    }
}
