using System.Text.Json;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Secrets;

/// <summary>
/// Implements <see cref="ISecretStore"/> on top of any <see cref="ISecretBackend"/>. Each field is its own backend
/// entry (some stores limit entry size), and a small index entry per source lists its fields so a whole source
/// can be deleted without enumerating the backend.
/// </summary>
public sealed class IndexedSecretStore(ISecretBackend backend) : ISecretStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ISecretBackend Backend { get; } = backend;

    public void Dispose() => _gate.Dispose();

    public async Task<string?> GetAsync(string sourceId, string field, CancellationToken cancellationToken = default)
    {
        Validate(sourceId, field);
        return await Backend.GetAsync(Account(sourceId, field), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(string sourceId, string field, string value, CancellationToken cancellationToken = default)
    {
        Validate(sourceId, field);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Backend.SetAsync(Account(sourceId, field), value, cancellationToken).ConfigureAwait(false);
            var fields = await ReadIndexAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (fields.Add(field))
            {
                await WriteIndexAsync(sourceId, fields, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string sourceId, string field, CancellationToken cancellationToken = default)
    {
        Validate(sourceId, field);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Backend.DeleteAsync(Account(sourceId, field), cancellationToken).ConfigureAwait(false);
            var fields = await ReadIndexAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (fields.Remove(field))
            {
                await WriteIndexAsync(sourceId, fields, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var field in await ReadIndexAsync(sourceId, cancellationToken).ConfigureAwait(false))
            {
                await Backend.DeleteAsync(Account(sourceId, field), cancellationToken).ConfigureAwait(false);
            }

            await Backend.DeleteAsync(IndexAccount(sourceId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Account(string sourceId, string field) => $"{sourceId}/{field}";

    private static string IndexAccount(string sourceId) => $"{sourceId}/\u0001index";

    private static void Validate(string sourceId, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (field.Contains('\u0001', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid field name.", nameof(field));
        }
    }

    private async Task<HashSet<string>> ReadIndexAsync(string sourceId, CancellationToken cancellationToken)
    {
        var json = await Backend.GetAsync(IndexAccount(sourceId), cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A damaged index only costs us the ability to enumerate; start a new one.
            return [];
        }
    }

    private Task WriteIndexAsync(string sourceId, HashSet<string> fields, CancellationToken cancellationToken) =>
        fields.Count == 0
            ? Backend.DeleteAsync(IndexAccount(sourceId), cancellationToken)
            : Backend.SetAsync(IndexAccount(sourceId), JsonSerializer.Serialize(fields), cancellationToken);
}
