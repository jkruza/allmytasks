namespace AllMyTasks.Core.Changes;

/// <summary>The queue of changes the user made that still have to be sent to their sources. Survives restarts.</summary>
public interface IPendingChangeStore
{
    /// <summary>All pending changes, oldest first.</summary>
    Task<IReadOnlyList<PendingChange>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds the change, or replaces the one with the same <see cref="PendingChange.Id"/>.</summary>
    Task SaveAsync(PendingChange change, CancellationToken cancellationToken = default);

    /// <summary>Removes a change; does nothing if it is already gone.</summary>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
