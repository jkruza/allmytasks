using AllMyTasks.Core.Model;

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
}
