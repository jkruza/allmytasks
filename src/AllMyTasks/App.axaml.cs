using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Views;
using AllMyTasks.Secrets;
using AllMyTasks.Services;
using AllMyTasks.Storage.Sqlite;
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
            try
            {
                Start(desktop);
            }
            catch (StorageVersionException ex)
            {
                ShowFatal(desktop, "Your data is from a newer version", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                ShowFatal(desktop, "Could not open your data", $"The local database could not be opened ({ex.GetType().Name}). Check that the data folder is writable, or start with --data-dir to use another one.");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var clock = TimeProvider.System;
        var zone = TimeZoneInfo.Local;
        var dataDirectory = DataDirectory.Resolve(DataDirectory.FromArguments(desktop.Args ?? []));

        var database = SqliteDatabase.OpenAsync(System.IO.Path.Combine(dataDirectory, "data.db")).GetAwaiter().GetResult();
        var items = new SqliteItemStore(database);
        var sources = new SqliteSourceRepository(database);
        var settings = new SqliteSettingsStore(database);

        // Until real providers can be added (M4/M5), the first run is seeded with sample data through the
        // normal sync path. After that the app reads only from the local database, so a restart shows the same
        // data with no network.
        if (sources.GetSourcesAsync().GetAwaiter().GetResult().Count == 0)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
            SampleData.SeedAsync(items, sources, today, zone).GetAwaiter().GetResult();
        }

        // Secrets are not needed yet; the store is created lazily and only asks the user when first used.
        var secrets = new SecretStoreFactory(dataDirectory, new DialogSecretPrompt(desktop));

        var agenda = new AgendaService(items, sources, new FakeRecurrenceExpander(), clock);
        var shell = new CalendarShellViewModel(agenda, new TaskListService(items, sources, clock), sources, clock, zone, new ViewStateStore(settings));

        desktop.MainWindow = new MainWindow { DataContext = shell };
        desktop.Exit += (_, _) =>
        {
            shell.Dispose();
            secrets.Dispose();
        };
        _ = shell.InitializeAsync();
    }

    private static void ShowFatal(IClassicDesktopStyleApplicationLifetime desktop, string heading, string message)
    {
        var window = new MessageWindow(heading, message, "Close", null);
        window.Closed += (_, _) => desktop.Shutdown(1);
        desktop.MainWindow = window;
    }
}
