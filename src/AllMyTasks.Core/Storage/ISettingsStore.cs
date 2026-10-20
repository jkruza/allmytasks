namespace AllMyTasks.Core.Storage;

/// <summary>Small key/value store for UI preferences (current view, filters, window state). Not for secrets.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
