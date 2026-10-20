namespace AllMyTasks.Core.Sync;

/// <summary>Retry delays after failures: 1, 2, 4, ... minutes, capped at 30.</summary>
public static class Backoff
{
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    /// <summary>The wait after <paramref name="consecutiveFailures"/> failures in a row (1 for the first).</summary>
    public static TimeSpan Delay(int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
        {
            return TimeSpan.Zero;
        }

        var minutes = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        var delay = TimeSpan.FromMinutes(minutes);
        return delay > Max ? Max : delay;
    }
}
