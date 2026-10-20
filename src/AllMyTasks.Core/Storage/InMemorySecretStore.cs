namespace AllMyTasks.Core.Storage;

/// <summary>A secret store that keeps secrets in process memory only. For tests; never used by the app.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Source, string Field), string> _secrets = [];

    public Task<string?> GetAsync(string sourceId, string field, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_secrets.TryGetValue((sourceId, field), out var v) ? v : null);
        }
    }

    public Task SetAsync(string sourceId, string field, string value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _secrets[(sourceId, field)] = value;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sourceId, string field, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _secrets.Remove((sourceId, field));
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in _secrets.Keys.Where(k => k.Source == sourceId).ToList())
            {
                _secrets.Remove(key);
            }
        }

        return Task.CompletedTask;
    }
}
