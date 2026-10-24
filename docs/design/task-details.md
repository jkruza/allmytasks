# Task details

Clicking a task in any view shows everything the app knows about it. v1 is read-only, like
the rest of the app.

The presentation (a right-docked details pane, one at a time, Esc to close) is shared with
calendar entry details and is described in [details-pane.md](details-pane.md). This doc
covers what a task's details contain, where they come from, and how each view opens them.

## What is shown

Whatever the provider supplied; empty fields are left out rather than shown as blanks.

| Field | Source | Notes |
| --- | --- | --- |
| Title | `TaskItem.Title` | "(no title)" placeholder for empty titles, as in the lists; status glyph beside it |
| Status | `TaskItem.Status` | Open, In progress, Completed (with completion time), Cancelled; ", overdue" added when open and past due |
| Due | `TaskItem.Due` | Date only ("Fri 24 Oct") or date and time in the display zone |
| Starts | `TaskItem.Start` | "Do not show before"; shown only when set |
| Priority | `TaskItem.Priority` | High, Medium, Low; hidden for None |
| Notes | `TaskItem.Notes` | Plain text, selectable, wraps; capped at 20 000 characters like event descriptions |
| List and source | collection and source display names, collection colour | e.g. "Groceries · Todoist" |
| Labels | `TaskItem.Labels` | Comma-separated |
| Repeats | `TaskItem.Recurrence` | Plain-language summary from `RecurrenceDescriber`, shared with events |
| Subtask of | `TaskItem.ParentKey` | Parent's title as a button that opens the parent; shown when the parent is in the cache |
| Subtasks | tasks whose `ParentKey` is this task | Status glyph and title; a subtask can be clicked to open its details |
| Last changed | `TaskItem.LastModified` | Local date and time |
| Link | `TaskItem.Url` | An "Open link" button; only `http` and `https` links are offered |

## Where the data comes from

Views already hold a `TaskItem` (All tasks rows) or a `TaskEntry` (calendar views), but
neither has the subtasks or the source name, and a click should show what is in the cache
now. So the details are built from the store by key:

```csharp
public interface ITaskDetailsService
{
    Task<TaskDetails?> GetAsync(ItemKey key, TimeZoneInfo displayZone, CancellationToken ct = default);
}

public sealed record TaskDetails(
    TaskItem Task, string CollectionName, string? CollectionColor, string SourceName,
    TaskItem? Parent, IReadOnlyList<TaskItem> Subtasks, bool IsOverdue);
```

`TaskDetailsService` (Core, `AllMyTasks.Core.Views`) reads tasks from `IItemStore`,
collections and sources from `ISourceRepository`, and finds the parent and direct children
by `ParentKey`. It returns `null` when the task no longer exists, for example after a sync
removed it; the pane then closes. Subtasks are open first, then by due date (undated
last), then by title. A task that is its own parent has no parent, and a parent is never
listed as its own subtask, so cycles in `ParentKey` cannot loop.

`TaskDetailsBuilder` (Core) turns `TaskDetails` into `TaskDetailsContent`: the title,
status glyph, accent colour, `DetailRow`s (the same type the calendar entry details use,
empty ones left out), a safe link, capped notes, and `TaskRef`s for the parent and
subtasks. `TaskDetailsViewModel` only exposes that, so the wording and the field rules are
unit-tested without a UI.

## Opening details

| View | Gesture |
| --- | --- |
| All tasks | Click a row, or focus it and press Enter or Space |
| Day, Upcoming | Click an entry row that is a task |
| Week, Month | Click a task chip or bar |

All of them go through the `EntryActivation` attached behaviour described in
[details-pane.md](details-pane.md#the-pane-host); `CalendarShellViewModel.ShowDetailsAsync`
sends task entries to `ShowTaskDetailsAsync`, and events to the calendar entry details.
Clicking the parent or a subtask in the pane replaces the content with that task. If the
task is gone (`TaskDetailsService` returns `null`) the pane closes. A click that is still
loading when the user clicks something else or closes the pane is dropped, so a slow load
cannot reopen the pane. Like the rest of the pane, the content is a snapshot taken when it
was opened and is not refreshed by background syncs.

## Accessibility

Rows, chips and bars are focusable and activate with Enter and Space. The pane has an
automation name ("Details for task <title>"); opening moves focus to the Close button and
Esc closes it, as for every details view.

## Out of scope

Editing, completing from the pane, opening the provider's link, attachments and
reminders. The pane is the natural home for those once write support exists
([providers.md](providers.md#write-support-later)).

## Testing

- `TaskDetailsService` against the in-memory store: found, missing, collection and source
  names, parent, subtask order, self-parent, parent cycle, overdue by status and date.
- `TaskDetailsBuilder`: sparse and full tasks, empty title, glyph per status, completion
  time and due time in a non-UTC display zone, list text with unknown list or source,
  non-http links, notes cap, default colour, parent and subtask references.
- Pane wiring (click in each view, subtask navigation, Esc) is checked by hand; see the
  manual checklist in the pull request.
