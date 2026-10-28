using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;

namespace AllMyTasks.Core.Changes;

/// <summary>Raised when the queue changed. <see cref="NeedsPush"/> is true when a source should sync to send it.</summary>
public sealed class TaskChangeEventArgs(string sourceId, bool needsPush) : EventArgs
{
    public string SourceId { get; } = sourceId;

    public bool NeedsPush { get; } = needsPush;
}

/// <summary>What the views need to know about changes: which tasks can be changed and which changes are waiting.</summary>
public sealed class TaskChangeSnapshot(IReadOnlyList<PendingChange> pending, IReadOnlySet<(string SourceId, string CollectionId)> writable)
{
    public static TaskChangeSnapshot Empty { get; } = new([], new HashSet<(string, string)>());

    public IReadOnlyList<PendingChange> Pending { get; } = pending;

    /// <summary>Whether tasks in the key's collection can be completed and reopened.</summary>
    public bool CanChange(ItemKey key) => writable.Contains((key.SourceId, key.CollectionId));

    public PendingChange? PendingFor(ItemKey key) => Pending.LastOrDefault(c => c.Key == key);

    /// <summary>Changes waiting to be sent (not the failed ones).</summary>
    public int QueuedCount => Pending.Count(c => c.State == PendingState.Queued);

    public int FailedCount => Pending.Count(c => c.State == PendingState.Failed);
}

/// <summary>Complete and reopen tasks. Changes show at once and are sent to the source on its next sync.</summary>
public interface ITaskChangeService
{
    event EventHandler<TaskChangeEventArgs>? Changed;

    Task<TaskChangeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues completing the task. Throws <see cref="InvalidOperationException"/> if its source does not allow changes.</summary>
    Task CompleteAsync(ItemKey key, CancellationToken cancellationToken = default);

    /// <summary>Queues reopening the task. Throws <see cref="InvalidOperationException"/> if its source does not allow changes.</summary>
    Task ReopenAsync(ItemKey key, CancellationToken cancellationToken = default);

    /// <summary>Gives up a change; the task shows what the source last said.</summary>
    Task DiscardAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Queues a failed change again.</summary>
    Task RetryAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITaskChangeService"/>
public sealed class TaskChangeService(IPendingChangeStore store, ISourceRepository repository, ISourceProvider sources, TimeProvider clock) : ITaskChangeService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<TaskChangeEventArgs>? Changed;

    public async Task<TaskChangeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var pending = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var writable = new HashSet<(string, string)>();
        var collections = await repository.GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await IsWritableAsync(source, cancellationToken).ConfigureAwait(false))
            {
                foreach (var collection in collections.Where(c => c.SourceId == source.Id && c.Kind == CollectionKind.TaskList && !c.IsReadOnly))
                {
                    writable.Add((source.Id, collection.Id));
                }
            }
        }

        return new TaskChangeSnapshot(pending, writable);
    }

    public void Dispose() => _gate.Dispose();

    public Task CompleteAsync(ItemKey key, CancellationToken cancellationToken = default) => EnqueueAsync(key, PendingKind.Complete, cancellationToken);

    public Task ReopenAsync(ItemKey key, CancellationToken cancellationToken = default) => EnqueueAsync(key, PendingKind.Reopen, cancellationToken);

    public async Task DiscardAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var change = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == id);
            if (change is null)
            {
                return;
            }

            await store.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, new TaskChangeEventArgs(change.Key.SourceId, needsPush: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var change = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Id == id);
            if (change is null || change.State != PendingState.Failed)
            {
                return;
            }

            await store.SaveAsync(change with { State = PendingState.Queued, Attempts = 0, Error = null }, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, new TaskChangeEventArgs(change.Key.SourceId, needsPush: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnqueueAsync(ItemKey key, PendingKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var info = (await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Id == key.SourceId);
        var collection = (await repository.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.SourceId == key.SourceId && c.Id == key.CollectionId);
        if (info is null || collection is null or { IsReadOnly: true } || !await IsWritableAsync(info, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("This source does not allow changes.");
        }

        var needsPush = true;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false)).Where(c => c.Key == key).ToList();
            if (existing.Any(c => c.Kind == kind && c.State == PendingState.Queued))
            {
                return;
            }

            // The opposite change is cancelled by this one, so tick then untick sends nothing (the source never saw the
            // first one, even if it failed). A failed change of the same kind is replaced by a fresh attempt.
            foreach (var old in existing)
            {
                await store.RemoveAsync(old.Id, cancellationToken).ConfigureAwait(false);
            }

            if (existing.Any(c => c.Kind != kind))
            {
                needsPush = false;
            }
            else
            {
                await store.SaveAsync(new PendingChange(Guid.NewGuid(), key, kind, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, new TaskChangeEventArgs(key.SourceId, needsPush));
    }

    private async Task<bool> IsWritableAsync(SourceInfo info, CancellationToken cancellationToken)
    {
        try
        {
            return await sources.GetAsync(info, cancellationToken).ConfigureAwait(false) is IWritableTaskSource;
        }
        catch (SourceException)
        {
            // A source this build cannot create (unknown kind) is simply not writable.
            return false;
        }
    }
}
