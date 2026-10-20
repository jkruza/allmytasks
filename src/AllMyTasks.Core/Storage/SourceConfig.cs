namespace AllMyTasks.Core.Storage;

/// <summary>
/// The non-secret settings of a source (server URL, username, ...). Secrets live in <see cref="ISecretStore"/>
/// and are never part of this.
/// </summary>
public sealed record SourceConfig(IReadOnlyDictionary<string, string> Values)
{
    public static SourceConfig Empty { get; } = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Values { get; init => field = Guard.NotNull(value); } = Guard.NotNull(Values);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}
