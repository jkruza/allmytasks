# Design docs

Design for **All My Tasks**: one desktop app that shows calendars and tasks from many
providers in a single place, stored locally, with no backend of our own.

These docs describe the intended design. They are not a spec of what exists today; the
[roadmap](roadmap.md) says what is built and in which order. When code and docs disagree,
fix whichever is wrong in the same pull request.

## Contents

| Doc | Covers |
| --- | --- |
| [architecture.md](architecture.md) | Layers, project layout, data flow, dependency rules |
| [domain-model.md](domain-model.md) | Provider-neutral `CalendarEvent` and `TaskItem`, time handling, recurrence |
| [providers.md](providers.md) | Provider abstraction, capabilities, sync contract, errors |
| [ical-caldav-provider.md](ical-caldav-provider.md) | First provider: iCalendar feeds and CalDAV, read-only |
| [google-calendar-provider.md](google-calendar-provider.md) | Google Calendar provider: OAuth sign-in, sync tokens, mapping (implemented read-only, with Google Tasks) |
| [google-tasks-provider.md](google-tasks-provider.md) | Google Tasks provider: OAuth sign-in, `updatedMin` delta sync, mapping, limits (implemented read-only) |
| [microsoft-todo-provider.md](microsoft-todo-provider.md) | Microsoft To Do tasks via Microsoft Graph, personal accounts, read-only |
| [todoist-provider.md](todoist-provider.md) | Todoist task provider: API token auth, Sync API delta, mapping, read-only |
| [outlook-calendar-provider.md](outlook-calendar-provider.md) | Outlook calendar via Microsoft Graph (personal accounts), and the Microsoft sign-in and client id shared with To Do |
| [views.md](views.md) | Day agenda, week, month and upcoming views and the Core logic behind them |
| [toolbar.md](toolbar.md) | Main toolbar look (title and segmented view switcher) and the All tasks filter bar |
| [details-pane.md](details-pane.md) | Right-hand details pane and the calendar entry details shown in it |
| [app-icon.md](app-icon.md) | The three-lines logo as window, executable and tray icon, and how the files are generated |
| [system-tray.md](system-tray.md) | Tray icon and menu, `--tray` start option, close-to-tray, Linux StatusNotifier caveat |
| [launch-at-login.md](launch-at-login.md) | Settings option to start at login on Windows, Linux and macOS, optionally tray-only |
| [complete-tasks.md](complete-tasks.md) | Complete and reopen Todoist tasks from the app: write interface, pending-changes queue, opt-in per source |
| [reminders.md](reminders.md) | Desktop notifications before events and at task due times: planner, scheduler, per-OS notifier |
| [search-and-saved-filters.md](search-and-saved-filters.md) | Global search over events and tasks, and saved filters for All tasks |
| [meeting-url-and-attendees.md](meeting-url-and-attendees.md) | Join links and attendees on events: model, provider mapping, display |
| [packaging.md](packaging.md) | Installers and signing for Windows, macOS and Linux, shared release decisions |
| [storage-and-sync.md](storage-and-sync.md) | Local database, credentials, cache and refresh behaviour |
| [task-details.md](task-details.md) | Read-only details of a clicked task: fields, subtasks, where the data comes from |
| [settings.md](settings.md) | Settings dialog, persistence of app preferences, week start |
| [ci-docs-only-changes.md](ci-docs-only-changes.md) | How CI skips build and test for docs-only changes while required checks still report |
| [project-site.md](project-site.md) | The static project page published on GitHub Pages and how it is deployed |
| [roadmap.md](roadmap.md) | Milestones and open questions |

## Goals

- One **common model** for calendar events and one for tasks, whatever the provider.
- A **provider interface** so adding Todoist, Google, Microsoft or CalDAV does not touch
  views or storage.
- Four **calendar views**: day agenda, week, month, upcoming; plus a separate
  **All tasks** view with filtering.
- **Local-first**: all data and credentials stay on the user's machine. No cloud backend,
  no account with us, no telemetry.
- First calendar provider is **iCalendar / WebDAV (CalDAV), read-only**.

## Non-goals (for now)

- Writing back to providers (create, edit, delete, complete). The design leaves room for
  it, see [providers.md](providers.md#write-support-later), but v1 is read-only.
- **Local-only items.** Every event and task comes from a configured provider; the app
  has no built-in store of its own items and no "Local" provider.
- Sync between a user's own devices.
- Attendee management, invitations, free/busy scheduling.
- Mobile or web builds.
- Reminders and notifications. Likely soon after v1, but not designed here.

## Decision log

Short record of decisions that shape everything else. Change one by editing this table
and the doc it points to.

| # | Decision | Why | Details |
| --- | --- | --- | --- |
| 1 | Core is UI-free; views only render what Core computes | Testable layout logic, matches existing `CLAUDE.md` rule | [architecture.md](architecture.md) |
| 2 | Events and tasks are separate types sharing a small set of concepts, not one type | They differ in time semantics, completion and recurrence; one type becomes a bag of nullable fields | [domain-model.md](domain-model.md) |
| 3 | All-day and timed values are distinct types, never a `DateTimeOffset` at midnight | Midnight-as-all-day breaks across time zones | [domain-model.md](domain-model.md#time) |
| 4 | Providers return normalised data; recurrence is expanded in Core, not by providers | One implementation, same behaviour for every provider | [domain-model.md](domain-model.md#recurrence) |
| 5 | Providers sync into a local SQLite cache; views read only from the cache | Instant startup, offline use, one query path | [storage-and-sync.md](storage-and-sync.md) |
| 6 | Credentials live in the OS secret store, never in the database or config files | Matches `CLAUDE.md`: keep credentials out of the repo and logs | [storage-and-sync.md](storage-and-sync.md#credentials) |
| 7 | Provider capabilities are expressed as small interfaces, not flags on one big one | A read-only source cannot be handed to code that writes | [providers.md](providers.md#shape) |
| 8 | Tasks get their own "All tasks" view (list + filters); the four calendar views show dated items only | Undated tasks have no place on a calendar; a list suits triage | [views.md](views.md#all-tasks) |
| 9 | Linux secrets: Secret Service (GNOME Keyring / KWallet) first, else app-level encryption keyed from a 6-digit PIN | Matches the other OSes when a keyring exists; an explicit, honest fallback when it does not | [storage-and-sync.md](storage-and-sync.md#credentials) |
| 10 | First provider handles both plain `.ics` URLs and CalDAV | `.ics` is the simplest useful thing; CalDAV reuses the same parsing | [ical-caldav-provider.md](ical-caldav-provider.md) |
| 11 | Week start is Sunday or Monday, default follows the OS culture (Monday when the culture starts elsewhere) | Right for most users on first run; two options is all that was asked for | [settings.md](settings.md#week-start) |
| 12 | Details of a clicked entry open in a docked right pane, not a popover or dialog; one host shows any kind of details | Same behaviour in every view, no window ownership issues, room for long text | [details-pane.md](details-pane.md) |
