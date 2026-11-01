# Toolbar and All tasks filter bar

Status: implemented. Issue: [#50](https://github.com/jkruza/allmytasks/issues/50).

Two pieces of chrome that Jarek found messy or unlike the [project site](project-site.md)
mockup: the main window toolbar and the filter controls of the [All tasks](views.md#all-tasks)
view.

## Goals

- The toolbar looks like the mockup on the site: the page title on the left, a quiet
  segmented switcher (Day, Week, Month, Upcoming, Inbox, All tasks) on the right, the active view
  shown by a soft background and semi-bold text, the others muted.
- The All tasks filters are scannable at a glance and cost one click to change, instead of
  eleven controls in one wrapping row.
- Nothing overlaps at the minimum window width (640 px). This keeps the earlier narrow-window fix.

## Main toolbar

Two rows in one bordered strip.

| Row | Left | Right |
| --- | --- | --- |
| 1 | Title (22 px, semi-bold, trimmed with an ellipsis when short of room) | Segmented view switcher |
| 2 | Previous, Today, Next | Sync progress, Filter menu, sources status, Refresh and Settings (both 14 px icons) |

- The switcher uses the `seg` style on `ToggleButton` (defined in `App.axaml`): no border, 3 px
  corner radius, muted at rest, filled with the theme's low-contrast base brush when checked.
- Row 2 and the utility buttons use the `tool` style: flat until hovered.
- Overlap is avoided by structure, not by wrapping: each row is a `Grid` whose left column is
  `*` (it shrinks and the title trims) and whose right column is `Auto`. At 640 px the
  widest right group (the switcher, about 330 px) leaves room for the left side.
- Behaviour, bindings, keyboard shortcuts and tooltips are unchanged.

## All tasks filter bar

```
[ Filter this list by title and notes ................]  Open  Completed  Cancelled  All
( Filters ⌄ ) ( Due date ⌄ ) ( Priority ⌄ ) ( Labels ⌄ ) Clear filters            12 tasks  Sort: Due date ↑ ⌄
```

- **Row 1**: the search box takes the free width; status is the same segmented control as the
  toolbar (one choice, four options), since it is the filter people change most.
- **Row 2**: the *Filters* chip, then one chip per filter. A chip opens a small flyout with only that filter's controls:
  - *Filters*: the saved filters (click one to apply it; ↑ ↓ reorder, ✕ deletes), a name box with **Save as new**, and for
    the active one **Update**, **Revert** (only while modified) and **Rename**, plus **Clear**. The chip shows the active
    filter's name, with `(modified)` after a change. See [search-and-saved-filters.md](search-and-saved-filters.md#saved-filters).
  - *Due date*: the due options list.
  - *Priority*: High, Medium, Low, None as checkboxes.
  - *Labels*: comma-separated text and a "must have all of them" checkbox.
- A chip with a value is outlined in the accent colour and says what it holds, for example
  `Priority: High, Medium`, so the active filters are readable without opening anything.
- **Clear filters** appears only while something narrows the list (status other than Open, a
  due option, priorities, labels or search text) and resets all of them. Sort and grouping are
  not filters and are not reset.
- **Sort** is one button, `Sort: Due date ↑`, whose flyout holds sort field, direction and
  grouping. The result count sits next to it.
- The chip row is a `WrapPanel`, so chips wrap on narrow windows.

No filter semantics change: the same `TaskFilter` is built, and persisted state (`TasksState`) is
unchanged. The view model only gained presentation properties (`PriorityChip`, `DueChip`,
`LabelsChip`, `SortSummary`, `HasActiveFilters`, the status flags) and `ClearFiltersCommand`.

## Search button and shortcut

A magnifier button in the second toolbar row, left of Refresh, and **Ctrl+F** (**Cmd+F** on macOS) open
[global search](search-and-saved-filters.md#global-search) in place of the current view, with the query box focused.
**Esc** closes the details pane first, then the search. Choosing a view button, or pressing its key, leaves the search.
Search works while typing in a text box, unlike the single-key shortcuts.

## Alternatives considered

- *Keep one wrapping row, just tidy spacing.* Still eleven controls with no hierarchy.
- *A permanent side panel of filters.* Takes width from the list and the details pane.
- *A single "Filters" dialog.* Hides the active state and needs several clicks per change.
