namespace AllMyTasks.Secrets;

/// <summary>
/// One OS (or file) secret store reduced to the three operations every backend has. Accounts are opaque
/// strings chosen by <see cref="IndexedSecretStore"/>.
/// </summary>
public interface ISecretBackend
{
    /// <summary>Human-readable backend name for the settings screen, such as "Windows Credential Manager".</summary>
    string Name { get; }

    Task<string?> GetAsync(string account, CancellationToken cancellationToken);

    Task SetAsync(string account, string secret, CancellationToken cancellationToken);

    /// <summary>Deletes the secret. Missing ones are ignored.</summary>
    Task DeleteAsync(string account, CancellationToken cancellationToken);
}
