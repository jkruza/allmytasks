# Source diagnostics

Tracked in [#138](https://github.com/jkruza/allmytasks/issues/138). **Status: proposed, version 1.**

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
