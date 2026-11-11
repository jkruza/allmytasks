# Command palette

Tracked in [#142](https://github.com/jkruza/allmytasks/issues/142). **Status: proposed, version 2.**

## Goal

One keyboard shortcut (Ctrl+K, Cmd+K on macOS) opens a box where typing finds a target: go to a date, switch view,
open a saved filter, jump to a source's page, or find an item. It builds on
[global search](search-and-saved-filters.md#global-search) and the view keys proposed in [views.md](views.md#navigation-and-shared-state).

## Design

- A centred overlay with one text box and a result list; Esc closes, Up/Down choose, Enter runs.
- Result groups, in order: **Commands** (views, "Go to today", "Refresh all", "Open Settings"), **Dates**
  (typed `next friday`, `2026-12-24`, `tomorrow`), **Saved filters**, **Sources**, **Items** (the global search results,
  limited to the top few).
- Matching reuses the word-prefix, accent-insensitive rule of global search ([matching](search-and-saved-filters.md#matching)).
- Date parsing is a small Core parser using the OS culture and week start; it accepts only unambiguous forms and
  returns nothing otherwise.
- Every command is registered with a name, optional keywords and an `ICommand`. Adding a command later (for example
  "Reschedule", a version 2 write action) needs no change in the palette.
- No telemetry and no history beyond "recently used" ordering kept in settings.

## Accessibility

Focus moves into the box on open and back to the previous control on close; results are exposed as a list with
the selected item announced. All commands remain reachable through menus and toolbar; the palette is an addition.

## Core changes

`CommandCatalog` (command definitions and ranking) and `DateExpressionParser` in Core, unit-tested. The overlay is
one view, one view model.

## Testing

Ranking with ties, accent folding, date phrases in two cultures and with Monday and Sunday week start, empty
query shows recent commands, long result lists are capped.

## Open questions

- Single shortcut or also `/` like web apps? Proposal: Ctrl/Cmd+K only; Ctrl+F stays plain search.
