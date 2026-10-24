using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AllMyTasks.Services;

/// <summary>
/// A small append-only log file in the data folder (<c>logs/allmytasks.log</c>), for troubleshooting sync problems.
/// Information and above are written; when the file passes 1 MB it is moved to <c>allmytasks.log.1</c>.
/// Callers are responsible for keeping credentials and feed addresses out of what they log.
/// </summary>
public sealed class FileLog : ILogger
{
    private static readonly long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    public FileLog(string dataDirectory, TimeProvider clock)
    {
        _clock = clock;
        Directory = System.IO.Path.Combine(dataDirectory, "logs");
        Path = System.IO.Path.Combine(Directory, "allmytasks.log");
    }

    /// <summary>The folder that holds the log files.</summary>
    public string Directory { get; }

    /// <summary>The current log file.</summary>
    public string Path { get; }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = new StringBuilder()
            .Append(_clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(logLevel.ToString().ToUpperInvariant())
            .Append(' ')
            .Append(formatter(state, exception))
            .AppendLine()
            .ToString();

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Move(Path, Path + ".1", overwrite: true);
                }

                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }
}
