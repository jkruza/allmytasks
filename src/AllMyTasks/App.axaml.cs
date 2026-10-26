using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Views;
using AllMyTasks.Providers.ICal;
using AllMyTasks.Providers.ICal.CalDav;
using AllMyTasks.Providers.Todoist;
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

        // Secrets are not needed by the sample provider; the store is created lazily and only asks the user when
        // something first needs it.
        var secrets = new SecretStoreFactory(dataDirectory, new DialogSecretPrompt(desktop));
        var log = new FileLog(dataDirectory, clock);
        var feedFactory = new IcalFeedFactory(logger: log);
        var davFactory = new CalDavFactory(logger: log);
        var todoistFactory = new TodoistFactory(logger: log, displayZone: zone);
        var registry = new SourceRegistry([feedFactory, davFactory, todoistFactory, new SampleSourceFactory(clock, zone)], sources, secrets);
        var engine = new SyncEngine(items, sources, registry, clock, logger: log);
        var scheduler = new SyncScheduler(engine, sources, clock);
        var dialogs = new DialogService(desktop);

        // First run: add the sample source through the registry; the first sync below fills the database through
        // the normal sync path. After that the app only reads from the local database, so a restart shows the same
        // data with no network.
        if (sources.GetSourcesAsync().GetAwaiter().GetResult().Count == 0)
        {
            registry.AddAsync(SampleSourceFactory.KindName, "Sample data", new Dictionary<string, string>()).GetAwaiter().GetResult();
        }

        var viewState = new ViewStateStore(settings);
        var appSettings = new SettingsService(viewState);
        appSettings.LoadAsync().GetAwaiter().GetResult();

        var agenda = new AgendaService(items, sources, new IcalRecurrenceExpander(), clock);
        CalendarShellViewModel? shell = null;
        shell = new CalendarShellViewModel(
            agenda,
            new TaskListService(items, sources, clock),
            new TaskDetailsService(items, sources, clock),
            sources,
            clock,
            zone,
            viewState,
            engine,
            () => dialogs.ShowSources(new SourcesViewModel(engine, registry, sources, clock, dialogs, () => shell!.OnCollectionsChangedAsync())),
            appSettings,
            () => _ = dialogs.ShowSettingsAsync(new SettingsViewModel(appSettings)));

        desktop.MainWindow = new MainWindow { DataContext = shell };
        desktop.MainWindow.Activated += (_, _) => _ = scheduler.OnWindowActivatedAsync();
        desktop.Exit += (_, _) =>
        {
            scheduler.Dispose();
            feedFactory.Dispose();
            davFactory.Dispose();
            todoistFactory.Dispose();
            shell.Dispose();
            engine.Dispose();
            secrets.Dispose();
        };

        // Show cached data first, then sync in the background.
        _ = StartAsync(shell, scheduler);
    }

    private static async Task StartAsync(CalendarShellViewModel shell, SyncScheduler scheduler)
    {
        await shell.InitializeAsync();
        await scheduler.StartAsync();
    }

    private static void ShowFatal(IClassicDesktopStyleApplicationLifetime desktop, string heading, string message)
    {
        var window = new MessageWindow(heading, message, "Close", null);
        window.Closed += (_, _) => desktop.Shutdown(1);
        desktop.MainWindow = window;
    }
}
