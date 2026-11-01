using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Jira;

/// <summary>
/// A Jira Cloud site (kind <c>jira</c>): the open issues assigned to the signed-in user, with each Jira project that has
/// at least one of them as a read-only task list. Jira has no change feed, and the list of one person's open issues is
/// small, so every sync is a full snapshot; an issue that was resolved or reassigned simply stops being listed.
/// The engine syncs per collection while the search covers the whole site, so one reply is shared by the collections that
/// ask within a short time.
/// </summary>
public sealed class JiraSource : SourceBase, ITaskSource, IDisposable
{
    public const string KindName = "jira";

    private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(2);

    private readonly JiraClient _client;
    private readonly JiraMapper _mapper;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<IssueDto>>? _memo;
    private DateTimeOffset _memoExpires;

    internal JiraSource(string id, string displayName, JiraClient client, Uri site, ILogger? logger = null, TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _mapper = new JiraMapper(id, site);
    }

    public void Dispose() => _client.Dispose();

    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var issues = await IssuesAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. issues
                .Select(i => _mapper.MapProject(i.Fields?.Project))
                .OfType<CollectionInfo>()
                .DistinctBy(c => c.Id)
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var issues = await IssuesAsync(cancellationToken).ConfigureAwait(false);
        var projectOfIssue = issues
            .Where(i => i is { Id.Length: > 0, Fields.Project.Id.Length: > 0 })
            .DistinctBy(i => i.Id)
            .ToDictionary(i => i.Id!, i => i.Fields!.Project!.Id!);

        var tasks = new List<TaskItem>();
        var warnings = new List<SyncWarning>();
        foreach (var issue in issues.Where(i => i.Fields?.Project?.Id == collection.Id))
        {
            if (_mapper.MapIssue(issue, projectOfIssue) is { } task)
            {
                tasks.Add(task);
            }
            else
            {
                warnings.Add(new SyncWarning(issue.Id, "An issue could not be read and was skipped."));
            }
        }

        // The token only has to be a valid, changing position; it is when this snapshot was taken.
        var state = new SyncState(TimeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return SyncResult.Snapshot<TaskItem>([.. tasks.DistinctBy(t => t.Key)], state, warnings, confirmedEmpty: true);
    }

    private Task<IReadOnlyList<IssueDto>> IssuesAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<IssueDto>> task;
        lock (_gate)
        {
            var now = TimeProvider.GetUtcNow();
            if (_memo is null || _memo.IsFaulted || _memo.IsCanceled || _memoExpires <= now)
            {
                // The request is not cancelled by one caller leaving; the client's own timeout bounds it.
                _memo = _client.SearchAsync(CancellationToken.None);
                _memoExpires = now + MemoLifetime;
                var started = _memo;
                _ = started.ContinueWith(t => Forget(t), CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }

            task = _memo;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>A failed request is not remembered, so the next attempt asks again.</summary>
    private void Forget(Task<IReadOnlyList<IssueDto>> failed)
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
}
