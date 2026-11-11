namespace AllMyTasks.Core.Sync;

/// <summary>What kind of failure ended a sync. A short, fixed vocabulary: never the provider's own message.</summary>
public enum ErrorCategory
{
    /// <summary>The sync succeeded.</summary>
    None,

    /// <summary>The network or the server could not be reached, or the server failed.</summary>
    Offline,

    /// <summary>The credentials were rejected or have expired.</summary>
    Authentication,

    /// <summary>The server asked the app to slow down (it sent a retry-after time).</summary>
    RateLimit,

    /// <summary>The server answered, but the answer could not be used.</summary>
    ServerError,

    /// <summary>Something the provider should have handled itself.</summary>
    Unexpected,
}

/// <summary>
/// Facts about the last sync of one source in this run: when it started, how long it took, the failure category, and how
/// many collections were read in full and how many as a delta since the last sync.
/// </summary>
public sealed record SyncAttempt(DateTimeOffset StartedAt, TimeSpan Duration, ErrorCategory Category, int FullCollections, int DeltaCollections)
{
    public bool Succeeded => Category == ErrorCategory.None;
}
