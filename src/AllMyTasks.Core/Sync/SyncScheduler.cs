using System.Globalization;
using AllMyTasks.Core.Storage;

namespace AllMyTasks.Core.Sync;

/// <summary>
/// Decides when sources sync: at startup, on a timer, when the window regains focus, and after backoff. The
/// default interval is 15 minutes, overridable per source with the <c>refreshMinutes</c> config value (minimum 1).
/// </summary>
public sealed class SyncScheduler(SyncEngine engine, ISourceRepository repository, TimeProvider clock) : IDisposable
{
    public const string IntervalKey = "refreshMinutes";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(30);

    private ITimer? _timer;

    /// <summary>Starts the timer and syncs everything once. Call after the UI is showing cached data.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _timer ??= clock.CreateTimer(_ => _ = TickAsync(CancellationToken.None), null, TickPeriod, TickPeriod);
        return engine.SyncAllAsync(cancellationToken);
    }

    /// <summary>Syncs every source that is due: its interval has passed and it is not backing off or waiting for the user.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = clock.GetUtcNow();
            var infos = await repository.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            var statuses = (await engine.GetStatusesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.SourceId);
            var due = new List<Task>();
            foreach (var info in infos)
            {
                var config = await repository.GetSourceConfigAsync(info.Id, cancellationToken).ConfigureAwait(false);
                if (IsDue(statuses.GetValueOrDefault(info.Id) ?? SourceStatus.NewFor(info.Id), IntervalFor(config), now))
                {
                    due.Add(engine.SyncSourceAsync(info.Id, cancellationToken));
                }
            }

            await Task.WhenAll(due).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Call when the window regains focus: the same check as a timer tick.</summary>
    public Task OnWindowActivatedAsync(CancellationToken cancellationToken = default) => TickAsync(cancellationToken);

    public void Dispose() => _timer?.Dispose();

    /// <summary>The refresh interval of a source, from its config.</summary>
    public static TimeSpan IntervalFor(SourceConfig? config)
    {
        if (config?.Get(IntervalKey) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            var interval = TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60));
            return interval < MinimumInterval ? MinimumInterval : interval;
        }

        return DefaultInterval;
    }

    internal static bool IsDue(SourceStatus status, TimeSpan interval, DateTimeOffset now)
    {
        switch (status.Kind)
        {
            case SourceStatusKind.Syncing:
            case SourceStatusKind.NeedsAttention:
                return false;
            case SourceStatusKind.Offline:
            case SourceStatusKind.Error:
                return status.NextRetryAt is null || status.NextRetryAt <= now;
            case SourceStatusKind.New:
                return true;
            default:
                return status.LastSyncedAt is null || now - status.LastSyncedAt >= interval;
        }
    }
}
