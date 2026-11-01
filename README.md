# All My Tasks

An open-source, cross-platform desktop app that gives you a single, **read-only** place to see the tasks and
events from many task and calendar sources, for a better overview of the work to be done. You keep working in
Todoist, Outlook, Google, Jira, GitHub and GitLab; this app only looks. Built with .NET and
[Avalonia](https://avaloniaui.net/), it runs on Windows, macOS and Linux.

**Website:** <https://allmytasks.jkruza.com/>

**Status:** early, and read-only. Working today: iCalendar feeds, CalDAV calendars, Todoist tasks
(add one with a Todoist API token), personal Microsoft accounts (Outlook calendar and Microsoft To Do) and Google
accounts (Google Calendar and Google Tasks), Jira Cloud (issues assigned to you, with an API token) and GitHub (issues and pull requests assigned to you or awaiting your review, with a personal access token). The Microsoft and Google providers have been tried on real accounts; the Jira and GitHub providers have not been tried on a live account yet. GitLab is designed but not built yet; the Inbox view is built, see the [roadmap](docs/design/roadmap.md).
Todoist tasks can also be completed and reopened from the app once you switch on "Allow All My Tasks to change tasks in Todoist (preview)" for that source. That is an early preview of version 2 write support, off by default, and not yet verified against a live account.

## Roadmap

- **Version 1 (read-only overview):** a general framework; Todoist, Microsoft Outlook / To Do, Google Calendar /
  Tasks, Jira, GitHub and GitLab; views: calendar grids (day, week, month), Upcoming, All tasks and Inbox (tasks
  without a date).
- **Version 2:** more services (to be chosen), and write support for completing tasks, changing task dates and
  rescheduling events.
- **Further research:** runtime plug-in support, MCP support, and AI summaries and planning with local models.

Details: [product scope](docs/design/product-scope.md) and [roadmap](docs/design/roadmap.md).

## Connecting accounts

### Microsoft accounts

Microsoft only issues tokens to a registered app, and the repository does not ship one. To add a Microsoft
source, register a free app once (a few minutes, no secret, no verification for personal accounts) and paste its
Application (client) ID into the source form; or build with `AMT_MICROSOFT_CLIENT_ID` set to embed it. Steps:
[Register an app](docs/oauth-setup.md#microsoft-register-an-app-in-microsoft-entra). After you save the source, a
browser window opens to sign in; only a refresh token is kept, in your system keyring. Work and school accounts
are not supported yet. A calendar-only alternative that needs no registration is the Outlook.com *published
calendar* link, added as an iCalendar feed.

### Google accounts

Google Calendar and Google Tasks share one `google` source per account. Google only issues tokens to a registered OAuth client,
and the repository does not ship one; the project's own client is still to be created and verified by Google, so for now you
bring your own (the form requires it when your build has none built in). In a Google Cloud project of yours, enable the *Google Calendar API* and *Google Tasks API*, configure the
OAuth consent screen (add yourself as a test user; an app in *Testing* has refresh tokens that expire after 7 days) and create
an OAuth client of type **Desktop app**. Paste its client ID and client secret into the source form (the secret is kept in your
system keyring; Google does not treat a desktop client's secret as confidential), or build with `AMT_GOOGLE_CLIENT_ID` and
`AMT_GOOGLE_CLIENT_SECRET` set to embed them. After you save the source, a browser window opens to sign in and ask for
read-only access to your calendars and tasks; only a refresh token is kept. Steps:
[Create an OAuth client](docs/oauth-setup.md#google-create-an-oauth-client-in-google-cloud). Removing the source revokes the access at Google.
A calendar-only alternative that needs no OAuth client is Google Calendar's secret iCal address, added as an iCalendar feed.

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

### Search and saved filters

**Ctrl+F** (**Cmd+F** on macOS), or the magnifier in the toolbar, searches every event and task in the local cache, with
no network: type words, and every word must start a word in the title, notes or description, location, labels, calendar
or list name, or attendee; ignoring case and accents (`zolc` finds `żółć`). Put words in quotes to match a phrase. Choose
a result to see its details, or *Show in calendar* to jump to the day. In **All tasks**, the *Filters* chip saves the
current filter, sort and grouping under a name, so you can switch between setups such as "Work, due this week". See
[docs/design/search-and-saved-filters.md](docs/design/search-and-saved-filters.md).

### Launch at login

Settings → Startup → *Launch All My Tasks when I log in* adds a per-user login entry (Windows Run key, macOS launchd
agent, Linux XDG autostart file). *Start minimized to tray* makes that login launch use `--tray`; starting the app yourself
still opens the window. It is unavailable when the app is run through `dotnet run`. On Linux, only use the tray option with a
StatusNotifier host (see above). See [docs/design/launch-at-login.md](docs/design/launch-at-login.md).

### Reminders

Settings → Notifications shows a notification shortly before an event starts (10 minutes by default) and when a task is due
(tasks with a date but no time at 09:00). **Open**, **Snooze** and **Dismiss** are on the notification where the desktop
allows it. Reminders only appear while the app is running, so combine them with *Launch at login*. On Linux the app uses
`notify-send` (package `libnotify-bin` or `libnotify`); without it, and on Windows and macOS for now, a small window in
the corner of the screen is used. See [docs/design/reminders.md](docs/design/reminders.md).

## Project layout

| Path | What it is |
| --- | --- |
| `src/AllMyTasks` | The Avalonia desktop app (views and view models) |
| `src/AllMyTasks.Core` | UI-independent logic: tasks, providers, aggregation |
| `src/AllMyTasks.Providers.ICal` | iCalendar feed and CalDAV providers |
| `src/AllMyTasks.Providers.Todoist` | Todoist task provider (API token; read-only unless changes are allowed) |
| `src/AllMyTasks.Providers.Jira` | Jira Cloud issues assigned to you (email and API token, read-only) |
| `src/AllMyTasks.Providers.GitHub` | GitHub issues and pull requests assigned to you or awaiting your review (personal access token, read-only) |
| `src/AllMyTasks.Providers.Google` | Google Calendar and Google Tasks (OAuth, read-only) |
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
