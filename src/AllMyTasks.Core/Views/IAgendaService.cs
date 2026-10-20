namespace AllMyTasks.Core.Views;

/// <summary>The one query every calendar view starts from.</summary>
public interface IAgendaService
{
    /// <summary>
    /// Returns events (recurrences expanded) and dated tasks in the range, from visible collections only,
    /// sorted by start, then all-day first, then title.
    /// </summary>
    Task<IReadOnlyList<AgendaEntry>> GetEntriesAsync(ViewQuery query, CancellationToken cancellationToken = default);
}
