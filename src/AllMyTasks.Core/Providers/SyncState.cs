namespace AllMyTasks.Core.Providers;

/// <summary>An opaque marker of where a collection was last synced to. Only the provider that issued it can read it.</summary>
public sealed record SyncState(string Token)
{
    public string Token { get; init => field = Guard.NotBlank(value); } = Guard.NotBlank(Token);

    public override string ToString() => nameof(SyncState);
}
