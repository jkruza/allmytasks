# Write support (version 2)

Tracked in [#75](https://github.com/jkruza/allmytasks/issues/75). **Status: partly built (Todoist completion
only); the rest is a plan for version 2.**

Version 1 is a read-only overview ([product-scope.md](product-scope.md)). Version 2 adds a small, careful set of
writes so that the things you do right after looking at the overview can be done without leaving it.

## Scope of version 2 writes

Exactly three actions, nothing more:

1. **Complete and reopen a task.**
2. **Change a task's date** (due date, or clear it).
3. **Reschedule an event** (move its start and end; keep its duration unless told otherwise).

Not planned: creating items, editing titles or notes, moving between lists, deleting, accepting or declining
invitations, changing attendees. If one of these is wanted later it needs its own design.

## Rules that apply to every write

- **Opt-in per source**, off by default, with a clear statement of what the source's token or scope will then
  be allowed to do. Existing read-only sources stay read-only until the user switches this on.
- **Minimal scopes**, requested only when the user opts in (for Google and Microsoft, by incremental authorisation).
- **Local first, then delivered**: the change shows at once, is stored in the pending-changes queue defined in
  [complete-tasks.md](complete-tasks.md), survives a restart, and is shown as failed if it cannot be delivered.
  Nothing is dropped silently.
- **Conflicts**: the provider's version tag (ETag, `updated`, sync token) is sent where the API supports it. A
  conflict is shown to the user and the remote value wins unless they retry. No merging.
- **Interfaces**: the capability interfaces already reserved in [providers.md](providers.md#write-support-later).
  `IWritableTaskSource` gains `SetDueAsync`; a new `IWritableCalendarSource` has only `RescheduleAsync`. A source
  that does not implement them is simply read-only.

## Per provider

| Provider | Complete | Change date | Reschedule event | Notes |
| --- | --- | --- | --- | --- |
| Todoist | Built (preview) | Planned | n/a | Recurring tasks: change the date of this occurrence only, never rewrite the recurrence text |
| Google Tasks / Calendar | Planned | Planned | Planned | Needs the broader scope; the project OAuth client must cover it |
| Microsoft To Do / Outlook | Planned | Planned | Planned | `Tasks.ReadWrite`, `Calendars.ReadWrite`; only events the user organises can be moved |
| Jira, GitHub, GitLab | Open question | Jira `duedate`, GitLab `due_date` possible; GitHub has none | n/a | "Complete" would mean closing an issue, which is a workflow decision, so these start read-only in version 2 and need their own proposal |
| iCalendar feed | n/a (feeds are read-only) | n/a | n/a | |
| CalDAV | n/a | n/a | Possible | `PUT` with `If-Match`; VTODO is not read yet |

## Todoist completion today

Completing and reopening Todoist tasks ([#54](https://github.com/jkruza/allmytasks/issues/54),
[complete-tasks.md](complete-tasks.md)) was built before the refocus on a read-only overview. Decision:

- **Keep it**, not delete it. It is the first real use of the write path and the pending-changes queue that
  every version 2 write needs, and it is opt-in and off by default.
- **Present it as a preview of version 2**, not a version 1 feature. README, site and `complete-tasks.md` say so.
  Version 1 documentation describes the app as read-only unless a source's "Allow changes" is switched on.
- **No extension in version 1**: no completion for other providers and no new write actions until version 2 starts.

Open for the maintainer: whether the "Allow All My Tasks to change tasks" option should also be labelled
"Preview" in the app itself. That is a small code and UI-text change, kept out of this documentation change.

## More services

Version 2 also adds more services, still to be chosen. Candidates raised so far, only as input to that decision:
Microsoft work and school accounts (already deferred), Linear, Asana, Trello, Notion, Apple Reminders, TickTick,
Azure DevOps, Bitbucket, Basecamp. Choose by user demand and by whether the API has a usable delta feed and a
sign-in the app can handle without a project-owned secret.

## Open questions

- Should a reschedule be allowed on events with attendees? Proposal: only where the user is the organiser.
- Undo: one level of undo for a delivered change? Proposal: yes for date changes, no for completing recurring tasks.
