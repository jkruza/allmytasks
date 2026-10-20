namespace AllMyTasks.Core.Providers;

/// <summary>A problem with one item that did not stop the sync, such as one malformed event in a feed.</summary>
/// <param name="ItemId">The provider's id for the item, if it could be determined.</param>
/// <param name="Message">What was wrong. Must not contain credentials or item content.</param>
public sealed record SyncWarning(string? ItemId, string Message);

/// <summary>What changed in one collection since the previous sync.</summary>
/// <typeparam name="T"><see cref="Calendar.CalendarEvent"/> or <see cref="Tasks.TaskItem"/>.</typeparam>
/// <param name="Upserts">New or changed items.</param>
/// <param name="DeletedItemIds">Ids of items removed since the previous state.</param>
/// <param name="NewState">Pass this back as <c>previous</c> on the next sync.</param>
/// <param name="IsFullSnapshot">True when anything not in <paramref name="Upserts"/> no longer exists.</param>
/// <param name="Warnings">Per-item problems; the UI shows "N items skipped".</param>
public sealed record SyncResult<T>(
    IReadOnlyList<T> Upserts,
    IReadOnlyList<string> DeletedItemIds,
    SyncState NewState,
    bool IsFullSnapshot,
    IReadOnlyList<SyncWarning>? Warnings = null)
{
    public IReadOnlyList<T> Upserts { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Upserts);

    public IReadOnlyList<string> DeletedItemIds { get; init => field = Guard.NotNull(value); } = Guard.NotNull(DeletedItemIds);

    public SyncState NewState { get; init => field = Guard.NotNull(value); } = Guard.NotNull(NewState);

    public IReadOnlyList<SyncWarning> Warnings { get; init => field = value ?? []; } = Warnings ?? [];
}

/// <summary>Factory methods for <see cref="SyncResult{T}"/>.</summary>
public static class SyncResult
{
    /// <summary>A full snapshot: the engine deletes cached items that are not in <paramref name="items"/>.</summary>
    public static SyncResult<T> Snapshot<T>(IReadOnlyList<T> items, SyncState newState, IReadOnlyList<SyncWarning>? warnings = null) =>
        new(items, [], newState, IsFullSnapshot: true, warnings);

    /// <summary>A delta: only the listed items changed or were deleted.</summary>
    public static SyncResult<T> Delta<T>(
        IReadOnlyList<T> upserts,
        IReadOnlyList<string> deletedItemIds,
        SyncState newState,
        IReadOnlyList<SyncWarning>? warnings = null) =>
        new(upserts, deletedItemIds, newState, IsFullSnapshot: false, warnings);
}
