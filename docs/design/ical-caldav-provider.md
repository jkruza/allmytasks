# iCalendar / CalDAV provider (read-only)

The first calendar provider. Project: `AllMyTasks.Providers.ICal`. It covers two kinds of
source that share one parser:

| Kind | What the user gives us | Transport |
| --- | --- | --- |
| `ical` | URL of a single `.ics` feed (public or private-link calendars, holiday feeds, Google/Outlook "secret address") | HTTP `GET` |
| `caldav` | Server URL (or account root) + username + password/app password | WebDAV/CalDAV over HTTP |

CalDAV is WebDAV with calendar extensions, so "iCal/WebDAV" is delivered as the pair
above. Read-only in v1: no `PUT`, `DELETE` or `POST`, and `Collection.IsReadOnly = true`.

## Scope

In v1:

- Subscribe to `.ics` URLs; `webcal://` is treated as `https://`.
- CalDAV discovery from a server URL, listing calendars, syncing `VEVENT`s.
- Recurrence (RRULE / RDATE / EXDATE, `RECURRENCE-ID` overrides), all-day events, time
  zones, `STATUS:CANCELLED`.
- HTTP Basic auth over TLS (covers app-password setups: Nextcloud, Fastmail, iCloud, Radicale,
  Synology).

Not in v1 (documented so absence is a decision, not a bug):

- `VTODO` tasks from CalDAV (the model supports it; add after events are solid).
- OAuth-based CalDAV (Google CalDAV requires OAuth). Google users use the secret `.ics`
  address until the Google provider exists.
- Digest/NTLM/client-certificate auth.
- Attendees, alarms, attachments.
- Writing.

## `ical` source: single feed

```
sync(previous):
  GET url  with If-None-Match: <etag>, If-Modified-Since: <lastModified>
  304 -> return empty result, same state
  200 -> parse whole VCALENDAR -> full snapshot, new state = {etag, lastModified, content hash}
```

- State token encodes the ETag, `Last-Modified` and a hash of the body. If the server
  sends no validators, the hash lets the engine skip a no-op write.
- A feed is one collection (named from `X-WR-CALNAME` or the URL host).
- Response size is capped (default 20 MB) and the request has a timeout; both configurable
  in code, not in UI.
- Redirects are followed, but **never from `https` to `http`**.

## `caldav` source: server

### Discovery (RFC 6764 / RFC 4791)

Given the URL the user entered:

1. `PROPFIND` the URL (Depth 0) for `current-user-principal`.
2. `PROPFIND` the principal for `calendar-home-set`.
3. `PROPFIND` the home (Depth 1) for child collections whose `resourcetype` includes
   `calendar`, reading `displayname`, `calendar-color` (Apple extension, optional),
   `supported-calendar-component-set`, `getctag`/`sync-token`.
4. If the entered URL fails and looks like a bare domain, try `/.well-known/caldav`.
   DNS SRV lookup is out of scope for v1.

Each calendar collection supporting `VEVENT` becomes a `Collection`. The user can hide
collections; hidden ones are not synced.

### Sync strategy, best available first

1. **`sync-collection` REPORT (RFC 6578)** when the collection advertises it: send the
   previous sync token, receive changed hrefs (with etags) and removed hrefs, fetch changed
   bodies with `calendar-multiget`. State = server sync token.
2. **CTag + ETag diff** otherwise: if `getctag` unchanged, nothing to do; if changed,
   `PROPFIND`/`calendar-query` for `href` + `getetag` of everything, diff against cached
   etags (kept in the state token's companion table), `calendar-multiget` the new/changed,
   treat missing as deleted.
3. **Full `calendar-query` REPORT** as last resort, returned as a full snapshot.

Time-range filtering (fetch only e.g. -1 month to +12 months) is an optimisation for huge
calendars, not a correctness feature. v1 syncs everything the server returns; recurring
series make a time range query unreliable on some servers anyway. Revisit if it proves
slow.

If a server rejects an expired sync token (`DAV:valid-sync-token` precondition failure),
throw `SyncStateExpiredException`.

### Auth and TLS

- Basic auth, sent only over `https`. `http` URLs are refused unless the user explicitly
  ticks "allow insecure connection" for a source (stored per source, shown as a warning on
  the source).
- Platform default certificate validation. No "ignore certificate errors" option in v1; self-signed server
  users can add their CA to the OS trust store.
- 401 → `SourceAuthException`. 403/404 on a previously known collection → collection
  marked missing, not auto-deleted.

## Mapping to the model

| iCalendar | Model |
| --- | --- |
| `UID` | `ItemKey.ItemId` |
| `SUMMARY` | `Title` |
| `DESCRIPTION`, `LOCATION`, `URL` | `Description`, `Location`, `Url` |
| `DTSTART`/`DTEND` with `VALUE=DATE` | `AllDayEvent` (end already exclusive) |
| `DTSTART`/`DTEND` with `TZID` | `TimedEvent` with IANA zone id |
| `DTSTART` UTC (`Z`) | `TimedEvent`, zone `UTC` |
| `DTSTART` floating | `TimedEvent` in the user's zone, flagged floating |
| `DURATION` instead of `DTEND` | computed end |
| no `DTEND`/`DURATION` | all-day: one day; timed: zero length |
| `RRULE`/`RDATE`/`EXDATE` | `Recurrence` |
| `RECURRENCE-ID` | override row linked to master |
| `STATUS` | `EventStatus` |
| `TRANSP` | `Availability` |
| `CATEGORIES` | `Categories` |
| `LAST-MODIFIED` | `LastModified` |
| everything else | kept in the raw payload only |

Mapping quirks to handle and test:

- Windows time zone ids (`W. Europe Standard Time`) and `X-WR-TIMEZONE`.
- A `VTIMEZONE` that conflicts with the IANA zone of the same name: trust IANA, warn.
- Several `VEVENT`s with one `UID` in one resource (master plus overrides).
- Missing `UID`: derive a stable id from the href (CalDAV) or a hash of the component
  (feed) and mark the item so it is not mistaken for server data.
- Line folding, escaped commas/semicolons, CRLF vs LF, BOM, and non-UTF-8 feeds.
- Very long titles/descriptions are kept intact in storage; truncation is a view concern.

## Libraries

| Need | Choice | Notes |
| --- | --- | --- |
| iCalendar parsing, RRULE expansion | [Ical.Net](https://github.com/ical-org/ical.net) | MIT, actively maintained, .NET 8+. Wrapped behind Core's `IRecurrenceExpander` and the provider's parser so it is replaceable |
| HTTP | `HttpClient` via `IHttpClientFactory` | One handler per source for credentials and redirect policy |
| WebDAV XML | `System.Xml.Linq` | CalDAV needs ~4 request shapes; a full WebDAV library is more dependency than value |

Adding Ical.Net is a package change: add the version to `Directory.Packages.props`, run
`dotnet restore`, commit the updated `packages.lock.json` files (see `CLAUDE.md`). Check
the licence and current version at that time.

## Testing

- **Parser tests** from fixture `.ics` files: all-day, timed, floating, TZID, UTC,
  recurring with EXDATE, override, cancelled, no-UID, malformed component among good ones.
- **CalDAV client tests** against recorded XML responses (multistatus for each step of
  discovery and each sync strategy) served by a fake `HttpMessageHandler`. No network in
  unit tests.
- **Auth tests** assert credentials never appear in logs, exception messages or
  `ToString()` output.
- **Optional integration test** against a local Radicale container, off by default and
  not run in CI until a stable job exists.
- All fixtures use made-up data. Never commit a real calendar export.

## Security

- Credentials: secret store only; the `Authorization` header is never logged. Log URLs
  with user-info and query string stripped (private `.ics` links often carry the secret in
  the query).
- A private feed URL is itself a credential. Store it as a secret field, not as plain
  config.
- Parse untrusted input defensively: size cap, no external entity resolution in XML
  (`DtdProcessing.Prohibit`), bounded recurrence expansion (cap occurrences per series
  per query).
- Never follow a redirect to a different scheme or send credentials to a different host
  than configured.

## Open questions

- Time-range limited sync: needed for very large calendars?
- Do we support `VTODO` from CalDAV in the same milestone as events, or after?
- Should `ical` feed refresh interval be user-set per source, or a global default?
  (Proposed: global default 15 minutes, overridable per source.)
