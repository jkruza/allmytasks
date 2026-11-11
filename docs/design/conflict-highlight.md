# Highlight overlapping events across calendars

Tracked in [#136](https://github.com/jkruza/allmytasks/issues/136). **Status: implemented, version 1.**

## Goal

When events from different sources overlap in time (a work meeting in Outlook and a private appointment in Google),
show it. Seeing conflicts is the clearest benefit of merging calendars, and the layout code already computes
overlap clusters ([views.md](views.md#week)).

## Definition

Two timed events conflict when they overlap by at least one minute, both are visible, and both count as busy.
Not conflicts:

- all-day events and tasks (an all-day "Holiday" does not conflict with a meeting unless it is marked busy by the
  source and the user asks for it, see settings below),
- events that only touch (one ends when the other starts),
- declined or cancelled events (declined meetings are shown as Cancelled, see the Microsoft provider decisions),
- events marked free or tentative by the source, when the model carries it (`Transparency` if present; if the model
  has no such field, this rule is skipped and recorded as a limitation).
- events within the same source are included, because overlapping entries in one calendar are also conflicts.

## Design

- Core: `ConflictFinder.Find(IReadOnlyList<AgendaEntry>)` returns conflict groups (sets of entries that overlap
  transitively) using the same sweep the column packing uses. Pure function, unit-tested.
- Day agenda and week: conflicting entries get a small warning glyph and a thin accent edge; the spoken string
  gains "overlaps with Team sync".
- Upcoming: a "Conflict" chip on the entry.
- Details pane: a line "Overlaps with" listing the other entries, each opening its own details.
- Toolbar: optional count "2 conflicts this week" next to the date range, opens the first one.
- Settings, Appearance: "Highlight overlapping events" (on by default); "Count all-day busy events" (off).

## Edge cases

DST days use real elapsed time, as in the layout. Recurring events are checked on expanded occurrences. Events in
hidden collections are ignored, so hiding a calendar removes its conflicts.

## Testing

Chain, full cluster, touching, nested, all-day, cancelled, hidden collection, DST day, same-source overlap, many
events (linear time after sorting).

## Implementation notes

- Core: `Views/ConflictFinder.cs` (`ConflictFinder.Find`, `ConflictOptions`, `ConflictGroup`, `ConflictMap`). One sweep over the
  entries sorted by start. The result has the groups (transitive clusters, in start order) and, for each entry, the
  entries it **directly** overlaps; the "Overlaps with" line and the spoken text list only those, so a chain A-B-C tells A
  about B only. The model does carry `Availability` (busy or free), so the free rule is applied; tentative events and
  cancelled events are also left out, as the rules above say. An event shorter than a minute cannot overlap by a minute.
- Entries are keyed by item key plus start, so occurrences of one recurring series are told apart.
- Wiring: `ViewContext.Conflicts` (null when the setting is off) is handed to each view; Day, Week, Month and Upcoming run the
  finder on the entries they already loaded and expose `ContentViewModel.Conflicts`. Hidden collections never reach it, because
  `AgendaService` already drops them. The details pane runs the same finder on the day range of the clicked event, so it works
  from search and reminders too.
- Marks: rows (Day, Upcoming) get a "Conflict" badge with the other titles as tooltip and a thin warning edge; chips and bars
  (Week, Month) get a warning glyph before the title. The spoken text gains "overlaps with Team sync". The warning colour is
  the amber token of the design system.
- Toolbar: "2 conflicts this week" (or "on this day", "this month", "coming up") sits beside the navigation buttons and opens the
  first conflict: its day, with that event's details.
- Settings: the two check boxes are under **Calendar** in the Settings dialog (not Appearance, which only holds the accent):
  *Highlight overlapping events* (`highlightConflicts`, on) and *Count all-day events that are marked busy*
  (`countAllDayBusy`, off, only enabled while highlighting is on). Turning highlighting off removes every mark at once.
- Tests: `ConflictFinderTests` (pair, same source, touching, under a minute, exactly a minute, chain, full cluster, nested,
  separate groups, all-day and tasks, busy all-day with the option, cancelled/tentative/free, zero length, series occurrences,
  a DST day, 20,000 events in linear time, empty) and the two settings in `AppSettingsTests`.
