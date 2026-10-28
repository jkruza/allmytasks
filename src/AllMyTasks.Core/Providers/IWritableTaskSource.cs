using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Providers;

/// <summary>What a provider did with a write.</summary>
public enum WriteOutcome
{
    /// <summary>The change was made.</summary>
    Applied,

    /// <summary>The task already had the requested state; the user's intent is met.</summary>
    AlreadyInState,

    /// <summary>The task no longer exists in the source.</summary>
    NotFound,
}

/// <param name="Outcome">What happened. Failures are not outcomes: they throw a <see cref="SourceException"/>.</param>
public sealed record WriteResult(WriteOutcome Outcome);

/// <summary>
/// A task source that can complete and reopen tasks. A source that is read-only, or whose owner did not allow
/// changes, does not implement this, and the UI then shows no checkbox for its tasks.
/// </summary>
public interface IWritableTaskSource : ITaskSource
{
    /// <summary>Marks the task done. <paramref name="changeId"/> makes a retry of the same change idempotent where the provider supports it.</summary>
    Task<WriteResult> CompleteAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default);

    /// <summary>Marks the task open again.</summary>
    Task<WriteResult> ReopenAsync(ItemKey key, Guid changeId, CancellationToken cancellationToken = default);
}
