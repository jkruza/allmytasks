using System.Globalization;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.GitLab;

/// <summary>
/// A GitLab account on gitlab.com or a self-managed instance (kind <c>gitlab</c>). It shows open issues assigned to the
/// user and open merge requests assigned to the user or waiting for their review. Each project with at least one such
/// item is a task list, named by its path. Read-only.
/// GitLab lists these across projects, not per project, while the engine syncs per collection. So one fetch of the three
/// lists is shared by every collection asking within a short time, and each sync is a snapshot of its project: an item that
/// was closed, unassigned or reassigned simply is not in the lists any more.
/// </summary>
public sealed class GitLabSource : SourceBase, ITaskSource, IDisposable
{
    public const string KindName = "gitlab";

    internal static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(2);

    private readonly GitLabClient _client;
    private readonly GitLabMapper _mapper;
    private readonly Lock _gate = new();
    private Memo? _memo;

    internal GitLabSource(string id, string displayName, GitLabClient client, ILogger? logger = null, TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _mapper = new GitLabMapper(id);
    }

    public void Dispose() => _client.Dispose();

    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await FetchAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. all.GroupBy(m => m.ProjectId)
                .Select(g => new CollectionInfo(Id, g.Key, g.First().ProjectName, CollectionKind.TaskList, IsVisible: true, IsReadOnly: true))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var all = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var mine = all.Where(m => m.ProjectId == collection.Id).ToList();
        var tasks = mine.Select(m => m.Task).OfType<TaskItem>().ToList();
        var warnings = mine.Where(m => m.Task is null).Select(m => new SyncWarning(m.ItemId, "An item could not be read and was skipped.")).ToList();
        var state = new SyncState(TimeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        return SyncResult.Snapshot(tasks, state, warnings, confirmedEmpty: true);
    }

    /// <summary>One shared fetch per short period; a failed one is not remembered, so the next attempt asks again.</summary>
    private Task<IReadOnlyList<Mapped>> FetchAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<Mapped>> task;
        lock (_gate)
        {
            var now = TimeProvider.GetUtcNow();
            if (_memo is null || _memo.Expires <= now || _memo.Result.IsFaulted || _memo.Result.IsCanceled)
            {
                // The request is not cancelled by one caller leaving; the client's own timeout bounds it.
                _memo = new Memo(RetryAsync(ReadAllAsync, cancellationToken: CancellationToken.None), now + MemoLifetime);
            }

            task = _memo.Result;
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Mapped>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var user = await _client.GetUsernameAsync(cancellationToken).ConfigureAwait(false);
        var issues = await _client.GetListAsync("issues", "scope=assigned_to_me&state=opened", cancellationToken).ConfigureAwait(false);
        var assigned = await _client.GetListAsync("merge_requests", "scope=assigned_to_me&state=opened", cancellationToken).ConfigureAwait(false);

        // The global merge request list defaults to the ones I created; scope=all is needed for the reviewer filter.
        var reviewing = await _client.GetListAsync("merge_requests", $"scope=all&state=opened&reviewer_username={Uri.EscapeDataString(user)}", cancellationToken).ConfigureAwait(false);
        var reviewIds = reviewing.Select(r => r.Id).ToHashSet();

        var result = new List<Mapped>();
        foreach (var issue in issues.DistinctBy(i => i.Id))
        {
            if (_mapper.MapIssue(issue) is { } mapped)
            {
                result.Add(mapped);
            }
        }

        foreach (var mergeRequest in assigned.Concat(reviewing).DistinctBy(m => m.Id))
        {
            if (_mapper.MapMergeRequest(mergeRequest, reviewIds.Contains(mergeRequest.Id)) is { } mapped)
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private sealed record Memo(Task<IReadOnlyList<Mapped>> Result, DateTimeOffset Expires);
}
