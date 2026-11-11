# Detect duplicate events and tasks across sources

Tracked in [#137](https://github.com/jkruza/allmytasks/issues/137). **Status: implemented for events, version 1; task matching is a first cut.**

## Goal

The same meeting often reaches the app twice: an invitation in Outlook that was also forwarded to Google, or a work
calendar shared into a personal one. Issues can also appear twice, for example a Jira issue mirrored in GitHub.
Duplicates inflate the overview and trigger two reminders. Show one entry.

## Principles

- **Never delete or change anything**. Duplicates are hidden in the view only; the cache keeps both items.
- **Conservative**: a false merge hides something real, which is worse than showing a duplicate. Only merge on
  strong evidence.
- **Visible and reversible**: the surviving entry says "Also in Google Calendar" and can show the hidden one.
- **On by default for events, off for tasks**, each with a setting.

## Events

Two events from different sources are duplicates when all hold:

1. Same start and end (to the minute; all-day: same dates).
2. Same normalised title (trimmed, case-folded, accents ignored).
3. At least one of: same iCalUID (iCalendar `UID` carried in the model when a provider exposes it), same meeting
   URL, same organiser address, or the same location text.

An iCalUID match alone is enough, because invitations keep it across calendars. Time and title alone are not
enough (two different "Standup" meetings can coincide).

## Tasks

Off by default. When on: same normalised title and same due date, from different sources, plus the same URL or an
explicit "linked issue" reference when a provider supplies one. Mirrored issues usually carry the other tracker's
URL in the description, but parsing that is out of scope for the first cut.

## Which entry survives

Source priority, set by the user as an ordered list in Settings (default: the order sources were added). The
survivor takes the title and time of the highest-priority source; the others are listed under "Also in".

## Core changes

`DuplicateFinder` over cached items returns groups with a survivor. It sits between the store query and the view
layout in `IAgendaService`/`ITaskListService`, so every view, search and reminders agree. Reminders are planned on
survivors only.

## Testing

Same UID, same URL, same time different title (not merged), same title different time (not merged), three-way
group, priority change, hidden source, recurring series with one moved occurrence, setting off.

## Implementation notes

- `CalendarEvent.Uid` carries the iCalendar UID: Google `iCalUID`, Outlook `iCalUId`, iCalendar feeds and CalDAV `UID`.
  It is stored in the item JSON, so no migration is needed; events cached before this change get a UID on their next
  sync.
- `DuplicateFinder.Collapse` runs in `AgendaService` after recurrences are expanded, so every calendar view, upcoming
  and reminders see the same list. A UID match still requires the same start and end, because every occurrence of a
  series shares one UID. Cancelled (including declined) copies are never merged with live ones.
- A task is a duplicate only with the same title, due value **and** link, from different sources. Setting is off by
  default. The task list, Inbox and search are not collapsed yet.
- Survivor: the source added first. The user-ordered priority list from the design is supported by the finder
  (`DuplicateOptions.SourcePriority`) but has no editor yet.
- The survivor lists the others under "Also in" in its details pane. Settings, Calendar: two checkboxes
  (`hideDuplicateEvents` on, `hideDuplicateTasks` off).
- Hidden duplicates are excluded from anything built on the agenda, which includes the workload glance.

## Open questions

- Does the model carry `UID` and organiser for every provider? Google, Outlook and iCalendar do; check Todoist, Jira
  and others before the task part.
- Should a hidden duplicate still count in the workload glance? Proposal: no.
