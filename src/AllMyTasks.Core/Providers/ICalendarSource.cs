using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Providers;

/// <summary>A source that serves calendar events.</summary>
public interface ICalendarSource : ISource
{
    /// <summary>
    /// Returns what changed in <paramref name="collection"/> since <paramref name="previous"/>.
    /// A null <paramref name="previous"/> means first sync and yields a full snapshot.
    /// Throws <see cref="SyncStateExpiredException"/> if <paramref name="previous"/> is no longer valid.
    /// </summary>
    Task<SyncResult<CalendarEvent>> SyncEventsAsync(
        CollectionInfo collection,
        SyncState? previous,
        CancellationToken cancellationToken = default);
}
