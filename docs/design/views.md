# Views

Four calendar views of the same data: **day agenda**, **week**, **month**, **upcoming**,
plus a separate list view, [**All tasks**](#all-tasks), that is not tied to dates.
All share the same inputs (cached events and tasks), the same filters, and the same
`AgendaEntry` output type. They differ only in the date range queried and how entries are
arranged.

## Principle: layout logic lives in Core

Avalonia views render. Anything that decides *what goes where* is plain C# in
`AllMyTasks.Core/Views/` (namespace `AllMyTasks.Core.Views`), unit-tested without a UI:
grouping by day, splitting multi-day events, assigning overlap columns, building the
month grid, week-start rules, "today" boundaries, time zone conversion.

```
View (axaml) → ViewModel (thin) → Core query → DayAgenda / WeekLayout / MonthGrid / UpcomingList
```

## Toolbar layout

The main window toolbar has the title and a segmented view switcher on the first row and
navigation, filter, sync, settings and refresh on the second. Rows are grids whose title column
trims, so nothing overlaps down to the window's minimum width. See [toolbar.md](toolbar.md).

## Shared query

```csharp
public sealed record ViewQuery(
    DateOnly From,                     // inclusive, in display zone
    DateOnly ToExclusive,
    TimeZoneInfo DisplayZone,
    ViewFilter Filter);                // visible collections, show events/tasks/both, show completed

public interface IAgendaService
{
    Task<IReadOnlyList<AgendaEntry>> GetEntriesAsync(ViewQuery query, CancellationToken ct = default);
}
```

`AgendaService` reads from the store, expands recurrences for the range, converts to the
display zone, drops entries from hidden collections, and sorts. Every view starts from
this list.

Entries are normalised into **day segments**:

- A timed event on one day: one segment.
- A timed event crossing midnight: one segment per day, each flagged as continued from
  before / continuing after.
- An all-day (or multi-day) event: one segment per day, or one span for views that draw
  spans (week header, month grid).
- A task with a due date: one segment on that day; date-only tasks are "all-day-like",
  timed tasks sit at their time.
- Overdue open tasks are shown on **today** (and in a separate "Overdue" group), not on
  their original day, in agenda and upcoming; the week and month grids show them on their
  actual due date.

## Day agenda

The default view. A vertical list for one day, navigable by day.

Sections, in order:

1. **All day**: all-day events and date-only tasks due that day.
2. **Timeline**: timed events and timed tasks sorted by start time; overlapping events
   shown stacked, not side by side.
3. **Overdue** (today only): open tasks past due.

Undated tasks are not shown in any calendar view; they appear only in
[All tasks](#all-tasks).

Behaviour:

- "Now" marker between past and upcoming entries when viewing today.
- Past entries of today are dimmed, not hidden.
- Entry shows: time range (or "All day"), title, collection colour, location if present,
  and a source badge. Tasks show a checkbox glyph (display only in read-only v1).
- Empty state: a quiet "Nothing planned" with the next entry's date if there is one.

Core type: `DayAgenda(DateOnly Date, IReadOnlyList<AgendaEntry> AllDay, IReadOnlyList<AgendaEntry> Timed, IReadOnlyList<AgendaEntry> Overdue)`.

## Week

Seven columns (or five with a "hide weekends" setting) with an hour grid.

- **First day of week**: from the OS culture by default, overridable in Settings
  (Sunday/Monday), see [settings.md](settings.md#week-start).
- All-day events and date-only tasks in a header strip above the grid; multi-day events
  span columns.
- Timed events positioned by start/end on the time axis. Overlaps are resolved by the
  standard column-packing algorithm: sort by start, assign each event the first free
  column in its overlap cluster, then width = 1 / cluster column count.
- Visible hours default to working hours (08:00-20:00) and expand to fit the earliest and
  latest event in the week; scrolls to the current time on open.
- A DST-change week has 23 or 25 hour days. Time positions are computed from real
  elapsed time, not by assuming 24 rows.

Core type: `WeekLayout(IReadOnlyList<DayColumn> Days, IReadOnlyList<SpanBar> AllDaySpans)` where `DayColumn` holds `PositionedEntry(entry, startMinute, endMinute, column, columnCount)`.

## Month

A 6×7 grid (always six rows so the layout does not jump between months).

- Leading/trailing days from adjacent months are shown dimmed and **do** include their
  entries.
- Each cell shows up to N entries (N from available height); the rest collapse into
  "+3 more", which opens that day in the day agenda.
- Multi-day and all-day events render as bars across cells, laid out per week row with the
  same lane-packing idea as the week header.
- Timed events render as a one-line chip with start time and title.
- Tasks show as a small marker plus title; a count badge is enough when space is tight.

Core type: `MonthGrid(DateOnly Month, IReadOnlyList<WeekRow> Rows)` with `WeekRow(Days, SpanBars with lane numbers)`.

## Upcoming

A flat, scrolling list grouped by day for the next N days (default 14), meant as the
"what's next" glance and the likely tray/compact view later.

- Groups: **Overdue**, **Today**, **Tomorrow**, then a heading per weekday/date.
- Within a group: all-day first, then by time.
- Starts from *now*: events that have already ended today are not listed.
- Load more by extending the range (infinite scroll), not paging.
- Days with nothing are skipped; no empty headings.
- Optional filter: events only, tasks only, both.

Core type: `UpcomingList(IReadOnlyList<DayGroup>)`.

## All tasks

A flat list of tasks from every source, independent of date. It is where undated tasks
live and where the user triages everything.

Rows: checkbox glyph (a button that completes or reopens the task when its source allows changes, see
[complete-tasks.md](complete-tasks.md); display only otherwise), title, due (date or date+time,
overdue highlighted), collection badge and colour, priority marker, labels, and a
subtask indicator. Subtasks nest under their parent (`ParentKey`); a subtask whose parent
is filtered out is shown at top level so it never disappears. Clicking a row (or Enter on it, or Space where the task cannot be ticked) opens its [details](task-details.md) in the pane.

### Filtering

The controls (search, status switcher, one chip per filter, a sort and group menu) are
described in [toolbar.md](toolbar.md#all-tasks-filter-bar).

Filters combine with AND; within one filter, multiple choices combine with OR.

| Filter | Options |
| --- | --- |
| Status | Open (default), Completed, Cancelled, All |
| Due | Overdue, Today, Next 7 days, This month, Has no due date, Any (default), Custom range |
| Source / collection | Multi-select; follows the shared collection visibility |
| Priority | Any, or one or more of High / Medium / Low / None |
| Labels | Multi-select; any-of or all-of toggle |
| Text | Case-insensitive substring over title and notes |

### Sorting and grouping

- Sort: due date (default, undated last), priority, title, source, last modified.
  Ascending or descending.
- Group by: none (default), due bucket (Overdue / Today / This week / Later / No date),
  collection, priority, or label.
- Sorting is stable, so equal keys keep their previous order.

### Core types

```csharp
public sealed record TaskFilter(
    TaskStatusFilter Status, DueFilter Due, IReadOnlySet<string> CollectionIds,
    IReadOnlySet<Priority> Priorities, IReadOnlySet<string> Labels, bool AllLabels,
    string? Text);

public sealed record TaskListQuery(TaskFilter Filter, TaskSort Sort, TaskGrouping Grouping,
                                   TimeZoneInfo DisplayZone);
```

`ITaskListService.GetAsync(query)` returns `TaskGroup`s of `TaskRow`s (with nested
subtasks). Filtering is a pure function over `TaskItem`s, so it runs the same in tests, in
the store query (where it can be pushed down to SQL) and in memory. Text and label
filters re-run on every keystroke, so the view model debounces them and the service
supports cancellation.

Filter, sort and grouping state is remembered between sessions. Saved/named filters are a
possible later addition, not v1.

The calendar views' "show tasks" toggle and this view's filters are separate: the shared
calendar filter only controls which collections and item kinds appear on the calendar.

## Navigation and shared state

One `CalendarShellViewModel` owns:

- the **current date** and **view mode**; switching view keeps the date,
- previous / next / today commands (step = day, week, month, or none for upcoming),
- the **filter** (which collections are visible, events/tasks toggle, show completed),
- a **refresh** command and per-source status (last synced, errors).

Per-view view models are thin: ask `IAgendaService` for the range implied by the shell's
state, hand the result to the matching Core layout builder, expose it for binding.

The All tasks view has its own view model and filter state, but shares the collection
visibility and display zone with the shell.

Re-query triggers: date or mode change, filter change, store-changed notification from the
sync engine, and midnight rollover (timer in the shell using `TimeProvider`).

Keyboard (proposed): `T` today, `D/W/M/U` switch view, `←/→` previous/next, `R` refresh.

## Entry details

Clicking an entry in any calendar view (or focusing it and pressing Enter or Space) opens a
read-only details pane on the right; Esc closes it. The text is built in Core
(`EntryDetailsBuilder`, `RecurrenceDescriber`), the pane is a shared host in the main window.
See [details-pane.md](details-pane.md). Tasks (dated ones in the calendar views and every
row of All tasks) open [task details](task-details.md) instead.

## Rendering notes (Avalonia)

- Compiled bindings everywhere, so every view and `DataTemplate` needs `x:DataType`.
- Timeline and month grid use custom panels (`Panel` subclasses) positioned from
  precomputed layout values; no layout maths in code-behind.
- Virtualise the upcoming list; it is the only unbounded one.
- Colours come from the collection colour with a contrast-safe text colour computed in
  Core; high-contrast and dark themes must not produce unreadable chips.
- Accessibility: each entry exposes a single spoken string ("Team sync, 10:00 to 10:30,
  Work calendar"); entries are focusable and open the details pane with Enter or Space.

## Testing

Core layout code is the bulk of the logic, and all of it is deterministic given a fixed
`TimeProvider` and zone:

- overlap packing (none, chain, full cluster, touching but not overlapping),
- multi-day split at midnight, DST 23h/25h days, events spanning a DST change,
- all-day events do not shift when the display zone changes,
- first-day-of-week variants and month grids for months starting on each weekday,
- task filter combinations, sort stability, grouping buckets at day boundaries, subtask
  nesting when the parent is filtered out,
- overdue placement rules, "now" cut-off in upcoming,
- recurrence occurrences appearing in range, overrides replacing occurrences.

## Implementation notes (M2)

- `IItemStore` and `ISourceRepository` have in-memory implementations (`InMemoryStore`);
  `SampleData` fills it through the normal sync path with deliberately awkward data.
- `ViewQuery.IncludeOverdue` makes the service return open overdue tasks from before the
  range; day agenda and upcoming set it, week and month do not.
- `DueBucket` has an extra `Earlier` bucket for closed tasks whose due day is past.
- `TaskFilter` collections use `CollectionRef(SourceId, CollectionId)`; the All tasks UI
  follows the shared collection visibility instead of offering a second collection picker.
- Month cells show a fixed number of items (3, minus bar lanes) plus "+N more"; sizing by
  available height is not done.
- Filter, sort and grouping state is not yet remembered between sessions (needs the settings
  table from M3).
- `TimelinePanel` and `SpanBarPanel` read placement from child view models (`ITimelinePlacement`,
  `ISpanPlacement`); they do no layout decisions.

## Open questions

- Do tasks show in week/month by default, or behind a toggle? Proposal: shown, with the
  events/tasks toggle in the shared filter.
- Show week numbers? Cheap in Core; decide on UI taste.
