# Workload glance

Tracked in [#140](https://github.com/jkruza/allmytasks/issues/140). **Status: implemented, version 1.**

## Goal

The app exists to give "a better overview of the work to be done". The views list items; none says how heavy the
coming days are. A small summary answers "how loaded am I this week?" at a glance.

## Design

A thin strip at the top of the Upcoming view (and optionally the week view) with the next seven days, one column
per day:

- **Meeting hours**: total duration of timed events, busy ones only, overlaps counted once. Shown as a bar and a
  label such as "5h 30m".
- **Tasks due**: count of open tasks due that day.
- **Overdue** at the left: number of open overdue tasks, which opens All tasks filtered to Overdue.
- A day above a threshold (default 6 hours of meetings or 8 due tasks, both in Settings) is tinted with the amber
  accent from [design-system.md](design-system.md). No red, no judgement text.
- Clicking a day opens that day in the day agenda.

Not included: estimates of task effort (providers rarely have them), anything that needs write support, free-time
suggestions.

## Rules

- Same inputs and filters as the other views: hidden collections excluded, duplicates collapsed
  ([duplicate-detection.md](duplicate-detection.md)), completed tasks excluded.
- All-day events do not count as meeting hours. Declined and cancelled events do not count.
- Days use the display zone, DST days use real elapsed time.

## Core changes

`WorkloadSummary` built by `IAgendaService` from the entries it already returns: `DayLoad(Date, Duration
MeetingTime, int TasksDue)` plus the overdue count. Union of intervals for overlap-free meeting time.
Pure function; the view model only binds.

## Testing

Overlap union, all-day exclusion, cancelled exclusion, DST day, threshold flag, hidden collection, empty week.

## Implementation notes

- `WorkloadSummary.Build(entries, today, zone, options, days)` in Core is a pure function over the entries the Upcoming
  view already loads, so hidden collections, collapsed duplicates and the shared filters apply without extra code.
  `DayLoad(Date, MeetingTime, TasksDue, IsHeavy)` plus the overdue count; meeting time is the union of the day's busy,
  timed, non-cancelled events (tentative ones count) clipped to the day's real length in the display zone.
- A day is heavy when it **reaches** the limit (at least 6 hours, or at least 8 tasks, by default). Heavy days show
  their meeting time in the amber text colour; there is no red and no message.
- Shown only in Upcoming and on by default, with a Settings switch (Workload section) and the two limits as choices.
  The week view does not show it yet.
- Clicking a day opens that day; the overdue number opens All tasks filtered to Overdue.
- The strip counts the whole day, so meetings that have already ended today are included.

## Open questions

- Show it by default or behind a setting? Proposal: on in Upcoming, off in the week view.
- Working-hours based percentage instead of raw hours? Proposal: raw hours first.
