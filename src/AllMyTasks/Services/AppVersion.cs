using System.Reflection;

namespace AllMyTasks.Services;

/// <summary>The running version. The informational version carries build metadata ("1.2.0+commit"); only the version proper is used.</summary>
internal static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
