namespace AllMyTasks.Core.Providers;

/// <summary>
/// Optional: a source that must undo something at its provider when the user removes it, such as revoking a sign-in.
/// Best effort: <see cref="SourceRegistry"/> ignores failures and always removes the source.
/// </summary>
public interface ISourceCleanup
{
    /// <summary>Called before the source's secrets are deleted.</summary>
    Task OnRemovingAsync(CancellationToken cancellationToken = default);
}
