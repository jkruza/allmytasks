# Provider abstraction

A **provider** is an integration with one kind of external system (iCalendar/CalDAV,
Todoist, Google, Microsoft...). A **source** is one configured instance of a provider: a
specific account or feed URL.

Goal: adding a provider means writing one project that implements a few interfaces.
Nothing in views, storage or the sync engine changes.

## Shape

Small interfaces composed per capability, instead of one wide interface with flags.

```csharp
public interface ISource
{
    string Id { get; }                  // stable, unique per configured source
    string Kind { get; }                // provider type, e.g. "ical", "caldav", "todoist"
    string DisplayName { get; }

    Task<IReadOnlyList<Collection>> GetCollectionsAsync(CancellationToken ct = default);
}

public interface ICalendarSource : ISource
{
    Task<SyncResult<CalendarEvent>> SyncEventsAsync(
        Collection collection, SyncState? previous, CancellationToken ct = default);
}

public interface ITaskSource : ISource
{
    Task<SyncResult<TaskItem>> SyncTasksAsync(
        Collection collection, SyncState? previous, CancellationToken ct = default);
}
```

A source implements either or both: CalDAV can serve calendars and tasks; Todoist only
tasks; a `.ics` feed only events. The sync engine finds what a source can do with a type
check, not a capability bitfield, so the compiler stays honest.

The existing `ITaskSource` (`Id`, `DisplayName`, `GetTasksAsync`) is the seed of this. It
migrates by moving `Id`/`DisplayName` up into `ISource` and replacing `GetTasksAsync` with
the sync call. `InMemoryTaskSource` stays as the test and sample implementation.

### Optional base class

```csharp
public abstract class SourceBase(string id, string kind, string displayName) : ISource
```

Provides the identity properties, a protected `ILogger`, and shared helpers (retry with
backoff, mapping exceptions to `SourceException`). Providers may use it or implement the
interfaces directly. It never adds abstract members beyond the interfaces, so it is a
convenience, not a constraint.

## Sync contract

A sync call is "give me what changed in this collection since `previous`".

```csharp
public sealed record SyncState(string Token);        // opaque to everyone but the provider

public sealed record SyncResult<T>(
    IReadOnlyList<T> Upserts,                         // new or changed items
    IReadOnlyList<string> DeletedItemIds,
    SyncState NewState,
    bool IsFullSnapshot);                             // true: anything not in Upserts is gone
```

Rules:

- `previous == null` means first sync: return a full snapshot (`IsFullSnapshot = true`).
- Providers with real delta support (CalDAV `sync-collection`, Google `syncToken`,
  Todoist sync token) return only changes and a new token.
- Providers without it (plain `.ics` feed, CalDAV with ctag/etag only) fetch what is
  needed, **diff internally or return a full snapshot**. Either way the engine behaves the
  same: with `IsFullSnapshot`, it deletes cached items missing from `Upserts`.
- If the provider says the token is no longer valid it throws
  `SyncStateExpiredException`; the engine retries once with `previous = null`.
- Results are applied to the store in **one transaction per collection**. A sync that
  throws part-way leaves the cache untouched.
- Providers are unaware of the store. They return data; the engine persists it.

## Configuration and credentials

Each provider registers a descriptor so the app can offer "Add account" without
provider-specific UI code in Core:

```csharp
public interface ISourceFactory
{
    string Kind { get; }
    string DisplayName { get; }
    SourceConfigSchema ConfigSchema { get; }          // fields: label, type, required, secret?
    ISource Create(string sourceId, SourceConfig config, ISecretStore secrets);
}
```

- `SourceConfig` holds non-secret settings (server URL, username) and is stored in the
  database.
- Fields the schema marks `secret` are stored in the OS secret store under
  `(sourceId, field)` and handed to the provider through `ISecretStore` at call time.
  They are never part of `SourceConfig`, never logged, never in exception messages.
- OAuth providers (Google, Microsoft) add an `IAuthFlow` step later. Not designed here; it
  is the main reason `ISourceFactory.Create` takes the secret store rather than a
  password.

A schema-driven form keeps the first provider cheap. If a provider needs bespoke UI
(OAuth browser hand-off), it can supply a view model instead of a schema.

## Errors

Providers throw a small hierarchy, all deriving from `SourceException`:

| Exception | Meaning | Engine behaviour |
| --- | --- | --- |
| `SourceAuthException` | Credentials rejected or expired | Mark source "needs attention", stop retrying until user acts |
| `SourceUnavailableException` | Network, DNS, TLS, 5xx, timeout | Keep cache, retry with backoff |
| `SyncStateExpiredException` | Token invalid | Re-sync from scratch once |
| `SourceDataException` | Response unparseable or violates the model | Skip the item if possible, record a warning, keep going |
| `SourceException` (other) | Anything else | Treat as unavailable, log |

Per-item problems (one malformed `VEVENT` in a feed of 500) **must not fail the whole
sync**. Providers collect them as warnings on the result; the UI shows "3 items skipped"
on the source status.

## Write support (later)

Read-only v1 does not block writing. When added, writing is separate interfaces so a
read-only source simply does not implement them:

```csharp
public interface IWritableCalendarSource : ICalendarSource
{
    Task<CalendarEvent> CreateAsync(Collection c, CalendarEvent draft, CancellationToken ct);
    Task<CalendarEvent> UpdateAsync(CalendarEvent updated, CancellationToken ct);   // ETag / if-match
    Task DeleteAsync(ItemKey key, CancellationToken ct);
}
// ITaskSource gets IWritableTaskSource, plus CompleteAsync.
```

What v1 does now to keep this open: items keep the provider's raw payload and version tag
(ETag / `LastModified`); `Collection.IsReadOnly` exists; the store has room for a pending
changes queue. Conflict handling is a decision for that milestone.

## Registering providers

The app's composition root registers each `ISourceFactory`. Sources are loaded from the
database at startup and created through their factory. There is no reflection or plugin
loading in v1: providers are compiled in. Third-party plugins are a non-goal until the
interfaces have survived at least two real providers.

## Provider checklist

A new provider is done when:

- [ ] Implements `ISource` and the relevant calendar/task interface(s)
- [ ] Maps to the common model, including time zones and all-day items, with tests from
      fixtures
- [ ] Handles `previous == null`, a valid token, and an expired token
- [ ] Throws only `SourceException` subtypes across its public surface
- [ ] Keeps secrets out of config, logs and exception text (tested)
- [ ] Per-item errors become warnings, not sync failures
- [ ] Documents unsupported fields in its own design doc
