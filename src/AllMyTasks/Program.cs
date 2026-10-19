using Avalonia;

namespace AllMyTasks;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
