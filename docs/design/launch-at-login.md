# Launch at login

Tracked in [#52](https://github.com/jkruza/allmytasks/issues/52).

A **Startup** section in Settings with *Launch All My Tasks when I log in*, on Windows, Linux and
macOS, and a sub-option *Start minimized to tray (only when started at login)*.

## Goals

- One checkbox turns the per-user login entry on or off on every supported OS.
- The sub-option makes only the login launch start with the tray icon alone. A launch from the
  Start menu, Dock, launcher or terminal always starts normally.
- No installer, admin rights or background helper: the app writes and removes its own per-user entry.

## Non-goals

- A system-wide (all users) entry.
- Delaying the login start, or retrying it. The OS decides when login items run.
- Detecting that the user disabled the entry in an OS tool (see below).
- Snap and Flatpak sandboxes; they have their own autostart mechanisms.

## Behaviour

| Settings | Login entry runs |
| --- | --- |
| Launch at login off | nothing (entry removed) |
| On, sub-option off | `AllMyTasks` |
| On, sub-option on | `AllMyTasks --tray` |

The sub-option is stored independently and is greyed out while launch at login is off. `--tray` is
the existing option from [system-tray.md](system-tray.md): the tray-only start is a property of the
login entry, not of the app, which is why a regular launch is unaffected. If the app was itself
started with `--data-dir <path>`, the login entry repeats it so that it opens the same data.

## Where the entry lives

| OS | Entry |
| --- | --- |
| Windows | String value `AllMyTasks` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; the program path is always quoted |
| macOS | launchd user agent `~/Library/LaunchAgents/com.jkruza.allmytasks.plist` with `RunAtLoad`, picked up at the next login |
| Linux | XDG autostart file `$XDG_CONFIG_HOME/autostart/allmytasks.desktop` (default `~/.config/autostart`); works on GNOME, KDE, Xfce, Cinnamon, MATE, LXQt |

All three are per user and need no elevation.

## What is started

`Environment.ProcessPath`, the running executable. Two cases differ:

- On Linux, an AppImage sets `APPIMAGE`; that path is registered, since the process path points into a
  temporary mount.
- When the app is run through the `dotnet` host (for example `dotnet run`) there is no executable of its
  own to register. The checkbox is then disabled with an explanation.

## Settings and the OS entry

`AppSettings` holds `LaunchAtLogin` and `StartInTrayAtLogin` (both default off, stored as
`launchAtLogin` and `startInTrayAtLogin`). The stored settings are the source of truth and the entry is
written from them:

- Changing a checkbox writes or removes the entry first and saves the setting only if that worked. When
  the OS refuses (read-only home, policy-locked registry key) the checkbox goes back and the reason is
  shown under it.
- At every start with the setting on, the entry is rewritten, so it follows the executable after the app
  is moved or updated.

Limitation: if the user turns the entry off in the OS (Task Manager's Startup tab, System Settings login
items) the app still shows the setting as on until it is toggled. Windows keeps that choice in a separate
`StartupApproved` key which is not read.

## Linux and the tray

`Start minimized to tray` on a desktop without a StatusNotifier host (stock GNOME) leaves no way to reach
the window after login; see [system-tray.md](system-tray.md#linux). The README says so next to the option;
the app does not try to detect it.

## Design

### Core (`Autostart/`)

- `AutostartLaunch`: program plus arguments, built from the two flags and the optional data directory;
  `ResolveExecutable` applies the AppImage and `dotnet` host rules.
- `IAutostart` with `WindowsAutostart` (registry), `MacAutostart` and `LinuxAutostart` (file writers that take
  their directory, so tests use a temp folder). Quoting is done per format: Windows command line,
  XDG `Exec` (reserved characters, `%`), and XML escaping for the plist.
- `AutostartManager`: picks the backend for the current OS, reports `UnsupportedReason`, and `Apply` turns
  the settings into an entry, returning an error message instead of throwing for IO, access and security
  failures.

### App

- `SettingsViewModel` binds the two checkboxes and calls `AutostartManager.Apply`; `App` applies once at start.

## Tests

- Command construction (`--tray` only with the sub-option, `--data-dir` passed on), executable resolution.
- Windows command-line quoting, XDG `Exec` quoting and escaping, plist content and escaping.
- File backends: create, replace, remove, remove when missing.
- `AutostartManager`: enable/disable with a fake backend, refusal reported, unsupported cases.
- `AppSettings`: defaults and round trip.
- The registry write and the real login are checked by hand on each OS.

## Manual check status

Implemented and compiled and unit-tested on Linux in CI only; the login launch has not been run on Windows,
macOS or a Linux desktop yet.
