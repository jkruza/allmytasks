using AllMyTasks.Core.Model;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Diagnostics;

/// <summary>One calendar or list of a source, described without its name: only kind, visibility and how many items are cached.</summary>
public sealed record CollectionDiagnostics(CollectionKind Kind, bool IsVisible, int Items);

/// <summary>
/// What is known about the health of one source. Holds nothing that identifies the user or the content: no display name,
/// user name, address (only the host name of a self-hosted server), tenant, project, item title or provider message.
/// </summary>
/// <param name="ShortId">A short generated id that is the same across restarts, so reports of one source can be told apart.</param>
/// <param name="Kind">Provider kind, such as <c>todoist</c>.</param>
/// <param name="Host">Host name of the server, for self-hosted kinds only (GitLab, CalDAV).</param>
/// <param name="LastAttempt">The last sync in this run, or null if none ran yet.</param>
public sealed record SourceDiagnostics(
    string ShortId,
    string Kind,
    SourceStatusKind Status,
    DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures,
    DateTimeOffset? NextRetryAt,
    int SkippedItems,
    SyncAttempt? LastAttempt,
    string? Host,
    IReadOnlyList<CollectionDiagnostics> Collections)
{
    /// <summary>"error category" shown in the report: the failure category of the last attempt, or derived from the saved status after a restart.</summary>
    public ErrorCategory Category => LastAttempt?.Category ?? Status switch
    {
        SourceStatusKind.NeedsAttention => ErrorCategory.Authentication,
        SourceStatusKind.Offline => ErrorCategory.Offline,
        SourceStatusKind.Error => ErrorCategory.ServerError,
        _ => ErrorCategory.None,
    };

    /// <summary>"full", "delta", "mixed" or null when no sync ran in this run.</summary>
    public string? Strategy => LastAttempt switch
    {
        null or { FullCollections: 0, DeltaCollections: 0 } => null,
        { DeltaCollections: 0 } => "full",
        { FullCollections: 0 } => "delta",
        _ => "mixed",
    };
}

/// <summary>Where the report was made: app version, operating system and runtime.</summary>
public sealed record ReportEnvironment(string AppVersion, string OperatingSystem, string Runtime);
