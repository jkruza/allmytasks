# Search and saved filters

Tracked in [#56](https://github.com/jkruza/allmytasks/issues/56).

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

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Search past events without limit? | Whatever the cache holds; the sync window already bounds it |
| 2 | Keep the All tasks text filter as is, next to global search? | Yes; it filters the list in place, search jumps across everything |
| 3 | Saved filters per task source kind (for example only Todoist)? | The collection filter already covers it |
