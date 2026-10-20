using AllMyTasks.Core.Model;
using AllMyTasks.Core.Providers;

namespace AllMyTasks.Core.Storage;

/// <summary>Sources, their collections and the user's per-collection choices.</summary>
public interface ISourceRepository
{
    Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the collections of a source with what the provider reports now. The user's colour and
    /// visibility choices for collections that still exist are kept.
    /// </summary>
    Task SaveCollectionsAsync(SourceInfo source, IReadOnlyList<CollectionInfo> collections, CancellationToken cancellationToken = default);

    /// <summary>Stores the user's visibility and colour choice. A null colour means "use the provider's".</summary>
    Task SetCollectionPreferencesAsync(string sourceId, string collectionId, bool isVisible, string? colorOverride, CancellationToken cancellationToken = default);

    /// <summary>Adds a source or updates its name and settings.</summary>
    Task SaveSourceAsync(SourceInfo source, SourceConfig config, CancellationToken cancellationToken = default);

    Task<SourceConfig?> GetSourceConfigAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a source with its collections, cached items and sync state in one operation. The caller also
    /// deletes the source's secrets.
    /// </summary>
    Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    Task<SyncState?> GetSyncStateAsync(string sourceId, string collectionId, CancellationToken cancellationToken = default);

    /// <summary>Stores the sync state of a collection; null forgets it so the next sync is a full one.</summary>
    Task SaveSyncStateAsync(string sourceId, string collectionId, SyncState? state, CancellationToken cancellationToken = default);

    /// <summary>"Reset cache": drops all cached items and sync state but keeps sources, collections and preferences.</summary>
    Task ResetCacheAsync(CancellationToken cancellationToken = default);
}
