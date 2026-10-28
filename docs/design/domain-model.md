# Domain model

The model every provider maps into and every view reads from. It lives in
`AllMyTasks.Core` and has no knowledge of any provider's wire format.

Design principle: model what the **user sees**, keep what the provider sent. Each item
carries a small set of normalised fields plus the provider's raw representation, so
nothing is lost and later features (editing, round-tripping) do not need a re-fetch.

## Identity

Every item is identified by a triple, never by a bare string:

```
SourceId   which configured account/feed        e.g. "ical-3f2a"
CollectionId  which calendar or task list in it e.g. "/dav/cal/work/"
ItemId     provider's id within the collection  e.g. iCalendar UID
```

`ItemKey(SourceId, CollectionId, ItemId, RecurrenceId?)` is a value type used as the
primary key in the store. Recurring-event overrides set `RecurrenceId` to the start of the
occurrence they replace (midnight UTC of the date for all-day events); see
[recurrence](#recurrence). Parts are validated non-blank, also on `with`.

## Sources and collections

```csharp
record SourceInfo(string Id, string Kind, string DisplayName);        // account / feed
record CollectionInfo(string SourceId, string Id, string DisplayName,
                      CollectionKind Kind,    // Calendar | TaskList
                      string? Color, bool IsVisible, bool IsReadOnly);
```

(Named `CollectionInfo` because the build's analyzers forbid a type name ending in
`Collection`.)

`Color` and `IsVisible` are user-adjustable and stored locally, even when the provider has
its own values. The user's choice wins; the provider's value is the default.

## CalendarEvent

```csharp
record CalendarEvent(
    ItemKey Key,
    string Title,
    EventTime Time,                    // see below
    string? Description,
    string? Location,
    Uri? Url,
    EventStatus Status,                // Confirmed | Tentative | Cancelled
    Availability Availability,         // Busy | Free
    Recurrence? Recurrence,            // null for single events
    IReadOnlyList<string> Categories,
    DateTimeOffset? LastModified,
    Uri? MeetingUrl,                   // https only, see meeting-url-and-attendees.md
    Attendee? Organizer,
    IReadOnlyList<Attendee> Attendees, // at most 200
    int AttendeeCount);                // the real number when the list was capped

record Attendee(string? Name, string? Email, AttendeeRole Role, ResponseStatus Response, bool IsSelf);
enum AttendeeRole { Required, Optional, Resource }
enum ResponseStatus { NeedsAction, Accepted, Tentative, Declined }
```

`MeetingUrl`, `Organizer` and `Attendees` are display-only data ([meeting-url-and-attendees.md](meeting-url-and-attendees.md)).
Deliberately **left out**: reminders/alarms and attachments (reminders have their own design,
[reminders.md](reminders.md)). They stay available in the raw payload and can be promoted to fields
when a view needs them.

## TaskItem

The existing record (`Id`, `SourceId`, `Title`, `Due`, `IsCompleted`) grows into:

```csharp
record TaskItem(
    ItemKey Key,
    string Title,
    string? Notes,
    TaskDue? Due,                      // date-only or date+time, see below
    DateTimeOffset? Start,             // "do not show before", optional
    TaskItemStatus Status,             // Open | InProgress | Completed | Cancelled
    DateTimeOffset? CompletedAt,
    Priority Priority,                 // None | Low | Medium | High (normalised)
    IReadOnlyList<string> Labels,
    ItemKey? ParentKey,                // subtasks
    Recurrence? Recurrence,
    Uri? Url,
    DateTimeOffset? LastModified);
```

Notes:

- `IsCompleted` becomes derived from `Status`; `IsOpen` is true for Open and InProgress.
  `IsOverdue(now, displayZone)` stays, now using `TaskDue` semantics: a date-only task is
  overdue from the day after its date, a timed task from its time.
- The status enum is `TaskItemStatus` because `TaskStatus` clashes with
  `System.Threading.Tasks.TaskStatus`.
- Providers with a different priority scale (Todoist 1-4, iCalendar 1-9, Microsoft
  low/normal/high) map to the four-level enum. The raw value is kept in the payload.
- Task lists with sections/projects/hierarchies map to `Collection` (list) plus
  `ParentKey` (subtask). Anything richer is out of scope.
- iCalendar `VTODO` maps here too, so the CalDAV provider can serve tasks later without a
  model change.

## Time

The most error-prone part of any calendar app, so it is explicit.

```csharp
abstract record EventTime;
record TimedEvent(DateTimeOffset Start, DateTimeOffset End, string? TimeZoneId, bool IsFloating) : EventTime;
record AllDayEvent(DateOnly Start, DateOnly EndExclusive) : EventTime;
```

- **Timed**: an exact instant (`DateTimeOffset`, normalised to UTC offset at the time of
  the event) **plus** the IANA time zone id it was authored in. The instant is what views
  sort and place on. The zone id is needed to expand recurrences correctly across DST and
  to display "10:00 Europe/Warsaw" when the user is elsewhere.
- **All-day**: a date range, **not** instants. A holiday on 25 December is on 25
  December in every time zone. `EndExclusive` follows iCalendar (`DTEND` is the day
  after), which makes spans and month-grid maths simple.
- **Floating times** (iCalendar times with no zone): flagged with `IsFloating`. Only the
  wall-clock reading of `Start`/`End` counts; their offset is ignored and the display zone
  is applied when the instant is needed (`StartInstant(zone)`), because they genuinely mean
  "local time wherever you are".
- `EventTime.StartInstant(zone)`, `EndInstant(zone)` and `Overlaps(from, to, zone)` are the
  only way views and expanders place an event. All-day days are resolved to the first
  instant of the date in the zone, so a DST day is 23 or 25 hours long.
- Windows time zone names, which some servers emit, are mapped to IANA ids at the provider
  boundary. Unknown zones fall back to UTC with a warning on the source status, not an
  exception.

```csharp
abstract record TaskDue;
record DueDate(DateOnly Date) : TaskDue;                 // "due Friday"
record DueDateTime(DateTimeOffset At, string? TimeZoneId) : TaskDue;
```

`TaskDue.SortKey(zone)` orders both kinds on one axis (a date-only value sorts at the start
of its day, so before timed tasks that day) and `IsPast(now, zone)` decides overdue-ness.

Tasks need the same split: "due Friday" and "due Friday 17:00" are different things and
must not be compared as if they were the same.

**Display zone**: views take an explicit `TimeZoneInfo` (default: system zone). Core
converts at the edge; stored data is never rewritten in the display zone.

## Recurrence

```csharp
record Recurrence(
    string Rule,                              // RFC 5545 RRULE text, normalised
    IReadOnlyList<DateTimeOffset> ExtraDates, // RDATE
    IReadOnlyList<DateTimeOffset> ExcludedDates); // EXDATE
```

Decisions:

1. **Store masters, expand on query.** The store holds one row per recurring series plus
   one per modified/cancelled occurrence. Expansion happens in Core for the requested
   date range only. Infinite series are never materialised.
2. **One expander, shared by all providers.** Providers hand over the RRULE as RFC 5545
   text. Even non-iCalendar providers (Todoist "every other Friday") translate to RRULE at
   the boundary, or declare the item non-expandable and supply occurrences themselves.
3. **Overrides** are separate `CalendarEvent` rows with the same `ItemId` and a
   `RecurrenceId` pointing at the original occurrence start. The expander substitutes them
   for the generated occurrence. A cancelled override removes it.
4. **Occurrences** are a view-time type, not stored:

```csharp
record EventOccurrence(CalendarEvent Event, EventTime Time, bool IsOverride);

interface IRecurrenceExpander
{
    IReadOnlyList<EventOccurrence> Expand(CalendarEvent master,
        IEnumerable<CalendarEvent> modifiedOccurrences,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, TimeZoneInfo displayZone);
}
```

   Range is half-open, results are ordered by start and never include cancelled
   occurrences. `FakeRecurrenceExpander` implements the contract from `RDATE`/`EXDATE`
   only (it ignores `Rule`) for tests and samples until the real one lands in M5.

The recurrence implementation should use an existing library (see
[the iCal provider doc](ical-caldav-provider.md#libraries)) rather than a handwritten
RRULE engine. It is wrapped behind `IRecurrenceExpander` so it can be swapped.

## What views consume

Views do not consume `CalendarEvent` and `TaskItem` directly. They consume
`AgendaEntry`, a small union produced by Core queries:

```csharp
abstract record AgendaEntry(ItemKey Key, string Title, string? Color);
record EventEntry(EventOccurrence Occurrence, ...) : AgendaEntry;
record TaskEntry(TaskItem Task, ...) : AgendaEntry;
```

This is what lets a day agenda interleave events and due tasks without views caring where
either came from. See [views.md](views.md).

## Invariants (to be enforced in constructors/tests)

- `Title` is never null; empty titles are replaced with a localisable placeholder at the
  view, not in the model.
- `AllDayEvent.EndExclusive > Start`; `TimedEvent.End >= Start` (zero-length allowed).
- `ItemKey` parts are non-empty.
- Two different items never share an `ItemKey`; an override shares `ItemId` with its
  master but differs by `RecurrenceId`.
