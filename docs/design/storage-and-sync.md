# Local storage and sync

Everything lives on the user's machine. There is no backend, no account with this
project, and no outbound traffic except to the providers the user configured.

## What is stored, and where

| Data | Where | Why there |
| --- | --- | --- |
| Source configuration (kind, display name, URL, username, settings) | SQLite | Not secret |
| Collections (name, colour override, visibility, sync state) | SQLite | |
| Events, tasks, raw payloads, sync tokens | SQLite | Cache; can always be rebuilt from providers |
| Passwords, app passwords, private feed URLs, OAuth tokens | OS secret store | Must not sit in files |
| UI preferences (view, week start, window size) | SQLite `settings` table or a small JSON file | Not secret |

The database file lives in the per-user application data directory
(`%APPDATA%\AllMyTasks`, `~/Library/Application Support/AllMyTasks`,
`$XDG_DATA_HOME/allmytasks` or `~/.local/share/allmytasks`), resolved through
`Environment.GetFolderPath`. A `--data-dir` option overrides it, which makes tests and
portable installs easy.

**The cache is disposable.** Deleting the database loses source configuration and local
preferences but never provider data. A "Reset cache" action drops items and sync state
but keeps sources.

## Database

**SQLite** via `Microsoft.Data.Sqlite`. Chosen over files-on-disk because the views need
range queries ("events overlapping this month") and transactional updates from sync, and
over a heavy ORM because the schema is small and the queries are specific.

Access sits behind Core interfaces so Core and view tests use an in-memory fake:

```csharp
public interface IItemStore
{
    Task ApplyAsync(string sourceId, string collectionId, SyncResult<CalendarEvent> result, CancellationToken ct);
    Task ApplyAsync(string sourceId, string collectionId, SyncResult<TaskItem> result, CancellationToken ct);

    Task<IReadOnlyList<CalendarEvent>> GetEventsOverlappingAsync(DateTimeOffset from, DateTimeOffset to, ...);
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(TaskFilter filter, ...);
}

public interface ISourceRepository { /* sources, collections, sync state, user overrides */ }
```

### Schema sketch

```
sources      (id PK, kind, display_name, config_json, status, last_synced_at, last_error)
collections  (source_id, id, display_name, kind, color, is_visible, is_read_only,
              sync_token, PK(source_id, id))
events       (source_id, collection_id, item_id, recurrence_id NULL,
              title, start_utc, end_utc, all_day, start_date, end_date, tz_id,
              status, rrule, last_modified, etag, raw, ...,
              PK(source_id, collection_id, item_id, recurrence_id))
tasks        (source_id, collection_id, item_id, title, status, due_date, due_utc,
              parent_item_id, priority, ..., raw, PK(...))
settings     (key PK, value)
schema_info  (version)
```

- Timed events store `start_utc`/`end_utc` (indexed) for range queries. All-day events
  store `start_date`/`end_date` as plain dates, never converted. Queries combine both.
- **Recurring series** need care: a master with an infinite RRULE has no useful end. Store
  `until_utc` (NULL = unbounded) and select candidates with `start_utc < :to AND
  (until_utc IS NULL OR until_utc >= :from)`; Core then expands and does the exact filter.
- `raw` holds the original iCalendar/JSON text so we can re-map after improving the
  mapper without re-fetching.
- Migrations: ordered, forward-only SQL scripts embedded in the assembly, applied in a
  transaction at startup, versioned in `schema_info`. Opening a database with a newer
  version than the app knows is a hard, explained error, not a silent downgrade.
- Remap after a mapper change = bump a `mapper_version` and re-derive from `raw` at
  startup in the background.

## Credentials

```csharp
public interface ISecretStore
{
    Task<string?> GetAsync(string sourceId, string field, CancellationToken ct);
    Task SetAsync(string sourceId, string field, string value, CancellationToken ct);
    Task DeleteAsync(string sourceId, CancellationToken ct);       // all fields of a source
}
```

Implementations, picked at startup by OS:

| OS | Backend |
| --- | --- |
| Windows | Credential Manager / DPAPI |
| macOS | Keychain |
| Linux | Secret Service over D-Bus (GNOME Keyring, KWallet), else PIN-protected file, see [Linux](#linux) |

### Linux

Order of preference, decided once at startup and shown in settings:

1. **Secret Service over D-Bus** (`org.freedesktop.secrets`), which GNOME Keyring and
   KWallet (5.97+ / Plasma 6) both implement. Same model as the other OSes: the OS
   unlocks it at login and the app stores nothing itself.
2. **App-level encryption keyed from a 6-digit PIN**, used only when no Secret Service is
   reachable. The user is told why and must opt in; nothing is ever written in plaintext.

If a Secret Service appears later (user installs a keyring), the app offers to migrate
the secrets and delete the PIN-protected file.

#### PIN-protected secret file

- All secrets go into one file, `secrets.enc`, in the data directory, encrypted with
  AES-256-GCM (authenticated; tampering is detected). Fresh random nonce per write.
- The key is derived from the PIN with **Argon2id** (memory-hard) using a random 16-byte
  salt stored in the file header, with parameters tuned to roughly 0.5-1 s on the user's
  machine and stored in the header so they can be raised later. A key-derivation library
  is a new dependency (the .NET BCL has PBKDF2 but not Argon2); PBKDF2-HMAC-SHA256 with a
  very high iteration count is the no-dependency alternative, weaker against GPUs.
- The PIN is asked once per app start and never stored. The derived key is held in memory
  only while the app runs, in a pinned buffer that is zeroed on exit where the runtime
  allows.
- Wrong PIN fails the GCM tag check. After 5 consecutive wrong entries the app adds
  an increasing delay. This only throttles the UI; it does not protect the file.
- "Forgot PIN" has no recovery. The secrets are discarded and the user re-enters their
  credentials; calendar and task data are unaffected because they are a cache.
- Changing the PIN re-encrypts the file under a new salt and key.

**Honest limits.** A 6-digit PIN has only 1,000,000 possibilities. Anyone who copies
`secrets.enc` can try them all offline; at 1 s per guess on one core that is about 12
days, and attackers with several cores or GPUs do it far faster. Argon2id slows this but
cannot fix the small keyspace. So this tier protects against casual access, backups
leaked to a cloud drive and other users on the machine, **not** against a determined
attacker who obtains the file. The settings screen says so, and also offers a longer
passphrase instead of a PIN, which uses the identical file format. Prefer the keyring
whenever one is available.

Rules (from `CLAUDE.md`, restated because they are easy to break):

- Secrets are never in SQLite, in logs, in exception text, in `ToString()`, or in crash
  reports.
- Removing a source deletes its secrets in the same operation.
- Secrets are read when a sync starts and not held in long-lived fields longer than
  needed.

## Sync engine

```csharp
public sealed class SyncEngine
{
    Task SyncAllAsync(CancellationToken ct);
    Task SyncSourceAsync(string sourceId, CancellationToken ct);
    event EventHandler<SourceSyncedEventArgs> SourceSynced;     // UI re-queries
    IObservable<SourceStatus> Status { get; }                   // or an event; UI shows per-source state
}
```

Per source, per collection:

1. Load previous `SyncState` from the repository.
2. Call the provider's `SyncEventsAsync`/`SyncTasksAsync`.
3. On success, `IItemStore.ApplyAsync` in one transaction, along with the new state.
4. Raise `SourceSynced`.

### Scheduling

- Sync on **startup** (after the UI is shown; cached data is already visible), on
  **manual refresh**, on **window re-focus** if the last sync is older than the source's
  interval, and on a **timer** while the app runs.
- Default interval **15 minutes**, per-source override. Minimum 1 minute.
- Sources sync **in parallel**, with a small cap (e.g. 4). Collections within a source sync
  sequentially unless the provider says otherwise.
- At most one sync per source at a time; a refresh during a sync joins it.
- Network down: sources move to `Offline` quietly, no error spam; a successful sync
  returns them to `Ok`.

### Failure handling

Per [providers.md](providers.md#errors). Summary:

| Failure | Result |
| --- | --- |
| Network/5xx/timeout | Cache kept, exponential backoff (1, 2, 4... up to 30 min), status `Offline` or `Error` |
| Auth failure | Cache kept, status `NeedsAttention`, no retries until the user edits the source |
| Expired sync state | Immediate full resync once |
| Bad item | Skipped, counted, shown as a warning |

Last successful sync time is always shown, so stale data is never mistaken for fresh.

### Consistency

- Apply is atomic per collection. A partially failed sync never leaves half an update.
- A full snapshot replaces the collection's contents in that transaction. An *empty*
  snapshot for a collection that previously had many items is suspicious (server error
  page parsed as "no events"); the engine refuses to apply an empty full snapshot that
  would delete more than a threshold of items unless the provider positively confirmed it,
  and reports a warning instead.
- Deleting a source removes its collections, items, state and secrets together.

## Privacy

- Calendar and task content is user data. It is not logged above debug level and debug
  logs are off by default.
- No telemetry, analytics or crash upload. If crash reporting is added later, it is opt-in
  and strips content.
- At rest, the database is **not encrypted in v1**; it relies on OS account protection and
  disk encryption. Stated plainly in user docs. Encryption (SQLCipher) is a possible later
  option and would interact with the Linux PIN-protected secret file.
- Export and "delete all my data" are straightforward because it is one directory.

## Implementation notes (M3)

- **Projects.** `AllMyTasks.Storage.Sqlite` (database, migrations, stores) and `AllMyTasks.Secrets` (secret
  store backends) are separate libraries with their own test projects, rather than living in the app
  project, so they can be unit tested. Core holds the interfaces (`IItemStore`, `ISourceRepository`,
  `ISettingsStore`, `ISecretStore`).
- **Schema.** As sketched above, with two differences: each event/task row also keeps the full item as JSON
  (`json` column) as the source of truth, with the query columns derived from it; and there is no `raw`
  provider payload yet because the model does not carry one. All-day events store midnight-UTC of their dates
  in `start_utc`/`end_utc`, and range queries add a one-day margin; `AgendaService`/the expander filter exactly.
  `until_utc` is derived from `UNTIL`/`RDATE` and is NULL (unbounded) for `COUNT`-only or open rules.
- **Migrations.** Embedded `Migrations/NNN_name.sql`, applied in a transaction each, version in `schema_info`.
  A database newer than the app is a `StorageVersionException`, shown by the app as an explained error.
- **Data directory.** `%LOCALAPPDATA%\AllMyTasks` on Windows (not roaming `%APPDATA%`: it is a cache),
  `~/Library/Application Support/AllMyTasks`, `$XDG_DATA_HOME/allmytasks`; `--data-dir` overrides it.
- **Secrets.** One backend entry per `(source, field)` plus a small index entry per source so a whole source can be
  deleted without enumerating the store. Windows: Credential Manager via P/Invoke. macOS: Keychain via the
  Security framework's generic-password functions. Linux: the Secret Service through the `secret-tool` client
  (secrets on stdin) when it can reach a service; otherwise, only with the user's consent, the PIN-protected file.
  The file is AES-256-GCM with the header as associated data, Argon2id (Konscious, MIT) with calibrated
  parameters stored in the header, written atomically with owner-only permissions. After 5 wrong PINs the prompt
  is delayed (1, 2, 4... up to 30 s). Choosing Argon2id resolves the open question about the library.
- **Not verified here.** The Windows and macOS backends compile on all platforms but were not run on those OSes
  during development; the Linux tool backend is tested against a stand-in script, not a real keyring.
- **Preferences.** Shell and All tasks view state (mode, filters, sort, grouping) are saved as JSON in `settings`. App-wide preferences (week start) are one JSON object under `settings.app`, see [settings.md](settings.md#persistence).
- **First run.** Until providers can be added, an empty database is seeded with sample data through the sync path.

## Implementation notes (M4)

- `SyncEngine` (Core/Sync) syncs each visible collection: previous state from the repository, provider call, one
  expiry retry, empty-snapshot guard, `IItemStore.ApplyAsync`, then the new state is saved. The cache write and the state
  write are two steps; a crash between them only repeats idempotent changes on the next sync.
- One sync per source at a time (concurrent requests join it); up to 4 sources in parallel. Hidden collections are
  not synced. Failures are recorded in `SourceStatus`, not thrown; status text never contains provider messages.
- Backoff 1, 2, 4 ... 30 minutes (`Backoff`). Auth failures stop automatic retries until the user edits the source
  (`ResetAttentionAsync`); a manual Refresh still tries.
- Empty-snapshot guard: an unconfirmed empty full snapshot is refused when the collection holds at least 5 items;
  providers that know a collection is really empty set `SyncResult.ConfirmedEmpty`.
- `SyncScheduler`: startup sync, 30 s tick, window re-activation; interval per source via the `refreshMinutes` config
  value (default 15, minimum 1).
- Status is persisted (migration 002), so a restart shows the last known state.
- `ISourceFactory` + `SourceConfigSchema` drive the Add/Edit source form; `SourceRegistry` splits secret fields
  into the secret store and the rest into config. `SampleSourceFactory` ("sample") is the only provider until M5; the app's
  first run adds one through the registry and the first sync fills the database.

## Open questions

- Encrypt the database at rest?
- Retention: keep how far back? Proposal: keep everything the provider returns; it is
  small.
- Argon2id library choice and licence (Konscious.Security.Cryptography, libsodium
  bindings, ...) versus PBKDF2 with no new dependency.
- Whether to allow PIN length above 6 digits or alphanumeric by default, given the limits
  above. Proposal: allow, with the 6-digit PIN as the quick option.
