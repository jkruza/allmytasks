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
| Title | `TaskItem.Title` | "(no title)" placeholder for empty titles, as in the lists |
| Status | `TaskItem.Status` | Open, In progress, Completed (with completion time), Cancelled; "Overdue" badge when open and past due |
| Due | `TaskItem.Due` | Date only ("Fri 24 Oct") or date and time in the display zone |
| Start | `TaskItem.Start` | "Do not show before"; shown only when set |
| Priority | `TaskItem.Priority` | High, Medium, Low; hidden for None |
| Notes | `TaskItem.Notes` | Plain text, selectable, wraps |
| List and source | collection and source display names, collection colour | e.g. "Groceries · Todoist" |
| Labels | `TaskItem.Labels` | Comma-separated |
| Repeats | `TaskItem.Recurrence` | The rule text, shown as provided |
| Parent | `TaskItem.ParentKey` | Parent's title, when it is in the cache |
| Subtasks | tasks whose `ParentKey` is this task | Status glyph and title; a subtask can be clicked to open its details |
| Last modified | `TaskItem.LastModified` | Local date and time |
| Link | `TaskItem.Url` | Shown as text with a copy button; not opened automatically (no link-launching in v1) |

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
removed it; the pane then closes. Subtasks are sorted like the All tasks list (open first,
then by due date, then title) and cycles in `ParentKey` are ignored, as in the list.

Formatting (dates, glyphs, empty-field hiding) lives in `TaskDetailsViewModel`, which stays
thin and is tested through a Core-level `TaskDetailsText` helper where logic is involved.

## Opening details

| View | Gesture |
| --- | --- |
| All tasks | Select a row (click, or Enter on the focused row) |
| Day, Upcoming | Click an entry row that is a task |
| Week, Month | Click a task chip |

All entries open the same pane; events go to the calendar entry details, tasks to this
view model. Clicking a subtask in the pane replaces the pane content with that subtask.
The pane stays open across a refresh and re-reads the task; if the task disappeared it
closes.

## Accessibility

Rows and chips are focusable and activate with Enter and Space. The pane has an
automation name ("Task details") and takes focus when opened; Esc closes it and returns
focus to the row that opened it.

## Out of scope

Editing, completing from the pane, opening the provider's link, attachments and
reminders. The pane is the natural home for those once write support exists
([providers.md](providers.md#write-support-later)).

## Testing

- `TaskDetailsService` against the in-memory store: found, missing, parent, subtasks,
  parent cycle, collection and source names.
- `TaskDetailsViewModel`: which fields are present for a sparse task and a full one,
  overdue badge, completed task, subtask navigation.
