using AllMyTasks.Core.Providers;
using AllMyTasks.Core.Tasks;
using AllMyTasks.ViewModels;
using AllMyTasks.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AllMyTasks;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(new TaskAggregator([CreateSampleSource()]));
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.LoadCommand.ExecuteAsync(null);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Placeholder until real providers (Todoist, Google, Microsoft, CalDAV...) are added.
    private static InMemoryTaskSource CreateSampleSource()
    {
        var today = DateTimeOffset.Now.Date;
        return new InMemoryTaskSource("sample", "Sample tasks",
        [
            new TaskItem("1", "sample", "Connect your first calendar", today),
            new TaskItem("2", "sample", "Connect your first task list", today.AddDays(1)),
            new TaskItem("3", "sample", "Read CONTRIBUTING.md"),
            new TaskItem("4", "sample", "Install All My Tasks", today.AddDays(-1), IsCompleted: true),
        ]);
    }
}
