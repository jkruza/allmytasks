using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AllMyTasks.Secrets;

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet 5.97+ / Plasma 6) through the <c>secret-tool</c> client from
/// libsecret. Secrets are passed on standard input, never on the command line.
/// </summary>
public sealed class LinuxSecretToolBackend(string executable = "secret-tool") : ISecretBackend
{
    internal const string Service = "AllMyTasks";

    public string Name => "Secret Service (GNOME Keyring / KWallet)";

    /// <summary>
    /// True when <c>secret-tool</c> runs and can reach a Secret Service. A lookup of an entry that does not exist
    /// exits with status 1 and prints nothing; a missing service prints an error.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(string executable = "secret-tool", CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync(executable, ["lookup", "service", Service, "account", "\u0001probe"], null, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 || (result.ExitCode == 1 && result.Error.Trim().Length == 0);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public async Task<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, ["lookup", "service", Service, "account", account], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return result.Output;
        }

        if (result.ExitCode == 1 && result.Error.Trim().Length == 0)
        {
            return null;
        }

        throw new SecretStoreException("The system keyring could not be read.");
    }

    public async Task SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            executable,
            ["store", "--label=All My Tasks", "service", Service, "account", account],
            secret,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new SecretStoreException("The system keyring rejected the secret.");
        }
    }

    public async Task DeleteAsync(string account, CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, ["clear", "service", Service, "account", account], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.Error.Trim().Length > 0)
        {
            throw new SecretStoreException("The system keyring could not delete the secret.");
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string executable,
        string[] arguments,
        string? stdin,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new SecretStoreException("The system keyring tool could not be started.");
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The tool exited without reading its input (it failed early); its exit code tells the story.
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
