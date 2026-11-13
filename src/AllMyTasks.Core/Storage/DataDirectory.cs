namespace AllMyTasks.Core.Storage;

/// <summary>Where the app keeps its database and other per-user files.</summary>
public static class DataDirectory
{
    public const string OptionName = "--data-dir";

    /// <summary>
    /// Resolves the data directory: an explicit override wins; otherwise the per-user application data
    /// directory (<c>%LOCALAPPDATA%\AllMyTasks</c>, <c>~/Library/Application Support/AllMyTasks</c>,
    /// <c>$XDG_DATA_HOME/allmytasks</c> or <c>~/.local/share/allmytasks</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The override contains a NUL character or a <c>..</c> segment.</exception>
    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!IsAcceptable(overridePath))
            {
                throw new ArgumentException("The data directory must not contain '..' segments or NUL characters.", nameof(overridePath));
            }

            return Path.GetFullPath(overridePath);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var name = OperatingSystem.IsLinux() ? "allmytasks" : "AllMyTasks";
        return Path.Combine(root, name);
    }

    /// <summary>
    /// Whether a user-supplied data directory is plain: no NUL character and no <c>..</c> segment, so the
    /// folder the app writes to is the one the path visibly names.
    /// </summary>
    public static bool IsAcceptable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return !path.Contains('\0', StringComparison.Ordinal)
            && !path.Split('/', '\\').Contains("..", StringComparer.Ordinal);
    }

    /// <summary>Finds <c>--data-dir &lt;path&gt;</c> or <c>--data-dir=&lt;path&gt;</c> in the command line.</summary>
    public static string? FromArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == OptionName && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(OptionName + "=", StringComparison.Ordinal))
            {
                return args[i][(OptionName.Length + 1)..];
            }
        }

        return null;
    }
}
