using System.Net.Http;
using System.Net.Sockets;
using AllMyTasks.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllMyTasks.Core.Providers;

/// <summary>
/// Optional convenience base for providers: identity properties, a logger, retry with backoff and
/// mapping of stray exceptions to <see cref="SourceException"/>. Adds no abstract members beyond
/// <see cref="ISource"/>, so providers are free to implement the interfaces directly instead.
/// </summary>
public abstract partial class SourceBase : ISource
{
    protected SourceBase(
        string id,
        string kind,
        string displayName,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        Id = Guard.NotBlank(id);
        Kind = Guard.NotBlank(kind);
        DisplayName = Guard.NotNull(displayName);
        Logger = logger ?? NullLogger.Instance;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Id { get; }

    public string Kind { get; }

    public string DisplayName { get; }

    protected ILogger Logger { get; }

    protected TimeProvider TimeProvider { get; }

    public abstract Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying <see cref="SourceUnavailableException"/> with exponential
    /// backoff (<paramref name="baseDelay"/>, doubling) up to <paramref name="maxAttempts"/> times. Other
    /// exceptions are mapped by <see cref="MapException"/> and not retried.
    /// </summary>
    protected async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var delay = baseDelay ?? TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var mapped = MapException(ex);
                if (mapped is not SourceUnavailableException || attempt >= maxAttempts)
                {
                    if (ReferenceEquals(mapped, ex))
                    {
                        throw;
                    }

                    throw mapped;
                }

                LogRetrying(Logger, Id, attempt, maxAttempts);
                await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Maps an arbitrary exception to a <see cref="SourceException"/>. Network and timeout failures become
    /// <see cref="SourceUnavailableException"/>, everything else a plain <see cref="SourceException"/>. The
    /// message names only the exception type, because messages from HTTP stacks can include URLs with credentials.
    /// </summary>
    protected virtual SourceException MapException(Exception exception) => exception switch
    {
        SourceException source => source,
        HttpRequestException or SocketException or IOException or TimeoutException or OperationCanceledException =>
            new SourceUnavailableException($"Source '{Id}' is unavailable ({exception.GetType().Name}).", exception),
        _ => new SourceException($"Source '{Id}' failed ({exception.GetType().Name}).", exception),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceId} unavailable (attempt {Attempt} of {MaxAttempts}); retrying")]
    private static partial void LogRetrying(ILogger logger, string sourceId, int attempt, int maxAttempts);
}
