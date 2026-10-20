using AllMyTasks.Core.Storage;

namespace AllMyTasks.Secrets;

/// <summary>Which kind of backend ended up holding the secrets.</summary>
public enum SecretBackendKind
{
    WindowsCredentialManager,
    MacKeychain,
    SecretService,
    PinProtectedFile,
}

/// <summary>
/// Picks the secret store for this OS once, lazily on first use: Credential Manager on Windows, Keychain on
/// macOS, Secret Service on Linux when reachable and otherwise (with the user's opt-in) a PIN-protected file.
/// </summary>
public sealed class SecretStoreFactory(string dataDirectory, ISecretUserPrompt prompt, Argon2Parameters? pinFileParameters = null) : ISecretStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISecretStore? _store;
    private PinProtectedFileBackend? _pinFile;

    /// <summary>The backend in use, or null before the first secret is read or written.</summary>
    public SecretBackendKind? Kind { get; private set; }

    public Task<string?> GetAsync(string sourceId, string field, CancellationToken cancellationToken = default) =>
        WithStoreAsync(s => s.GetAsync(sourceId, field, cancellationToken), cancellationToken);

    public Task SetAsync(string sourceId, string field, string value, CancellationToken cancellationToken = default) =>
        WithStoreAsync(s => s.SetAsync(sourceId, field, value, cancellationToken), cancellationToken);

    public Task DeleteAsync(string sourceId, string field, CancellationToken cancellationToken = default) =>
        WithStoreAsync(s => s.DeleteAsync(sourceId, field, cancellationToken), cancellationToken);

    public Task DeleteAsync(string sourceId, CancellationToken cancellationToken = default) =>
        WithStoreAsync(s => s.DeleteAsync(sourceId, cancellationToken), cancellationToken);

    /// <summary>The PIN-protected file backend, if that is what was chosen (for "change PIN" and "forgot PIN").</summary>
    public PinProtectedFileBackend? PinFile => _pinFile;

    public void Dispose()
    {
        _pinFile?.Dispose();
        _gate.Dispose();
    }

    private async Task<T> WithStoreAsync<T>(Func<ISecretStore, Task<T>> action, CancellationToken cancellationToken) =>
        await action(await GetStoreAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task WithStoreAsync(Func<ISecretStore, Task> action, CancellationToken cancellationToken) =>
        await action(await GetStoreAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<ISecretStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store is not null)
            {
                return _store;
            }

            ISecretBackend backend;
            if (OperatingSystem.IsWindows())
            {
                backend = new WindowsCredentialBackend();
                Kind = SecretBackendKind.WindowsCredentialManager;
            }
            else if (OperatingSystem.IsMacOS())
            {
                backend = new MacKeychainBackend();
                Kind = SecretBackendKind.MacKeychain;
            }
            else if (await LinuxSecretToolBackend.IsAvailableAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                backend = new LinuxSecretToolBackend();
                Kind = SecretBackendKind.SecretService;
            }
            else
            {
                // Never silently weaker: the user has to agree to the PIN-protected file.
                if (!await prompt.ConfirmPinFallbackAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new SecretStoreUnavailableException("No system keyring is available and the PIN-protected file was declined.");
                }

                _pinFile = new PinProtectedFileBackend(PinProtectedFileBackend.DefaultPath(dataDirectory), prompt, pinFileParameters);
                backend = _pinFile;
                Kind = SecretBackendKind.PinProtectedFile;
            }

            _store = new IndexedSecretStore(backend);
            return _store;
        }
        finally
        {
            _gate.Release();
        }
    }
}
