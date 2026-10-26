using AllMyTasks.ViewModels;
using AllMyTasks.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace AllMyTasks.Services;

/// <summary>
/// The system tray icon and its menu, and the rules for hiding the main window to the tray.
/// See docs/design/system-tray.md.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private static readonly Uri IconUri = new("avares://AllMyTasks/Assets/tray.png");

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly CalendarShellViewModel _shell;
    private readonly SettingsService _settings;
    private readonly TrayIcon _icon;
    private bool _quitting;

    public TrayService(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        CalendarShellViewModel shell,
        SettingsService settings)
    {
        _desktop = desktop;
        _window = window;
        _shell = shell;
        _settings = settings;

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(IconUri)),
            ToolTipText = "All My Tasks",
            Menu = BuildMenu(),
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(application, [_icon]);

        // The tray keeps the app alive while the window is hidden, so quitting is always explicit.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Closing += OnWindowClosing;
        window.Closed += (_, _) => Quit();
    }

    /// <summary>Shows the main window in front, creating no second window.</summary>
    public void Show()
    {
        _desktop.MainWindow ??= _window;
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Dispose() => _icon.Dispose();

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Show All My Tasks", Show));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Day", () => ShowView(ViewMode.Day)));
        menu.Items.Add(Item("Week", () => ShowView(ViewMode.Week)));
        menu.Items.Add(Item("Month", () => ShowView(ViewMode.Month)));
        menu.Items.Add(Item("Upcoming", () => ShowView(ViewMode.Upcoming)));
        menu.Items.Add(Item("All tasks", () => ShowView(ViewMode.Tasks)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Refresh", () => _ = _shell.RefreshAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit", Quit));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowView(ViewMode mode)
    {
        _shell.Mode = mode;
        Show();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Only a close the user asked for becomes a hide; never hold up an OS or application shutdown.
        if (!_quitting && e.CloseReason == WindowCloseReason.WindowClosing && _settings.Current.CloseToTray && _icon.IsVisible)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    private void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _desktop.Shutdown();
    }
}
