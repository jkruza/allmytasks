# Source diagnostics

Tracked in [#138](https://github.com/jkruza/allmytasks/issues/138). **Status: implemented, version 1.**

## Goal

When a source misbehaves, the user and the maintainers need facts without guessing and without leaking secrets.
Jira and GitLab have not been tried on live sites yet ([roadmap](roadmap.md)), so reports from real use are
the fastest way to find problems.

The app already persists a status, last sync time and last error per source ([storage-and-sync.md](storage-and-sync.md))
and shows them on the Sources page. This doc extends that, it does not replace it.

## Design

- **Per-source details** (a "Details" expander on the Sources page): last successful sync, last attempt, duration of
  the last sync, counts of events and tasks by collection, number of consecutive failures, next retry time, sync
  strategy used (for example full or delta/sync token), and the **error category** (offline, authentication,
  rate limit, server error, unexpected), never the provider's raw message.
- **Copy diagnostics** button: puts a plain-text report on the clipboard: app version, OS, .NET version, and the
  per-source details above. Includes: source kind and a short generated id (not the display name, user name,
  e-mail, URL, tenant or project names). Excludes: tokens, secrets, item titles, notes, attendees and anything
  from provider responses. Source URLs are reduced to the host name only for self-hosted kinds (GitLab, CalDAV),
  and the user sees the report before copying.
- **Log level and location**: the Sources page links to the log folder and a switch for verbose logging, off by
  default; verbose logs still follow the no-secrets rule and are cleared on restart.

## Core changes

`SourceDiagnostics` record and `DiagnosticsReportBuilder` in Core build the report from `SourceStatus` plus new
counters recorded by the sync engine (duration, counts). A test asserts that the builder cannot emit a configured
secret or item title by feeding it a source whose settings and items contain marker strings.

## Testing

Report content for each status; marker-string leak test; host-only reduction for URLs; stable output order.

## Open questions

- Add an "Open issue with this report" button that opens a prefilled GitHub issue URL? Proposal: later; copying is
  enough and nothing leaves the machine without the user pasting it.

## Implementation notes

- Core, `Diagnostics/`: `SourceDiagnostics` (no field for a name, secret, address or item, so none can leak),
  `DiagnosticsReportBuilder` (the report and the per-source block; fixed order: sources by kind then short id,
  collections by kind then size), `SourceDiagnosticsService` (reads the engine, the repository and the item counts).
  `SyncAttempt` and `ErrorCategory` are in `Sync/`.
- **What is recorded where.** The engine keeps the last attempt of each source **in memory** (start time, duration,
  error category, how many collections were read in full and how many as a delta). Nothing new is written to the
  database, so there is no migration. After a restart the details show "none since the app started" until the next
  sync, and the error category is derived from the saved status (needs attention, offline, error). The last success,
  consecutive failures, next retry and skipped items come from the saved status as before; counts come from the cache.
- **Error categories**: offline or server unavailable, authentication, rate limit (the server sent a retry-after
  time), server error (the answer could not be used) and unexpected. They are a fixed list; the provider's own message
  is never part of the report.
- **Source id**: `src-` plus the first six hex digits of a SHA-256 of the internal id. Stable across restarts, and it
  does not reveal the id. The display name is never used.
- **Host**: only for `gitlab` and `caldav`, reduced to the host name (no scheme, port, path, query or user info). A
  blank GitLab address shows `gitlab.com`. Jira, iCalendar feeds, Google, Microsoft, Todoist and GitHub show no host,
  because their addresses carry a tenant, an account or a secret.
- **Collections** appear as "calendar" or "task list" with their item count and whether they are hidden; their names
  are left out.
- **UI**: the Sources window has a **Details** expander per source, **Copy diagnostics…** (opens the finished report in
  a window, with Copy and Close, so the user reads it before it reaches the clipboard), **Open log folder** and a
  **Verbose logging** check box. Verbose logging is never remembered: it is off at every start. While on, the engine's
  debug messages (they use the short id, never a name, address or item text) go to `logs/verbose.log`, which is deleted
  when the app starts.
- **Tests**: `SourceDiagnosticsTests` (marker strings placed in the display name, settings, address, token, collection
  and item titles, notes, provider message and error detail never reach the report or the verbose log; host
  reduction; stable order; exact report text; categories; strategy; counts).
