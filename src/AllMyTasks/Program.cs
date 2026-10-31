using System.Reflection;
using AllMyTasks.Core.Startup;
using Avalonia;

namespace AllMyTasks;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (StartupOptions.FromArguments(args).ShowVersion)
        {
            Console.Out.WriteLine($"AllMyTasks {VersionText()}");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // The informational version carries the build metadata ("1.2.0+<commit>"); only the version proper is printed.
    private static string VersionText()
    {
        var info = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info is null ? "unknown" : info.Split('+')[0];
    }
}
