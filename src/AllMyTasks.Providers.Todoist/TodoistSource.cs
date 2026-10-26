using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Providers.Todoist;

/// <summary>
/// A Todoist account (kind <c>todoist</c>): every active project is a task list and the tasks are read-only.
/// Todoist syncs per account while the engine syncs per collection, so a collection's state is the account's
/// sync token it was last synced from, and one reply is shared by every collection asking from the same token.
/// </summary>
public sealed class TodoistSource : SourceBase, ITaskSource, IDisposable
{
    public const string KindName = "todoist";

    internal const int MemoLimit = 4;
    private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(2);

    private readonly TodoistClient _client;
    private readonly TodoistMapper _mapper;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemoEntry> _memo = [];

    internal TodoistSource(
        string id,
        string displayName,
        TodoistClient client,
        TimeZoneInfo displayZone,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
        : base(id, KindName, displayName, logger, timeProvider)
    {
        _client = client;
        _mapper = new TodoistMapper(id, displayZone);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Lists the projects with a projects-only full sync, which is small and always the complete list.</summary>
    public override async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _client.SyncAsync("*", TodoistClient.ProjectsOnly, cancellationToken).ConfigureAwait(false);
        return [.. reply.Projects.Select(_mapper.MapProject).OfType<CollectionInfo>()];
    }

    public async Task<SyncResult<TaskItem>> SyncTasksAsync(CollectionInfo collection, SyncState? previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var isFirst = previous is null;
        var reply = await ReplyFromAsync(previous?.Token ?? "*", cancellationToken).ConfigureAwait(false);
        if (!isFirst && reply.FullSync)
        {
            throw new SyncStateExpiredException("Todoist answered with a full sync to an incremental request.");
        }

        var tasks = new List<TaskItem>();
        var deleted = new List<string>();
        var warnings = new List<SyncWarning>();
        foreach (var item in reply.Items)
        {
            if (item.Id is not { Length: > 0 } itemId)
            {
                continue;
            }

            // An item that moved to another project shows up there with its new project only; for this collection it
            // is gone, and deleting an id the cache never held does nothing.
            if (item.IsDeleted || item.ProjectId != collection.Id)
            {
                deleted.Add(itemId);
            }
            else if (_mapper.MapItem(item) is { } task)
            {
                tasks.Add(task);
            }
            else
            {
                warnings.Add(new SyncWarning(itemId, "A task could not be read and was skipped."));
            }
        }

        var state = new SyncState(reply.SyncToken);
        return isFirst
            ? SyncResult.Snapshot(tasks, state, warnings, confirmedEmpty: true)
            : SyncResult.Delta(tasks, [.. deleted.Distinct()], state, warnings);
    }

    /// <summary>
    /// One request per distinct token: collections synced from the same token share the reply. The request itself is
    /// not cancelled by one caller leaving; the client's own timeout bounds it.
    /// </summary>
    private Task<SyncReply> ReplyFromAsync(string token, CancellationToken cancellationToken)
    {
        Task<SyncReply> task;
        lock (_gate)
        {
            var now = TimeProvider.GetUtcNow();
            foreach (var expired in _memo.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
            {
                _memo.Remove(expired);
            }

            if (!_memo.TryGetValue(token, out var entry) || entry.Reply.IsFaulted || entry.Reply.IsCanceled)
            {
                if (_memo.Count >= MemoLimit && !_memo.ContainsKey(token))
                {
                    _memo.Remove(_memo.MinBy(e => e.Value.Expires).Key);
                }

                var started = _client.SyncAsync(token, TodoistClient.ProjectsAndItems, CancellationToken.None);
                entry = new MemoEntry(started, now + MemoLifetime);
                _memo[token] = entry;
                _ = started.ContinueWith(t => Forget(token, t), CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }

            task = entry.Reply;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>A failed request is not remembered, so the next attempt asks again.</summary>
    private void Forget(string token, Task<SyncReply> failed)
    {
        _ = failed.Exception;
        lock (_gate)
        {
            if (_memo.TryGetValue(token, out var entry) && ReferenceEquals(entry.Reply, failed))
            {
                _memo.Remove(token);
            }
        }
    }

    private sealed record MemoEntry(Task<SyncReply> Reply, DateTimeOffset Expires);
}
