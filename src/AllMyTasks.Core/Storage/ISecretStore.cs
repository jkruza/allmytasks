namespace AllMyTasks.Core.Storage;

/// <summary>
/// Where passwords, app passwords, private feed URLs and tokens live. Backed by the OS secret store; never by
/// the database, a config file, a log or an exception message.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string sourceId, string field, CancellationToken cancellationToken = default);

    Task SetAsync(string sourceId, string field, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes one field of a source. Missing fields are ignored.</summary>
    Task DeleteAsync(string sourceId, string field, CancellationToken cancellationToken = default);

    /// <summary>Deletes every field of a source. Called in the same operation that removes the source.</summary>
    Task DeleteAsync(string sourceId, CancellationToken cancellationToken = default);
}
