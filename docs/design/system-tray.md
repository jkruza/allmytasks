# System tray

Tracked in [#39](https://github.com/jkruza/allmytasks/issues/39).

A native tray icon (notification area on Windows, menu bar on macOS, StatusNotifier on Linux)
so the app can live in the background, keep syncing and be reached in one click.

## Goals

- Show a tray icon while the app runs.
- `--tray` starts the app with no window, only the tray icon.
- Closing the main window hides it to the tray instead of quitting.
- Tray menu: **Show All My Tasks**, every view (**Day**, **Week**, **Month**, **Upcoming**,
  **All tasks**), **Refresh**, **Quit**.

## Non-goals

- Notifications, reminders, badges or a compact "upcoming" popup. See the roadmap's "Later" list.
- Single-instance handling. Starting a second copy starts a second process, as today.
- Minimize-to-tray (the minimize button still minimizes to the taskbar).
- Autostart at login. `--tray` is what an autostart entry would pass; setting that up is left to the user for now.

## Behaviour

| Action | Result |
| --- | --- |
| Start normally | Window and tray icon |
| Start with `--tray` | Tray icon only; the window is created hidden and shown on request |
| Close the window (X, Alt+F4, Cmd+W) | Window hides; app keeps running and syncing |
| Click the tray icon (Windows, Linux) | Show and focus the window. On macOS a click opens the menu |
| Menu: Show All My Tasks | Show and focus the window |
| Menu: Day / Week / Month / Upcoming / All tasks | Switch to that view and show the window |
| Menu: Refresh | Sync every source (same as the Refresh button); the window is not shown |
| Menu: Quit | Exit the app |

Closing is only turned into hiding for a user-initiated close. When the OS is shutting down or the
app is being shut down (`CloseReason` other than `WindowClosing`) the window closes normally, so the
app never blocks a logout.

The sync scheduler keeps running while the window is hidden, so the data is fresh when the window
comes back. Its "refresh on window activated" trigger fires when the window is shown again.

## Setting

Settings gets a **Window** section with one checkbox: *Keep running in the system tray when the
window is closed* (`closeToTray`, default on). When off, closing the window quits the app, as before;
the tray icon and menu remain available while the window is open.

The setting exists mainly for Linux, below. It is stored in `AppSettings` like the week start, and
applies immediately.

## Linux

Avalonia's tray icon on Linux uses the StatusNotifierItem D-Bus protocol (AppIndicator). It shows only if the
desktop has a host for it:

- KDE Plasma, Xfce, Cinnamon, MATE, LXQt, Budgie: work out of the box.
- GNOME: no tray by default. The user needs the *AppIndicator and KStatusNotifierItem Support* extension.

The app cannot reliably tell whether a host is present. If it is missing, the icon is silently not
shown. Consequences and how they are handled:

- With close-to-tray on, closing the window would leave an invisible process. Turn the setting
  off in that case (documented in the README); the setting can be changed before it matters because the
  window is shown on a normal start.
- `--tray` without a host leaves no way to reach the window. Do not use it there.

Detecting the host (querying `org.kde.StatusNotifierWatcher` over D-Bus) would remove both caveats but
adds a D-Bus dependency. Left as a follow-up if people hit this.

## Design

### Core

- `StartupOptions.FromArguments(args)` parses `--tray` into `StartInTray`, in the same style as
  `DataDirectory.FromArguments`. Unknown arguments are ignored. Tested.
- `AppSettings.CloseToTray` (default `true`), parsed tolerantly and serialised with the rest. Tested.

### App

- `TrayService` owns the `TrayIcon`, its `NativeMenu` and the show/hide/quit logic. It is given the
  desktop lifetime, the main window, the shell view model and `SettingsService`. Views are
  switched by setting `CalendarShellViewModel.Mode`, which already persists the choice.
- The main window is always constructed. With `--tray` it is just not assigned to
  `desktop.MainWindow` (which would show it) until the first request to show it.
- `desktop.ShutdownMode` is `OnExplicitShutdown`. Quit calls `desktop.Shutdown()`; if
  close-to-tray is off, `MainWindow.Closed` calls it too.
- The window `Closing` handler cancels and hides when `CloseReason == WindowClosing`,
  close-to-tray is on, and the tray icon is visible.
- Icon: `Assets/tray.png`, a simple placeholder (blue rounded square with a white check mark)
  until real branding exists. The icon is loaded from the assembly resources so it works in
  single-file publishes.

### Why Avalonia's `TrayIcon`

It is built in, native on all three platforms and needs no extra package or lock-file change.
Alternatives such as `H.NotifyIcon` are Windows-only.

## Tests

- `StartupOptions`: `--tray` present, absent, mixed with `--data-dir`, case sensitivity (exact match).
- `AppSettings`: `CloseToTray` defaults to true, round trips, missing or non-boolean values read as the default,
  and the week start still round trips alongside it.
- The tray itself is platform UI and is checked by hand: start with and without `--tray`, close, click, each
  menu item, Quit, the setting off.

## Manual check status

Implemented and compiled in CI only; not yet run on Windows, macOS or a Linux desktop with a StatusNotifier host.
