namespace AllMyTasks.Core.Storage;

/// <summary>Settings kept in memory. For tests and as the fallback when no database is available.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _values = [];

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}
