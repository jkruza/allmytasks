using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.GitHub;

/// <summary>
/// A GitHub account (kind <c>github</c>): the open issues and pull requests assigned to the user and the open pull
/// requests awaiting their review, one task list per repository. Always read-only.
/// GitHub has no change feed for these queries, so every sync is a full snapshot of the whole result; the engine syncs
/// per collection, so one fetch is shared by every collection asking within a short time.
/// </summary>
public sealed class GitHubSource : SourceBase, ITaskSource, IDisposable
{
    public const string KindName = "github";

    private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(2);

    private readonly GitHubClient _client;
    private readonly GitHubMapper _mapper;
    private readonly Lock _gate = new();
    private Task<Fetched>? _memo;
    private DateTimeOffset _memoExpires;

    internal GitHubSource(string id, string displayName, GitHubClient client, ILogger? logger = null, TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _mapper = new GitHubMapper(id);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The repositories that have at least one item for the user.</summary>
    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. fetched.Items
                .Select(i => i.Repository)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(repository => new CollectionInfo(Id, repository, repository, CollectionKind.TaskList, IsVisible: true, IsReadOnly: true)),
        ];
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var tasks = fetched.Items.Where(i => string.Equals(i.Repository, collection.Id, StringComparison.Ordinal)).Select(i => i.Task).ToList();
        var warnings = fetched.Skipped.Select(id => new SyncWarning(id, "An item could not be read and was skipped.")).ToList();

        // An item that closed or lost its assignment is simply missing from the snapshot, so the engine removes it.
        return SyncResult.Snapshot(tasks, new SyncState(TimeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)), warnings, confirmedEmpty: true);
    }

    /// <summary>
    /// One fetch for all collections: the request is not cancelled by one caller leaving (the client's own timeout bounds
    /// it), and a failed fetch is not remembered, so the next attempt asks again.
    /// </summary>
    private Task<Fetched> FetchAsync(CancellationToken cancellationToken)
    {
        Task<Fetched> task;
        lock (_gate)
        {
            var now = TimeProvider.GetUtcNow();
            if (_memo is null || _memo.IsFaulted || _memo.IsCanceled || _memoExpires <= now)
            {
                var started = LoadAsync();
                _memo = started;
                _memoExpires = now + MemoLifetime;
                _ = started.ContinueWith(t => Forget(t), CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }

            task = _memo;
        }

        return task.WaitAsync(cancellationToken);
    }

    private void Forget(Task<Fetched> failed)
    {
        _ = failed.Exception;
        lock (_gate)
        {
            if (ReferenceEquals(_memo, failed))
            {
                _memo = null;
            }
        }
    }

    private async Task<Fetched> LoadAsync()
    {
        var assigned = await _client.GetAssignedAsync(CancellationToken.None).ConfigureAwait(false);
        var reviews = await _client.GetReviewRequestsAsync(CancellationToken.None).ConfigureAwait(false);
        var reviewIds = reviews.Select(r => r.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var items = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        var skipped = new List<string?>();
        foreach (var issue in assigned.Concat(reviews))
        {
            if (issue.Id is { Length: > 0 } id && items.ContainsKey(id))
            {
                continue;
            }

            if (_mapper.Map(issue, issue.Id is not null && reviewIds.Contains(issue.Id)) is { } item)
            {
                items[issue.Id!] = item;
            }
            else
            {
                skipped.Add(issue.Id);
            }
        }

        return new Fetched([.. items.Values], skipped);
    }

    private sealed record Fetched(IReadOnlyList<WorkItem> Items, IReadOnlyList<string?> Skipped);
}
