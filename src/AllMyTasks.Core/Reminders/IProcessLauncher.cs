using System.ComponentModel;
using System.Diagnostics;

namespace AllMyTasks.Core.Reminders;

/// <summary>What a finished process printed and returned.</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Starts an external program without a shell. A seam so notifiers can be tested without the program.</summary>
public interface IProcessLauncher
{
    /// <summary>Runs the program and completes when it exits. Throws <see cref="Win32Exception"/> when it cannot be started.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Runs real processes. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so nothing is parsed by a shell.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await errors.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output);
    }
}
