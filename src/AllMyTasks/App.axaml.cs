using AllMyTasks.Core.Model;
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
            var zone = TimeZoneInfo.Local;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
            var aggregator = new TaskAggregator([CreateSampleSource(today)], zone);
            var viewModel = new MainWindowViewModel(aggregator, zone);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.LoadCommand.ExecuteAsync(null);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Placeholder until real providers (Todoist, Google, Microsoft, CalDAV...) are added.
    private static InMemoryTaskSource CreateSampleSource(DateOnly today)
    {
        const string SourceId = "sample";
        const string ListId = "inbox";
        var list = new CollectionInfo(SourceId, ListId, "Sample list", CollectionKind.TaskList);

        return new InMemoryTaskSource(SourceId, "Sample tasks", [list],
        [
            new TaskItem(new ItemKey(SourceId, ListId, "1"), "Connect your first calendar", Due: new DueDate(today)),
            new TaskItem(new ItemKey(SourceId, ListId, "2"), "Connect your first task list", Due: new DueDate(today.AddDays(1))),
            new TaskItem(new ItemKey(SourceId, ListId, "3"), "Read CONTRIBUTING.md"),
            new TaskItem(
                new ItemKey(SourceId, ListId, "4"),
                "Install All My Tasks",
                Due: new DueDate(today.AddDays(-1)),
                Status: TaskItemStatus.Completed),
        ]);
    }
}
