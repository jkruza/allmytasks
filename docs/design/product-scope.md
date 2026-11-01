# Product scope and versions

Decided 2026-11-01. This page is the short answer to "what is this app for, and what comes when". The
[roadmap](roadmap.md) holds the detailed milestones and status; the [decision log](README.md#decision-log) holds the
reasons.

## Goal

**One read-only place to see the tasks and events from many task and calendar sources, to get a better overview
of the work to be done.**

The app looks; it does not manage. You keep using Todoist, Outlook, Google, Jira, GitHub and GitLab to create and
change things. All My Tasks reads them, keeps a local copy and answers "what is coming up, what is overdue, and
what has no date yet?" across all of them at once.

Read-only is a product choice, not only a stage of development. It keeps the app safe to try (a token that can
only read is easy to grant), keeps the sync model simple (no conflicts), and keeps the promise that nothing you
do here can damage data elsewhere. Version 2 revisits it deliberately, in a narrow way (see below).

## Version 1: a read-only overview

1. **A general framework.** A provider-neutral model for events and tasks, a provider interface, a local SQLite
   cache, a sync engine with backoff and status, credentials in the OS secret store. Largely built: see
   [architecture.md](architecture.md), [providers.md](providers.md) and [storage-and-sync.md](storage-and-sync.md).
2. **The most common services.**

   | Service | What is shown | Status | Design |
   | --- | --- | --- | --- |
   | Todoist | Tasks | Implemented (API token) | [todoist-provider.md](todoist-provider.md) |
   | Microsoft Outlook and To Do | Events, tasks (personal accounts) | Implemented | [outlook-calendar-provider.md](outlook-calendar-provider.md), [microsoft-todo-provider.md](microsoft-todo-provider.md) |
   | Google Calendar and Tasks | Events, tasks | Implemented | [google-calendar-provider.md](google-calendar-provider.md), [google-tasks-provider.md](google-tasks-provider.md) |
   | Jira | Issues assigned to me | Designed, [#71](https://github.com/jkruza/allmytasks/issues/71) | [jira-provider.md](jira-provider.md) |
   | GitHub | Issues and pull requests assigned to me or awaiting my review | Designed, [#72](https://github.com/jkruza/allmytasks/issues/72) | [github-provider.md](github-provider.md) |
   | GitLab | Issues and merge requests assigned to me or awaiting my review | Designed, [#73](https://github.com/jkruza/allmytasks/issues/73) | [gitlab-provider.md](gitlab-provider.md) |

   iCalendar feeds and CalDAV calendars (events) are also in, as the generic calendar sources.
3. **Useful views.**

   | View | What it answers | Status |
   | --- | --- | --- |
   | Calendar grids (day, week, month) | What is on, and when | Implemented, [views.md](views.md) |
   | Upcoming | What is next, events and tasks together | Implemented, [views.md](views.md#upcoming) |
   | All tasks | Everything open, with filters, sorting, grouping and saved filters | Implemented, [views.md](views.md#all-tasks) |
   | Inbox | Tasks without a date, to triage | Implemented, [#74](https://github.com/jkruza/allmytasks/issues/74), [inbox-view.md](inbox-view.md) |

Also in version 1 but not a goal in itself: reminders, system tray, launch at login, search, installers.

## Version 2: more services and careful write support

- **More services**, which ones still to be decided. Candidates are listed in [write-support.md](write-support.md#more-services)
  only as a prompt for that decision; none is committed.
- **Write support**, limited to three things: completing tasks, changing task dates, and rescheduling events.
  Not creating, editing text, moving or deleting. Tracking issue [#75](https://github.com/jkruza/allmytasks/issues/75),
  design in [write-support.md](write-support.md).

Todoist task completion ([#54](https://github.com/jkruza/allmytasks/issues/54), [complete-tasks.md](complete-tasks.md))
was built before this refocus. It stays, as an opt-in preview of version 2; see
[write-support.md](write-support.md#todoist-completion-today).

## Further research (not scheduled)

Collected in [research.md](research.md): runtime plug-in support ([#76](https://github.com/jkruza/allmytasks/issues/76)),
MCP support ([#77](https://github.com/jkruza/allmytasks/issues/77)), and AI summaries and planning with local models
([#78](https://github.com/jkruza/allmytasks/issues/78)). None of these changes version 1 or 2 plans unless a
research outcome is accepted as a decision.

## What does not change

Local-first (no backend, no account with us, no telemetry), every item comes from a provider (no local-only
items), credentials only in the OS secret store, UI-free Core with tests.
