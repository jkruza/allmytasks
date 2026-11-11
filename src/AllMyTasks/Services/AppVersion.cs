using System.Reflection;
using AllMyTasks.Core.About;

namespace AllMyTasks.Services;

/// <summary>The running version. The informational version carries build metadata ("1.2.0+commit"); only the version proper is used for comparisons.</summary>
internal static class AppVersion
{
    private static readonly (string Version, string? Commit) Parts =
        AboutInfo.SplitVersion(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Current => Parts.Version;

    public static string? Commit => Parts.Commit;
}
