# Highlight overlapping events across calendars

Tracked in [#136](https://github.com/jkruza/allmytasks/issues/136). **Status: proposed, version 1.**

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
