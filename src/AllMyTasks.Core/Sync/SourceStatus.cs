namespace AllMyTasks.Core.Sync;

public enum SourceStatusKind
{
    /// <summary>Never synced.</summary>
    New,

    /// <summary>Last sync succeeded.</summary>
    Ok,

    /// <summary>A sync is running now.</summary>
    Syncing,

    /// <summary>The network or server is unreachable. Cached data is kept; retried with backoff.</summary>
    Offline,

    /// <summary>The last sync failed for another reason. Cached data is kept; retried with backoff.</summary>
    Error,

    /// <summary>Credentials were rejected. Nothing is retried until the user edits the source.</summary>
    NeedsAttention,
}

/// <summary>Where one source stands: shown in the UI and kept between runs, so stale data is never mistaken for fresh.</summary>
/// <param name="SourceId">The source.</param>
/// <param name="Kind">Current state.</param>
/// <param name="LastSyncedAt">When a sync last completed successfully; null if never.</param>
/// <param name="LastError">Short, content-free description of the last failure. Never contains credentials or URLs.</param>
/// <param name="Warning">A non-fatal note, such as a refused empty snapshot.</param>
/// <param name="SkippedItems">Items skipped in the last sync because they could not be read.</param>
/// <param name="ConsecutiveFailures">Failures since the last success, for backoff.</param>
/// <param name="NextRetryAt">When the next automatic attempt is due after a failure.</param>
public sealed record SourceStatus(
    string SourceId,
    SourceStatusKind Kind,
    DateTimeOffset? LastSyncedAt = null,
    string? LastError = null,
    string? Warning = null,
    int SkippedItems = 0,
    int ConsecutiveFailures = 0,
    DateTimeOffset? NextRetryAt = null)
{
    public string SourceId { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(SourceId);

    public static SourceStatus NewFor(string sourceId) => new(sourceId, SourceStatusKind.New);
}

/// <summary>Raised after a source's cache changed.</summary>
public sealed class SourceSyncedEventArgs(string sourceId) : EventArgs
{
    public string SourceId { get; } = sourceId;
}
