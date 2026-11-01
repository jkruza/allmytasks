using AllMyTasks.Core.Autostart;
using AllMyTasks.Core.Changes;
using AllMyTasks.Core.Reminders;
using AllMyTasks.Core.Samples;
using AllMyTasks.Core.Search;
using AllMyTasks.Core.Startup;
using AllMyTasks.Core.Storage;
using AllMyTasks.Core.Sync;
using AllMyTasks.Core.Views;
using AllMyTasks.Providers.GitLab;
using AllMyTasks.Providers.Google;
using AllMyTasks.Providers.ICal;
using AllMyTasks.Providers.ICal.CalDav;
using AllMyTasks.Providers.Microsoft;
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
                Start(this, desktop);
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

    private static void Start(Application application, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var clock = TimeProvider.System;
        var zone = TimeZoneInfo.Local;
        var dataDirectoryOption = DataDirectory.FromArguments(desktop.Args ?? []);
        var dataDirectory = DataDirectory.Resolve(dataDirectoryOption);

        var database = SqliteDatabase.OpenAsync(System.IO.Path.Combine(dataDirectory, "data.db")).GetAwaiter().GetResult();
        var items = new SqliteItemStore(database);
        var pending = new SqlitePendingChangeStore(database);

        // Views read the cache with the user's unsent changes applied; the sync engine writes to the plain cache.
        var viewItems = new PendingOverlayStore(items, pending);
        var sources = new SqliteSourceRepository(database);
        var settings = new SqliteSettingsStore(database);

        // Secrets are not needed by the sample provider; the store is created lazily and only asks the user when
        // something first needs it.
        var secrets = new SecretStoreFactory(dataDirectory, new DialogSecretPrompt(desktop));
        var log = new FileLog(dataDirectory, clock);
        var feedFactory = new IcalFeedFactory(logger: log);
        var davFactory = new CalDavFactory(logger: log);
        var todoistFactory = new TodoistFactory(logger: log, displayZone: zone);
        var microsoftFactory = new MicrosoftFactory(logger: log, displayZone: zone);
        var googleFactory = new GoogleFactory(logger: log);
        var gitlabFactory = new GitLabFactory(logger: log);
        var registry = new SourceRegistry([feedFactory, davFactory, todoistFactory, googleFactory, microsoftFactory, gitlabFactory, new SampleSourceFactory(clock, zone)], sources, secrets);
        var engine = new SyncEngine(items, sources, registry, clock, logger: log, pendingChanges: pending);
        var changes = new TaskChangeService(pending, sources, registry, clock);
        changes.Changed += (_, e) =>
        {
            if (e.NeedsPush)
            {
                _ = engine.SyncSourceAsync(e.SourceId);
            }
        };
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

        // Rewrite the login entry from the settings so it follows the executable if the app was moved or updated.
        var autostart = AutostartManager.ForCurrentProcess(dataDirectoryOption);
        if (appSettings.Current.LaunchAtLogin)
        {
            _ = autostart.Apply(true, appSettings.Current.StartInTrayAtLogin);
        }

        var agenda = new AgendaService(viewItems, sources, new IcalRecurrenceExpander(), clock);
        CalendarShellViewModel? shell = null;
        shell = new CalendarShellViewModel(
            agenda,
            new TaskListService(viewItems, sources, clock, changes),
            new TaskDetailsService(viewItems, sources, clock, changes),
            sources,
            clock,
            zone,
            viewState,
            engine,
            () => dialogs.ShowSources(new SourcesViewModel(engine, registry, sources, clock, dialogs, () => shell!.OnCollectionsChangedAsync())),
            appSettings,
            () => _ = dialogs.ShowSettingsAsync(new SettingsViewModel(appSettings, autostart)),
            changes,
            new SearchService(viewItems, sources, new IcalRecurrenceExpander(), clock),
            new SavedFilterStore(settings));

        var window = new MainWindow { DataContext = shell };
        window.Activated += (_, _) => _ = scheduler.OnWindowActivatedAsync();
        var tray = new TrayService(application, desktop, window, shell, appSettings);
        if (!StartupOptions.FromArguments(desktop.Args ?? []).StartInTray)
        {
            // With --tray the window is left out of desktop.MainWindow, which would show it; TrayService.Show adds it on request.
            desktop.MainWindow = window;
        }

        // Reminders: Linux uses the desktop's notification daemon and falls back to an in-app window; the other systems
        // use the in-app window until their native notifiers land with the installers (docs/design/reminders.md).
        var inApp = new InAppNotifier();
        INotifier notifier = OperatingSystem.IsLinux() ? new FallbackNotifier(new LinuxNotifier(new ProcessLauncher()), inApp) : inApp;
        var reminders = new ReminderScheduler(agenda, new SqliteReminderLog(database), notifier, () => appSettings.Current.ToReminderSettings(), zone, clock, log);
        reminders.OpenRequested += (_, reminder) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tray.Show();
            _ = shell.OpenReminderAsync(reminder);
        });
        appSettings.Changed += (_, _) => _ = reminders.TickAsync();
        engine.SourceSynced += (_, _) => _ = reminders.TickAsync();

        desktop.Exit += (_, _) =>
        {
            reminders.Dispose();
            tray.Dispose();
            scheduler.Dispose();
            feedFactory.Dispose();
            davFactory.Dispose();
            todoistFactory.Dispose();
            googleFactory.Dispose();
            gitlabFactory.Dispose();
            microsoftFactory.Dispose();
            shell.Dispose();
            changes.Dispose();
            engine.Dispose();
            secrets.Dispose();
        };

        // Show cached data first, then sync in the background.
        _ = StartAsync(shell, scheduler, reminders);
    }

    private static async Task StartAsync(CalendarShellViewModel shell, SyncScheduler scheduler, ReminderScheduler reminders)
    {
        await shell.InitializeAsync();
        await reminders.StartAsync();
        await scheduler.StartAsync();
    }

    private static void ShowFatal(IClassicDesktopStyleApplicationLifetime desktop, string heading, string message)
    {
        var window = new MessageWindow(heading, message, "Close", null);
        window.Closed += (_, _) => desktop.Shutdown(1);
        desktop.MainWindow = window;
    }
}
