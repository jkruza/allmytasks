using System.Reflection;
using AllMyTasks.Core.Startup;
using AllMyTasks.Core.Storage;
using AllMyTasks.Services;
using Avalonia;
using Microsoft.Extensions.Logging;

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

        var log = new FileLog(DataDirectory.Resolve(DataDirectory.FromArguments(args)), TimeProvider.System);
        InstallExceptionLogging(log);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (App.Shutdown.HasStarted)
        {
            // An exception while the app is closing (for example when Windows shuts down) is logged, not shown
            // to the user as an "Application Error" dialog.
            Report(log, LogLevel.Warning, "Exception while exiting", ex);
            return 0;
        }
        catch (Exception ex)
        {
            Report(log, LogLevel.Critical, "Unhandled exception", ex);
            throw;
        }
    }

    // Exceptions that nothing catches are written to logs/allmytasks.log so a crash can be diagnosed. Only the
    // type and message are logged, like the rest of the log.
    private static void InstallExceptionLogging(FileLog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(log, LogLevel.Critical, "Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(log, LogLevel.Warning, "Unobserved task exception", e.Exception.GetBaseException());
            e.SetObserved();
        };
    }

    /// <summary>Logs an exception; never throws, because it runs while the process may be going down.</summary>
    internal static void Report(FileLog log, LogLevel level, string what, Exception? exception)
    {
        try
        {
            var text = $"{what}: {exception?.GetType().FullName ?? "unknown"}: {exception?.Message}{(exception is null ? string.Empty : Environment.NewLine + exception.StackTrace)}";
            log.Log(level, default, text, null, static (message, _) => message);
        }
        catch (Exception)
        {
            // Nothing more can be done: the log itself is not writable.
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // The informational version carries the build metadata ("1.2.0+<commit>"); only the version proper is printed.
    private static string VersionText() => AppVersion.Current;
}
