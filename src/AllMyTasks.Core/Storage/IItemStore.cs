using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Core.Storage;

/// <summary>
/// The local cache of events and tasks. Providers never see it; the sync engine writes to it and
/// views read from it.
/// </summary>
public interface IItemStore
{
    /// <summary>Applies one sync result to a collection atomically.</summary>
    Task ApplyAsync(string sourceId, string collectionId, SyncResult<CalendarEvent> result, CancellationToken cancellationToken = default);

    /// <summary>Applies one sync result to a collection atomically.</summary>
    Task ApplyAsync(string sourceId, string collectionId, SyncResult<TaskItem> result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every event that may show up in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>): single
    /// events that overlap it, plus all rows of recurring series (masters and their overrides) whose
    /// series could reach it. Callers expand recurrences and do the exact filtering.
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventCandidatesAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo displayZone,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all cached tasks.</summary>
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default);
}
