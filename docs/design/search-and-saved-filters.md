# Search and saved filters

Tracked in [#56](https://github.com/jkruza/allmytasks/issues/56). Implemented; see [Implementation status](#implementation-status)
for where the code differs from this design.

Two related features: a **global search** that finds events and tasks from every source, and **saved filters** for
the All tasks view.

## Today

- All tasks has a text filter over title and notes ([views.md](views.md#all-tasks)). It is a filter on the task list,
  not a search.
- The calendar views have no search.
- Filter, sort and grouping state for All tasks is remembered between sessions, but there is only one such state.

## Goals

- One search box finds any event or task by what the user remembers about it.
- Searching never needs the network; it reads the local cache.
- Saved filters let users keep a few named All tasks setups ("Work, due this week") and switch between them.

## Non-goals

- Searching provider data that is not cached (for example events outside the sync window).
- Full-text ranking, fuzzy matching or search operators (`from:`, `before:`) in v1.
- Saved filters for the calendar views.
- Sharing or exporting filters.

## Global search

### Behaviour

- Opened with Ctrl+F (Cmd+F on macOS) from anywhere in the main window, or a toolbar button. It shows a results
  list in the main area with the query box focused; Esc closes it.
- Results are grouped into **Events** and **Tasks**, each sorted: events by next start (upcoming first, then past by
  most recent), tasks by due date with undated last.
- Selecting a result opens it in the [details pane](details-pane.md). For an event, a **Show in calendar** action
  switches to the day view on its date.
- A recurring event appears once (its series, with the next occurrence shown), not once per occurrence.
- Respects the shared collection visibility: hidden collections are not searched, with a hint showing how many results
  were hidden.

### Matching

The query is split on whitespace; every word must match (AND). A word matches when it is a prefix of a word in any of
these fields, compared case-insensitively and ignoring diacritics (so "zolc" finds "żółć"):

| Item | Fields |
| --- | --- |
| Event | title, description, location, collection name; attendees once [attendees](meeting-url-and-attendees.md) exist |
| Task | title, notes, labels, collection name |

A word in quotes must match as a whole phrase. Normalisation is Unicode normalisation form D with combining marks
removed, plus case folding, applied to both sides.

### Core

```csharp
public sealed record SearchQuery(string Text, IReadOnlySet<string>? CollectionIds, int Limit);
public sealed record SearchResults(IReadOnlyList<EventHit> Events, IReadOnlyList<TaskHit> Tasks, int HiddenByFilter);

public interface ISearchService
{
    Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct);
}
```

The matching function (`TextMatcher.Matches(query, fields)`) is pure and shared with the existing All tasks text
filter, so both behave the same.

### Implementation approach

Start with an in-memory search over the store's items: a personal calendar and task list is thousands of items, not
millions, and scanning them with a normalised cached search text is fast enough. Normalised text is computed when an
item is written to the store (a `SearchText` column) so it is not recomputed per keystroke. Move to SQLite FTS5
(`unicode61 remove_diacritics 2`) only if measured latency on a large synthetic data set is a problem; that needs a
check that the bundled SQLite build includes FTS5 and is a store-internal change.

The view model debounces keystrokes (about 150 ms) and cancels the previous search.

## Saved filters

### Behaviour

The All tasks filter bar gets a **Filters** menu:

- **Save current as...** asks for a name and stores the current filter, sort and grouping.
- A list of saved filters; choosing one applies it. The active one is shown, and shown as modified when the user
  changes a control afterwards, with **Update** and **Revert** actions.
- Rename, delete and reorder.
- **Clear** returns to the default (Open, any due date).

Built-in filters are not shipped in v1; the user's list starts empty.

### Data

```csharp
public sealed record SavedTaskFilter(
    Guid Id, string Name, int Version, TaskFilter Filter, TaskSort Sort, TaskGrouping Grouping);
```

Stored as JSON in the settings store under one key, with a schema `Version` so the format can change. `Due` filters
are relative buckets (Today, Next 7 days) and so stay meaningful; a custom range is stored as dates.

Collection filters refer to collection ids. When a source or collection is removed, its ids are dropped from saved
filters on load. A filter that ends up with no collections it could match keeps its other conditions and shows a small
notice instead of silently showing everything.

## Tests

- `TextMatcher`: prefix and phrase matching, case and diacritic folding (Polish letters, German sharp s), empty query.
- `SearchService`: events and tasks, recurring series once, hidden collections and the hidden count, limit.
- Saved filters: round trip through JSON, version handling, unknown fields tolerated, removed collections dropped,
  rename and reorder.
- View model: debounce and cancellation; active/modified state of a saved filter.

## Docs to update with the implementation

[views.md](views.md#all-tasks) and [toolbar.md](toolbar.md#all-tasks-filter-bar), [settings.md](settings.md)
(persisted keys), [storage-and-sync.md](storage-and-sync.md) (`SearchText`), the README and the
[roadmap](roadmap.md).

## Implementation status

Core: `AllMyTasks.Core.Search` (`TextQuery`, `TextMatcher`, `SearchService`, the hit and result records) and
`AllMyTasks.Core.Views` (`SavedTaskFilter`, `SavedFilterStore`). App: `SearchViewModel` and `SearchView`, the shell's
`OpenSearchCommand`, and the *Filters* chip in `TasksView`. Differences from the design above:

- **No `SearchText` column.** Search normalises fields as it scans. Nothing is stored, so there is no migration. Move to
  the column or FTS5 only if measured latency on a large synthetic data set needs it.
- **`ISearchService.SearchAsync` also takes the display zone**, which ordering needs. `SearchQuery.Collections` is a set
  of `CollectionRef` (source and collection), since a collection id is only unique within its source.
- **A recurring series is placed at its next occurrence** by asking the recurrence expander for the next three years, or
  for the latest past occurrence when none is left. Overrides do not appear separately.
- **Matching** is word-prefix over normalised words; a quoted phrase must start at a word and may end inside the last
  one. Letters that do not decompose (ł, ø, đ, ß, æ, œ) are mapped by hand, so `zolc` finds `żółć` and `strasse` finds
  `Straße`. Event matching also covers the organiser. The All tasks text filter uses the same rule over title and notes,
  so it no longer matches in the middle of a word (`port` does not find `report`).
- **Search results:** at most 100 events and 100 tasks. Events whose calendar is hidden are counted in the hint, not listed.
- **Saved filters** are stored as `{"version":1,"filters":[...]}` (the version is on the document, not on each filter) under
  `filters.tasks`. A filter that cannot be read is skipped. The filter bar has no collection control and only one due
  option, so a saved filter may hold more than the controls show (several due options, a custom range, collections);
  these are kept and applied while the filter is active, and dropped by *Clear* or by choosing a due option. Collections
  that no longer exist are removed on load and apply, and the bar says so.
- **Saving under a name that exists replaces that filter.** The name box in the menu is used for both *Save as new* and *Rename*.
- The id of the saved filter in use is remembered between sessions (`view.tasks`, `FilterId`).

Not done: view model tests for the debounce and the active/modified state (the app has no test project; the logic they
rely on, `SavedTaskFilter.Matches`, is tested in Core), a collection control in the filter bar, and search in the calendar
views.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Search past events without limit? | Whatever the cache holds; the sync window already bounds it |
| 2 | Keep the All tasks text filter as is, next to global search? | Yes; it filters the list in place, search jumps across everything |
| 3 | Saved filters per task source kind (for example only Todoist)? | The collection filter already covers it |
