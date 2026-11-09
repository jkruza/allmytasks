using Microsoft.Extensions.Logging;

namespace AllMyTasks.Core.Startup;

/// <summary>
/// The clean-up the app does when it exits. Every step runs in its own guard, so a step that throws is logged and
/// the others still run, and nothing escapes into the operating system's "application error" dialog (which is what
/// an exception during a Windows shutdown turns into). See docs/design/graceful-shutdown.md.
/// </summary>
public sealed partial class ShutdownSequence
{
    private readonly List<(string Name, Action Step)> _steps = [];
    private int _started;

    /// <summary>True once <see cref="Run"/> has been called. Handlers use it to tell an exit from a real fault.</summary>
    public bool HasStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Adds a step. Steps run in the order they were added.</summary>
    public void Add(string name, Action step)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add((name, step));
    }

    /// <summary>Runs every step once; a second call does nothing. Never throws.</summary>
    public void Run(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        foreach (var (name, step) in _steps)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                LogStepFailed(logger, name, ex.GetType().Name, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown step '{Step}' failed: {Type}: {Message}")]
    private static partial void LogStepFailed(ILogger logger, string step, string type, string message);
}
