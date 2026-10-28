using AllMyTasks.Core.Model;

namespace AllMyTasks.Core.Changes;

public enum PendingKind
{
    Complete,
    Reopen,
}

public enum PendingState
{
    /// <summary>Waiting for the next sync of its source to be sent.</summary>
    Queued,

    /// <summary>The source refused it for good. Stays until the user retries or discards it.</summary>
    Failed,
}

/// <summary>A change the user made that has not reached the source yet.</summary>
/// <param name="Id">Identity of the change; also what a provider may use to make a retry idempotent.</param>
/// <param name="Key">The task it applies to.</param>
/// <param name="Kind">What to do.</param>
/// <param name="CreatedAt">When the user made it; changes of one source are sent in this order.</param>
/// <param name="Attempts">How many times sending it was tried.</param>
/// <param name="State">Queued or failed.</param>
/// <param name="Error">Why it failed, for the user. Must not contain credentials.</param>
public sealed record PendingChange(
    Guid Id,
    ItemKey Key,
    PendingKind Kind,
    DateTimeOffset CreatedAt,
    int Attempts = 0,
    PendingState State = PendingState.Queued,
    string? Error = null)
{
    public ItemKey Key { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Key);
}
