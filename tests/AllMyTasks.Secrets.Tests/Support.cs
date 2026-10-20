using AllMyTasks.Secrets;

namespace AllMyTasks.Secrets.Tests;

internal static class Platform
{
    public static bool IsLinux => OperatingSystem.IsLinux();
}

/// <summary>Scripted answers to the PIN questions.</summary>
internal sealed class ScriptedPrompt(params string?[] pins) : ISecretUserPrompt
{
    private readonly Queue<string?> _pins = new(pins);

    public List<PinRequest> Requests { get; } = [];

    public bool AllowFallback { get; set; } = true;

    public Task<bool> ConfirmPinFallbackAsync(CancellationToken cancellationToken) => Task.FromResult(AllowFallback);

    public Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_pins.Count > 0 ? _pins.Dequeue() : null);
    }
}

internal sealed class DictionaryBackend : ISecretBackend
{
    public Dictionary<string, string> Entries { get; } = [];

    public string Name => "test";

    public Task<string?> GetAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.TryGetValue(account, out var v) ? v : null);

    public Task SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        Entries[account] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account, CancellationToken cancellationToken)
    {
        Entries.Remove(account);
        return Task.CompletedTask;
    }
}

/// <summary>A clock whose timers fire immediately and remember how long they were asked to wait.</summary>
internal sealed class InstantClock : TimeProvider
{
    public List<TimeSpan> Delays { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        var timer = new ImmediateTimer();
        _ = Task.Run(() => callback(state));
        return timer;
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amt-secrets-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
