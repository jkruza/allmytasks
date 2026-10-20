# Roadmap and open questions

Each milestone is shippable on its own and ends with tests green, `dotnet format
--verify-no-changes` clean, and the docs here updated if the design moved. Sizes are
rough: S ≈ a few days, M ≈ 1-2 weeks, L ≈ several weeks of part-time work.

## Where we are

M1, M2 and M3 are implemented: the domain model, provider interfaces, in-memory sources, a fake
recurrence expander, the Core view logic (`AgendaService`, day/week/month/upcoming layouts,
`TaskListService`), `InMemoryStore`, and the Avalonia shell with four calendar views and All
tasks over sample data, backed by the SQLite store with settings and secret stores. Everything from
M4 on is still to do.

## Milestones

### M1: Domain model and provider abstraction (M)

- `ItemKey`, `Collection`, `CalendarEvent`, `EventTime`, `TaskItem` v2, `TaskDue`,
  `Recurrence`.
- `ISource`, `ICalendarSource`, `ITaskSource` (migrated), `SyncResult`, `SyncState`,
  `SourceException` hierarchy, optional `SourceBase`.
- `InMemoryCalendarSource` and updated `InMemoryTaskSource` for tests and samples.
- `IRecurrenceExpander` interface with a simple fake; no real RRULE yet.
- Tests for invariants, time-type behaviour and migration of the aggregator to the new
  types.

*Done when*: sample events and tasks flow through the new types and the existing UI still
shows tasks.

### M2: Views over in-memory data (L)

- `AgendaService` over a fake store. Core layout builders: `DayAgenda`, `WeekLayout`,
  `MonthGrid`, `UpcomingList`, with the unit tests listed in [views.md](views.md#testing).
- `CalendarShellViewModel` and four views in Avalonia, using sample data.
- Navigation, view switching, filter toggles.
- **All tasks** view: `TaskFilter`, sort, grouping, `ITaskListService`, with tests, and
  the filter bar UI.

Doing views before real providers means they are exercised with deliberately nasty sample
data (DST days, overlaps, multi-day) instead of whatever one calendar happens to contain.

*Done when*: all four calendar views and All tasks work on sample data on Windows, macOS and Linux.

### M3: Local storage (M)

- `Storage.Sqlite` with migrations, `IItemStore`, `ISourceRepository`.
- `ISecretStore` for each OS. On Linux: Secret Service first, PIN-protected encrypted
  file otherwise (see [storage-and-sync.md](storage-and-sync.md#linux)).
- App reads views from the store; sample data is seeded through the sync path rather than
  bypassing it.

*Done when*: restarting the app shows the same data offline.

### M4: Sync engine (M)

- `SyncEngine`, scheduling, backoff, per-source status, empty-snapshot guard.
- Source status UI: last synced, errors, refresh.
- "Add source" UI driven by `ISourceFactory` schema.

### M5: iCalendar feed provider (M)

- `ical` kind: fetch, conditional GET, parse, map, recurrence expansion with Ical.Net.
- Fixture-based mapping tests, secret-handling tests.
- Real events visible in all four views.

*Done when*: a user can paste a public `.ics` URL and see events with correct times and
recurrences.

### M6: CalDAV (read-only) (L)

- Discovery, calendar listing, the three sync strategies, Basic auth, TLS rules.
- Collection picker (visibility, colour override).
- Verified manually against at least Radicale, Nextcloud and one hosted service (e.g.
  Fastmail or iCloud) using throwaway accounts.

*Done when*: a user can add a CalDAV account and see its calendars, kept up to date
incrementally.

### Later (unordered, not committed)

- `VTODO` tasks over CalDAV.
- Todoist provider (first task-only provider; proves the model is not calendar-shaped).
- Google and Microsoft providers (OAuth).
- Write support: complete tasks first (smallest, most valuable), then edit/create.
- Reminders and notifications; tray / compact upcoming widget.
- Saved filters for All tasks; global search.
- Database encryption.

## Risks

| Risk | Mitigation |
| --- | --- |
| Time zone and recurrence bugs (the usual calendar graveyard) | Distinct all-day/timed types, one shared expander, heavy fixture and DST tests, use a mature library |
| Model too calendar-shaped for task providers | Todoist before Google/Microsoft; keep task and event types separate |
| CalDAV server quirks | Prefer standards (6578, 4791); degrade to ctag/etag diff; per-source diagnostics; test against several servers |
| Linux has no secret service | PIN-derived app-level encryption, opt-in and never plaintext; limited by the 6-digit keyspace, so documented as weak and the keyring is preferred |
| Avalonia custom panels for week/month are fiddly | All layout maths in Core and tested; panels only place precomputed boxes |
| Interface designed from one provider fits only that provider | Revisit `providers.md` after the second real provider and expect changes |

## Resolved

| Question | Decision |
| --- | --- |
| Local-only items with no provider? | No. Everything comes from a provider. |
| Where do undated tasks live? | A separate All tasks view with filtering, in M2. |
| Linux secret storage? | Secret Service (GNOME Keyring / KWallet), else app-level encryption from a 6-digit PIN. |

## Open questions

Collected from the other docs, with a proposed answer where there is one. Resolve in
discussion or an issue, then record the outcome in the decision log in
[README.md](README.md#decision-log).

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Is "iCal/WebDAV" meant to mean CalDAV (calendar sync), or also plain WebDAV file hosting of `.ics` files? | CalDAV plus plain `.ics` URLs; a bare WebDAV file is just a URL |
| 5 | Encrypt database at rest? | Not in v1; document it |
| 6 | Sync time-range limiting for CalDAV | Not in v1; sync everything |
| 7 | Default refresh interval | 15 minutes, per-source override |
| 8 | Separate projects per provider from the start, or one `Providers` project until there are two? | Own project for iCal (it brings a dependency); decide again at provider two |
| 9 | UI localisation and culture | Use OS culture for dates and week start from day one; translations later |
| 10 | App name (README says "working title") | Not blocking; namespaces would need renaming if it changes, so decide before M3 |
