namespace AllMyTasks.Core.Updates;

/// <summary>A published release: its version, its release page, and whether it is marked as a prerelease.</summary>
public sealed record ReleaseInfo(string Version, string Url, bool Prerelease);

public enum ReleaseCheckStatus
{
    /// <summary>The release list was read.</summary>
    Ok,

    /// <summary>The server said the list is unchanged since the given ETag.</summary>
    NotModified,

    /// <summary>Offline, rate limited, or any other failure. Never shown to the user.</summary>
    Failed,
}

public sealed record ReleaseCheckResult(ReleaseCheckStatus Status, IReadOnlyList<ReleaseInfo> Releases, string? ETag)
{
    public static ReleaseCheckResult Failed { get; } = new(ReleaseCheckStatus.Failed, [], null);

    public static ReleaseCheckResult NotModified { get; } = new(ReleaseCheckStatus.NotModified, [], null);
}

/// <summary>Reads the list of published releases. The real implementation makes one anonymous HTTPS request.</summary>
public interface IReleaseChecker
{
    /// <summary>Asks for the releases, sending <paramref name="etag"/> as a conditional request when it is not null. Never throws for network problems.</summary>
    Task<ReleaseCheckResult> CheckAsync(string? etag, CancellationToken cancellationToken = default);
}
