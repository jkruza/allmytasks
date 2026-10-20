using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Views;
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
            var clock = TimeProvider.System;
            var zone = TimeZoneInfo.Local;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);

            // Sample data until real providers and the local store exist (M3+).
            var store = SampleData.CreateStoreAsync(today, zone).GetAwaiter().GetResult();
            var agenda = new AgendaService(store, store, new FakeRecurrenceExpander(), clock);
            var shell = new CalendarShellViewModel(agenda, new TaskListService(store, store, clock), store, clock, zone);

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.Exit += (_, _) => shell.Dispose();
            _ = shell.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
