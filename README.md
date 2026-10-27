# All My Tasks

> Working title.

An open-source, cross-platform desktop app that brings your calendars and tasks
from many providers into one place. Built with .NET and [Avalonia](https://avaloniaui.net/),
it runs on Windows, macOS and Linux.

**Website:** <https://allmytasks.jkruza.com/>

**Status:** early. Read-only providers so far: iCalendar feeds, CalDAV calendars, Todoist tasks
(add one with a Todoist API token) and personal Microsoft accounts (Outlook calendar and Microsoft To Do, not yet
verified against a live account). More providers are planned, see the [roadmap](docs/design/roadmap.md).

### Microsoft accounts

Microsoft only issues tokens to a registered app, and the repository does not ship one. To add a Microsoft
source, register a free app once (a few minutes, no secret, no verification for personal accounts) and paste its
Application (client) ID into the source form; or build with `AMT_MICROSOFT_CLIENT_ID` set to embed it. Steps:
[Register an app](docs/design/outlook-calendar-provider.md#register-an-app-once-per-maintainer-or-user). After you save the source, a
browser window opens to sign in; only a refresh token is kept, in your system keyring. Work and school accounts
are not supported yet. A calendar-only alternative that needs no registration is the Outlook.com *published
calendar* link, added as an iCalendar feed.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build AllMyTasks.slnx
dotnet test --solution AllMyTasks.slnx
dotnet run --project src/AllMyTasks
```

### System tray

The app shows a tray icon. Closing the window hides it to the tray; use **Quit** in the tray menu to exit
(or turn off *Keep running in the system tray when the window is closed* in Settings). Start with only the tray icon using
`--tray`, for example `dotnet run --project src/AllMyTasks -- --tray`.

On Linux the tray needs a StatusNotifier/AppIndicator host. KDE, Xfce, Cinnamon and MATE have one; GNOME needs the
*AppIndicator and KStatusNotifierItem Support* extension. Without one the icon does not show, so turn the setting off and
do not use `--tray`. See [docs/design/system-tray.md](docs/design/system-tray.md).

## Project layout

| Path | What it is |
| --- | --- |
| `src/AllMyTasks` | The Avalonia desktop app (views and view models) |
| `src/AllMyTasks.Core` | UI-independent logic: tasks, providers, aggregation |
| `src/AllMyTasks.Providers.ICal` | iCalendar feed and CalDAV providers |
| `src/AllMyTasks.Providers.Todoist` | Todoist task provider (API token, read-only) |
| `src/AllMyTasks.Providers.Microsoft` | Outlook calendar and Microsoft To Do for personal accounts (Microsoft Graph, read-only) |
| `site/` | The static project page published on GitHub Pages |
| `tests/` | xUnit tests for Core and each provider |

## Design

The intended design (data model, providers, views, storage, roadmap) is in
[docs/design](docs/design/README.md).

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first,
and report security issues as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
