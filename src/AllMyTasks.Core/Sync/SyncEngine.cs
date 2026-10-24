using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllMyTasks.Core.Sync;

/// <summary>Tuning for <see cref="SyncEngine"/>.</summary>
/// <param name="MaxParallelSources">How many sources sync at once.</param>
/// <param name="EmptySnapshotGuardThreshold">
/// An unconfirmed empty full snapshot is refused when the collection already holds at least this many items.
/// </param>
public sealed record SyncOptions(int MaxParallelSources = 4, int EmptySnapshotGuardThreshold = 5);

/// <summary>
/// Pulls changes from every source into the local store and keeps per-source status. Providers never see the
/// store; the engine never sees provider wire formats.
/// </summary>
public sealed partial class SyncEngine(
    IItemStore store,
    ISourceRepository repository,
    ISourceProvider sources,
    TimeProvider clock,
    SyncOptions? options = null,
    ILogger? logger = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly SyncOptions _options = options ?? new SyncOptions();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _running = [];
    private readonly Dictionary<string, SourceStatus> _statuses = [];
    private readonly SemaphoreSlim _parallel = new(Math.Max(1, (options ?? new SyncOptions()).MaxParallelSources));
    private bool _loaded;

    /// <summary>Raised after a source's cached data changed (from any thread).</summary>
    public event EventHandler<SourceSyncedEventArgs>? SourceSynced;

    /// <summary>Raised whenever a source's status changes (from any thread).</summary>
    public event EventHandler<SourceStatus>? StatusChanged;

    /// <summary>True while any source is syncing.</summary>
    public bool IsSyncing
    {
        get
        {
            lock (_gate)
            {
                return _running.Count > 0;
            }
        }
    }

    /// <summary>The current status of each known source, loading the last persisted ones on first use.</summary>
    public async Task<IReadOnlyList<SourceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var infos = await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return [.. infos.Select(i => _statuses.TryGetValue(i.Id, out var s) ? s : SourceStatus.NewFor(i.Id))];
        }
    }

    /// <summary>Syncs every source, up to the parallel limit.</summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var infos = await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(infos.Select(i => SyncSourceAsync(i.Id, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// Syncs one source. If it is already syncing, joins that sync instead of starting another. Failures are
    /// recorded in the source's status, not thrown; only cancellation propagates.
    /// </summary>
    public Task SyncSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_running.TryGetValue(sourceId, out var running))
            {
                return running;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[sourceId] = completion.Task;
        }

        _ = RunExclusiveAsync(sourceId, completion, cancellationToken);
        return completion.Task;
    }

    /// <summary>
    /// Call after the user edited a source: clears "needs attention" and the backoff so it can sync again
    /// straight away.
    /// </summary>
    public async Task ResetAttentionAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        SourceStatus updated;
        lock (_gate)
        {
            var current = _statuses.TryGetValue(sourceId, out var s) ? s : SourceStatus.NewFor(sourceId);
            updated = current with { Kind = current.LastSyncedAt is null ? SourceStatusKind.New : SourceStatusKind.Ok, ConsecutiveFailures = 0, NextRetryAt = null, LastError = null, LastErrorDetail = null };
            _statuses[sourceId] = updated;
        }

        await repository.SaveSourceStatusAsync(updated, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, updated);
    }

    public void Dispose() => _parallel.Dispose();

    /// <summary>Forgets a source that was removed.</summary>
    public void Forget(string sourceId)
    {
        lock (_gate)
        {
            _statuses.Remove(sourceId);
        }
    }

    private async Task RunExclusiveAsync(string sourceId, TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SyncCoreAsync(sourceId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _parallel.Release();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Unregister before completing, so a caller that awaits this sync and immediately starts another gets a new one.
        lock (_gate)
        {
            _running.Remove(sourceId);
        }

        if (failure is OperationCanceledException)
        {
            completion.SetCanceled(cancellationToken);
        }
        else if (failure is not null)
        {
            completion.SetException(failure);
        }
        else
        {
            completion.SetResult();
        }
    }

    private async Task SyncCoreAsync(string sourceId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var info = (await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Id == sourceId);
        if (info is null)
        {
            return;
        }

        var previous = Current(sourceId);
        await SetStatusAsync(previous with { Kind = SourceStatusKind.Syncing }, persist: false, cancellationToken).ConfigureAwait(false);

        var skipped = 0;
        string? warning = null;
        var changed = false;
        try
        {
            var source = await sources.GetAsync(info, cancellationToken).ConfigureAwait(false);
            var offered = await source.GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
            await repository.SaveCollectionsAsync(info, offered, cancellationToken).ConfigureAwait(false);

            // Read back what was saved: it carries the user's visibility choice. Hidden collections are not synced.
            var collections = (await repository.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
                .Where(c => c.SourceId == sourceId && c.IsVisible);
            foreach (var collection in collections)
            {
                var outcome = await SyncCollectionAsync(source, collection, cancellationToken).ConfigureAwait(false);
                skipped += outcome.Skipped;
                warning ??= outcome.Warning;
                changed |= outcome.Changed;
            }

            var now = clock.GetUtcNow();
            await SetStatusAsync(
                new SourceStatus(sourceId, SourceStatusKind.Ok, now, null, warning, skipped, 0, null),
                persist: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SetStatusAsync(previous, persist: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (SourceAuthException ex)
        {
            // Retrying with the same credentials would only get the account locked. Wait for the user.
            const string Error = "The server rejected the credentials.";
            var detail = DetailFor(ex);
            LogSyncFailed(_logger, sourceId, info.Kind, Error, DiagnosticText.Describe(ex, includeMessage: true), detail);
            await SetStatusAsync(
                previous with { Kind = SourceStatusKind.NeedsAttention, LastError = Error, LastErrorDetail = detail, NextRetryAt = null },
                persist: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (SourceUnavailableException ex)
        {
            const string Error = "The server could not be reached.";
            LogSyncFailed(_logger, sourceId, info.Kind, Error, DiagnosticText.Describe(ex, includeMessage: true), DetailFor(ex));
            await FailAsync(previous, SourceStatusKind.Offline, Error, DetailFor(ex)).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            var error = Describe(ex);
            LogSyncFailed(_logger, sourceId, info.Kind, error, DiagnosticText.Describe(ex, includeMessage: true), DetailFor(ex));
            await FailAsync(previous, SourceStatusKind.Error, error, DetailFor(ex)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider that breaks its contract (throws something else) must not take the app down or stop the
            // other sources. The message is kept out of the status (it may contain private details); the type and
            // the first stack frames are shown, and the local log gets the message with addresses removed.
            const string Error = "The sync failed unexpectedly.";
            LogSyncFailed(_logger, sourceId, info.Kind, Error, DiagnosticText.Describe(ex, includeMessage: true), string.Empty);
            await FailAsync(previous, SourceStatusKind.Error, Error, DiagnosticText.Describe(ex, includeMessage: false)).ConfigureAwait(false);
        }
        finally
        {
            if (changed)
            {
                SourceSynced?.Invoke(this, new SourceSyncedEventArgs(sourceId));
            }
        }
    }

    private async Task<(int Skipped, string? Warning, bool Changed)> SyncCollectionAsync(
        ISource source,
        CollectionInfo collection,
        CancellationToken cancellationToken)
    {
        var previous = await repository.GetSyncStateAsync(collection.SourceId, collection.Id, cancellationToken).ConfigureAwait(false);

        if (collection.Kind == CollectionKind.Calendar && source is ICalendarSource calendar)
        {
            var result = await WithExpiryRetryAsync(
                state => calendar.SyncEventsAsync(collection, state, cancellationToken),
                previous).ConfigureAwait(false);
            return await ApplyAsync(
                collection,
                result,
                (r, ct) => store.ApplyAsync(collection.SourceId, collection.Id, r, ct),
                cancellationToken).ConfigureAwait(false);
        }

        if (collection.Kind == CollectionKind.TaskList && source is ITaskSource tasks)
        {
            var result = await WithExpiryRetryAsync(
                state => tasks.SyncTasksAsync(collection, state, cancellationToken),
                previous).ConfigureAwait(false);
            return await ApplyAsync(
                collection,
                result,
                (r, ct) => store.ApplyAsync(collection.SourceId, collection.Id, r, ct),
                cancellationToken).ConfigureAwait(false);
        }

        return (0, null, false);
    }

    private static async Task<SyncResult<T>> WithExpiryRetryAsync<T>(Func<SyncState?, Task<SyncResult<T>>> sync, SyncState? previous)
    {
        try
        {
            return await sync(previous).ConfigureAwait(false);
        }
        catch (SyncStateExpiredException) when (previous is not null)
        {
            // The server forgot our token: start over once, then let a second failure surface.
            return await sync(null).ConfigureAwait(false);
        }
    }

    private async Task<(int Skipped, string? Warning, bool Changed)> ApplyAsync<T>(
        CollectionInfo collection,
        SyncResult<T> result,
        Func<SyncResult<T>, CancellationToken, Task> apply,
        CancellationToken cancellationToken)
    {
        if (result.IsFullSnapshot && result.Upserts.Count == 0 && !result.ConfirmedEmpty)
        {
            var existing = await store.CountItemsAsync(collection.SourceId, collection.Id, cancellationToken).ConfigureAwait(false);
            if (existing >= _options.EmptySnapshotGuardThreshold)
            {
                // Likely an error page parsed as "no items". Keep the cache and the old state; say so.
                return (result.Warnings.Count, $"'{collection.DisplayName}' came back empty, so its {existing} cached items were kept.", false);
            }
        }

        foreach (var warning in result.Warnings)
        {
            LogItemWarning(_logger, collection.SourceId, collection.Id, warning.ItemId, warning.Message);
        }

        // The cache and the new state are written one after the other: a crash in between only means the next sync
        // repeats the same idempotent changes.
        await apply(result, cancellationToken).ConfigureAwait(false);
        await repository.SaveSyncStateAsync(collection.SourceId, collection.Id, result.NewState, cancellationToken).ConfigureAwait(false);
        var changed = result.IsFullSnapshot || result.Upserts.Count > 0 || result.DeletedItemIds.Count > 0;
        return (result.Warnings.Count, null, changed);
    }

    private async Task FailAsync(SourceStatus previous, SourceStatusKind kind, string message, string? detail)
    {
        var failures = previous.ConsecutiveFailures + 1;
        var failed = previous with
        {
            Kind = kind,
            LastError = message,
            LastErrorDetail = detail,
            ConsecutiveFailures = failures,
            NextRetryAt = clock.GetUtcNow() + Backoff.Delay(failures),
        };
        await SetStatusAsync(failed, persist: true, CancellationToken.None).ConfigureAwait(false);
    }

    // Only the exception type is described: messages from lower layers may carry URLs or other private details.
    private static string Describe(SourceException ex) => ex switch
    {
        SourceDataException => "The server's response could not be understood.",
        _ => "The sync failed.",
    };

    // What the provider chose to disclose, plus the types of whatever it wrapped. Messages are left out here.
    private static string? DetailFor(SourceException ex)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ex.Diagnostics))
        {
            parts.Add(ex.Diagnostics);
        }

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            parts.Add("Caused by: " + inner.GetType().FullName);
        }

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync of source {SourceId} ({Kind}) failed: {Error}\n{Cause}\n{Detail}")]
    private static partial void LogSyncFailed(ILogger logger, string sourceId, string kind, string error, string cause, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceId}, collection {CollectionId}, item {ItemId}: {Problem}")]
    private static partial void LogItemWarning(ILogger logger, string sourceId, string collectionId, string? itemId, string problem);

    private SourceStatus Current(string sourceId)
    {
        lock (_gate)
        {
            return _statuses.TryGetValue(sourceId, out var s) ? s : SourceStatus.NewFor(sourceId);
        }
    }

    private async Task SetStatusAsync(SourceStatus status, bool persist, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _statuses[status.SourceId] = status;
        }

        if (persist)
        {
            await repository.SaveSourceStatusAsync(status, cancellationToken).ConfigureAwait(false);
        }

        StatusChanged?.Invoke(this, status);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        bool loaded;
        lock (_gate)
        {
            loaded = _loaded;
        }

        if (loaded)
        {
            return;
        }

        var persisted = await repository.GetSourceStatusesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            foreach (var status in persisted)
            {
                // A sync that was running when the app closed is not running now.
                _statuses[status.SourceId] = status.Kind == SourceStatusKind.Syncing ? status with { Kind = SourceStatusKind.Ok } : status;
            }

            _loaded = true;
        }
    }
}
