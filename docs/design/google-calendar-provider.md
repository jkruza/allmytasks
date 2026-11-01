# Google Calendar provider (read-only)

Tracking issue: [#28](https://github.com/jkruza/allmytasks/issues/28).

Status: implemented read-only, together with Google Tasks, in one PR; see [Implementation notes](#implementation-notes) for
where the code differs from this design and what is still open. The provider has been tried on a real account. Project: `AllMyTasks.Providers.Google`. It signs in
to a Google account with OAuth and syncs events from the user's Google calendars into the
common model, using the Google Calendar API v3 and its incremental sync tokens.

Read [providers.md](providers.md) for the provider contract and
[ical-caldav-provider.md](ical-caldav-provider.md) for the sibling calendar provider.
This doc covers only what is specific to Google.

## Scope

In v1:

- Sign in with a personal Google account or a Google Workspace (work/school) account, in the
  system browser, with no password handled by the app.
- List every calendar the account can see (own, subscribed, shared, holidays, birthdays).
- Sync events of each visible calendar incrementally.
- Recurring events, moved and cancelled occurrences, all-day events, time zones.
- Read-only: `CollectionInfo.IsReadOnly = true` for all calendars, whatever the Google access role.

Not in v1, documented so absence is a decision:

- Writing (create, edit, delete, RSVP). The scope choice below keeps the door open.
- Reminders, attachments. The meeting link, organiser and attendees are display-only data ([meeting-url-and-attendees.md](meeting-url-and-attendees.md)).
- Push notifications (`events.watch`): they need a public HTTPS endpoint and the app has no backend. Polling only.
- Google Tasks. It is a separate design, but shares the sign-in; see [Sharing with Google Tasks](#sharing-with-google-tasks).
- A time-range limited sync. Sync tokens cannot be combined with `timeMin`/`timeMax`; see [Open questions](#open-questions).

### Why the REST API and not CalDAV or a secret `.ics` address

| Option | Verdict |
| --- | --- |
| Secret `.ics` address (works today via the `ical` provider) | Stays as the no-sign-in fallback. No incremental sync, the address is a long-lived credential, and Workspace admins often disable it. |
| Google CalDAV | Needs OAuth anyway, offers less than the REST API (no per-event `eventType`, weaker sync), and Google treats it as legacy. |
| **Calendar API v3** | Chosen. Sync tokens, calendar list with colours and access roles, structured recurrence and exceptions. |

## Sign-in

OAuth 2.0 for installed apps, authorization code with PKCE, loopback redirect. No embedded web view.

1. App generates a PKCE verifier/challenge (S256) and a `state` value, starts an HTTP listener on
   `127.0.0.1` on a random free port, and opens the system browser at
   `https://accounts.google.com/o/oauth2/v2/auth` with `redirect_uri=http://127.0.0.1:<port>`.
2. The listener accepts exactly one request, checks `state`, answers with a small static
   "You can close this tab" page, and stops. It gives up after 5 minutes or when the user cancels the dialog.
3. The app exchanges the code at `https://oauth2.googleapis.com/token`, sending the verifier.
4. The refresh token goes to the secret store; the access token stays in memory only.

Parameters: `access_type=offline`; `prompt=consent` only when no refresh token is stored (Google
returns a refresh token only on the first consent otherwise); `include_granted_scopes=true` so a
later write scope adds to the grant instead of replacing it.

### Scopes

| Scope | Why |
| --- | --- |
| `openid email profile` | Name the source after the account and spot the same account being added twice (via `sub`). Not sensitive. Same scopes as the [Google Tasks design](google-tasks-provider.md#scopes). |
| `https://www.googleapis.com/auth/calendar.calendarlist.readonly` | `calendarList.list` |
| `https://www.googleapis.com/auth/calendar.events.readonly` | `events.list` |

Two narrow read scopes instead of `calendar.readonly`, which also reveals settings and ACLs we do not use.
Google's consent screen lets the user untick individual scopes; after the code exchange the provider
compares the returned `scope` with what it needs and, if one is missing, fails the sign-in with a message that
names what to tick, rather than failing later with a 403.

### Tokens and the secret store

Per source, in `ISecretStore` under `(sourceId, field)`:

| Field | Content |
| --- | --- |
| `refresh_token` | The long-lived credential. Secret. |
| `granted_scopes` | Space-separated scopes the user actually granted; tells the source which capabilities (calendar, tasks) are on. |

The field names are shared with Google Tasks, because both services use one `google` source per account (see
[Sharing with Google Tasks](#sharing-with-google-tasks)). The access token and its expiry live in memory in a per-source
`GoogleTokenProvider`, which refreshes 60 s before expiry, serialises concurrent refreshes, and is never written anywhere.
Non-secret config (`SourceConfig`): `account_email`, `account_sub`.

### Failure behaviour

| Event | Result |
| --- | --- |
| `invalid_grant` on refresh (revoked, expired, password reset, 6 months unused, admin removed the app) | `SourceAuthException`; the source shows "Sign in again" and the engine stops retrying |
| 401 from the Calendar API | refresh once and retry once; then as above |
| `access_denied` / `admin_policy_enforced` at sign-in (Workspace admin blocks third-party apps) | Sign-in fails with the Google error text scrubbed through `DiagnosticText`, plus a hint to ask the admin |
| Network error, 5xx, timeout | `SourceUnavailableException` |

### OAuth client and verification (a maintainer task, not code)

How the client reaches a build, what the form does without one, and revoke on removal are in [oauth-client-configuration.md](oauth-client-configuration.md); step-by-step Google Cloud setup is in [../oauth-setup.md](../oauth-setup.md).

- The calendar scopes are **sensitive**, not restricted: Google verification (privacy policy, home page,
  verified domain, short demo video) is needed, but no paid security assessment.
- Until the app is verified and set to "In production", Google shows an "unverified app" warning, caps users at 100
  test users, and **expires refresh tokens after 7 days** while the consent screen is in "Testing". Development
  and early alpha builds will hit this; the doc for testers must say so.
- For installed apps the client "secret" is not confidential (Google's own wording), but it is still a string
  GitHub secret scanning and push protection may flag. The project's production client id and secret are build-time
  properties supplied by the release workflow, not literals in the repo (as in the Google Tasks design). Needs the
  maintainer to confirm; see [Open questions](#open-questions).
- Forks and self-built copies cannot use the project's client id. They register their own client and supply it as
  configuration (`Google:ClientId` / `Google:ClientSecret`, or `ALLMYTASKS_GOOGLE_CLIENT_ID` /
  `ALLMYTASKS_GOOGLE_CLIENT_SECRET`), the same names the Google Tasks design uses. The README documents this until the
  app is verified.

## Calendars

`GetCollectionsAsync` calls `calendarList.list` (paged, one cheap call per refresh) and maps:

| Google | `CollectionInfo` |
| --- | --- |
| `id` | `Id` |
| `summaryOverride` ?? `summary` | `DisplayName` |
| `backgroundColor` | `Color` (`#RRGGBB`) |
| `accessRole` | `IsReadOnly = true` in v1 regardless; the role is kept in the raw payload for the write milestone |
| `selected` and not `hidden` | initial `IsVisible`; afterwards the local choice wins |
| `deleted: true` | omitted |

`selected` is Google's own "shown in the web UI" flag, so the first sync does not pull in the holiday and
other-people's calendars the user never turned on. A calendar that disappears from the list is removed with its
items; a 404 on a known calendar keeps its cache and adds a warning (same rule as CalDAV).

The `freeBusyReader` role returns busy blocks without titles. Those calendars are included; events arrive with
no `summary` and the view shows its placeholder.

## Sync

One `events.list` sequence per visible calendar.

**First sync (`previous == null`)**

```
GET /calendar/v3/calendars/{id}/events
    ?singleEvents=false          # masters and exceptions, not expanded; Core expands
    &maxResults=2500
    &fields=<trimmed field list, includes nextPageToken,nextSyncToken>
follow nextPageToken until absent; the last page carries nextSyncToken
```

Return `SyncResult.Snapshot(items, new SyncState(token), warnings, confirmedEmpty)`.
`confirmedEmpty` is true only when the response was a valid `calendar#events` document with an empty `items`
array; a body that is not that (HTML error page, truncated JSON) is a `SourceDataException`, never an empty snapshot.

**Later syncs**

```
GET .../events?syncToken=<token>&maxResults=2500&fields=<same list>
```

Return `SyncResult.Delta(upserts, deletedIds, new SyncState(nextSyncToken))`. With a sync token Google always
includes deleted entries, so nothing else is needed to learn about deletions.

**Rules that matter**

- A sync token is only valid with the same request parameters as the one that produced it, and cannot be combined
  with `timeMin`, `timeMax`, `q`, `updatedMin` and a few others. The provider uses one fixed parameter set for
  both phases and never adds filters.
- **410 Gone** means the token is invalid: throw `SyncStateExpiredException`; the engine re-syncs from scratch once.
  A 410 on a `pageToken` mid-sync restarts the sequence the same way. The new token replaces the old only after
  the last page arrives, so a failed sync leaves state untouched.
- State token = JSON `{"v":1,"syncToken":"..."}`. `ToString()` stays opaque as for every `SyncState`.
- Default refresh interval is the app-wide 15 minutes. An unchanged calendar costs one tiny request.
- Quota: 403 with reason `rateLimitExceeded`, `userRateLimitExceeded` or `quotaExceeded`, and 429, are
  `SourceUnavailableException`. Honour `Retry-After` if present, otherwise the base class's exponential backoff.
  403 `accessNotConfigured` (Calendar API not enabled for the OAuth project) is a plain `SourceException` with
  diagnostics, since retrying cannot help. 404 on a calendar, see above.

## Mapping to the model

| Google event field | Model |
| --- | --- |
| `id` (for an exception: `recurringEventId`) | `ItemKey.ItemId` |
| `originalStartTime` of an exception | `ItemKey.RecurrenceId` (timed: that instant; all-day: midnight UTC of the date) |
| `summary` | `Title` (`""` when absent) |
| `description`, `location` | `Description`, `Location` |
| `htmlLink` | `Url` |
| `start.date` / `end.date` | `AllDayEvent` (Google's `end.date` is already exclusive) |
| `start.dateTime` / `end.dateTime` | `TimedEvent`; `TimeZoneId` = `start.timeZone` when present, else null (the offset in the value is authoritative) |
| `recurrence[]` | `Recurrence` (below) |
| `status` | `EventStatus`: `confirmed`, `tentative`, `cancelled` map one to one |
| `transparency` | `opaque` or absent → `Busy`; `transparent` → `Free` |
| `updated` | `LastModified` |
| `conferenceData.entryPoints[]` with `entryPointType = video` (`uri`), else `hangoutLink`; `https` only, else dropped | `MeetingUrl` |
| `organizer` (`displayName`, `email`, `self`) | `Organizer` |
| `attendees[]` (`displayName`, `email`, `optional`, `resource`, `self`, `responseStatus`) | `Attendees` (first 200, real count in `AttendeeCount`) |
| `etag`, `iCalUId`, `eventType`, `colorId`, everything else | raw payload only |
| (no equivalent) | `Categories` empty |

Recurrence: `recurrence[]` is a list of iCalendar lines. `RRULE:FREQ=...` becomes `Recurrence.Rule` (prefix stripped),
`EXDATE...` lines become `ExcludedDates`, `RDATE...` lines `ExtraDates`. Date-only values (all-day series) become midnight
UTC of the date, as elsewhere in the model. Zone parameters (`EXDATE;TZID=Europe/Warsaw:...`) are resolved with the
same IANA lookup as the iCal provider. Google writes at most one `RRULE`; a second one is ignored with a warning.

### Exceptions and cancellations

Google returns a series as a master event plus one extra event per modified or cancelled occurrence
(`recurringEventId` and `originalStartTime` set). They map straight onto the model's override rows:

- **Moved or edited occurrence** → upsert with `ItemKey(ItemId = recurringEventId, RecurrenceId = originalStartTime)` and its own time.
- **Cancelled occurrence** → the same override key with `Status = Cancelled`. The expander in Core
  (`RecurrenceExpanderBase`) already treats that as "skip this occurrence", so Google needs no `EXDATE` rewrite.
  These come back from `singleEvents=false` listings even when `showDeleted` is false, which is why the provider
  does not set `showDeleted`.
- **Deleted master** (`status: cancelled`, usually without `start`/`end`) → an id in `DeletedItemIds`. Stores delete by
  `ItemId`, so its overrides go with it. A cancelled event that is not part of a series is treated the same way.

An entry with no usable `start` that is not cancelled is a `SourceDataException` for that item only: skipped, one
`SyncWarning`, sync continues.

### Event types and quirks

- `eventType` `workingLocation` entries are dropped (they are all-day clutter, one per day). `default`, `birthday`,
  `fromGmail`, `focusTime`, `outOfOffice` are kept. Dropped entries are not warnings.
- **Declined invitations**: an event whose attendee entry with `self: true`
  has `responseStatus: declined` is mapped to `Status = Cancelled`, which the views already hide, matching Google's
  default of hiding declined events. The attendee list is now in the model, but the mapping stays as decided; the declined answer is also shown in the details pane.
- All-day events spanning the user's time zone change are unaffected, since `AllDayEvent` is date-based.
- Calendar-level `timeZone` is only needed for events that omit a zone on recurring masters; Google always sends
  `start.timeZone` on those, so the provider does not read it.
- Birthday and holiday calendars are ordinary calendars as far as sync is concerned. Holiday calendars are large:
  see [Open questions](#open-questions).
- Meeting links: Google Meet/Zoom URLs sit in `hangoutLink` / `conferenceData`. The model has no field; the same gap
  exists for Outlook's `onlineMeeting`. Proposal for a shared `MeetingUrl` on `CalendarEvent` below.

## Components

| Type | Role |
| --- | --- |
| `GoogleSourceFactory : ISourceFactory` | Kind `google`, shared with Google Tasks. Sign-in is a button, not a schema field, so it supplies its own add-account view model for the browser hand-off, as `providers.md` anticipates. |
| `GoogleSource : SourceBase, ICalendarSource` | `GetCollectionsAsync` returns calendars when the calendar scopes are granted (and task lists when Tasks is). `SyncEventsAsync`. Uses `RetryAsync`. When Tasks lands the class also implements `ITaskSource`. |
| `GoogleCalendarClient` | Typed `HttpClient` wrapper: `ListCalendarsAsync`, `ListEventsAsync(syncToken/pageToken)`, status-code → exception mapping. |
| `GoogleEventMapper` | JSON → `CalendarEvent`, all the mapping above. Pure, no I/O, heavily tested. |
| `GoogleAuthFlow` | PKCE, loopback listener, code exchange. Takes an `IBrowserLauncher` so tests and headless CI can drive it. |
| `GoogleTokenProvider` | In-memory access token, refresh, `invalid_grant` → `SourceAuthException`. |

### Library choice

Plain `HttpClient` + `System.Text.Json` (source-generated context), no `Google.Apis.*` packages. The two endpoints used are
simple; the SDK brings a large dependency tree, its own token store and browser-flow assumptions that fight
`ISecretStore`, and extra weight for single-file self-contained publishing. The cost is writing ~300 lines of auth
and paging code that the SDK would provide; the tests below cover it. Revisit if the write milestone makes the SDK
worth its weight.

## Sharing with Google Tasks

Agreed with the [Google Tasks design](google-tasks-provider.md#one-account-two-capabilities); the two docs describe one
project, `AllMyTasks.Providers.Google`:

- One source **kind `google`** per Google account, implementing `ICalendarSource`, `ITaskSource` or both depending on the
  granted scopes. The user signs in once and picks which services to enable.
- Scopes are requested per service and added later by incremental authorization (`include_granted_scopes`): calendar scopes
  here, `tasks.readonly` there. A calendar-only user never sees a Tasks permission. Whichever service is enabled first
  decides the first consent screen; `granted_scopes` records the result.
- `GoogleAuthFlow` and `GoogleTokenProvider` are written once, with the Tasks design's `IGoogleAccessTokenProvider` as the
  seam the Calendar and Tasks clients take, so neither knows about OAuth.
- Whichever of the two PRs is implemented first builds the shared auth; the other reuses it.

## Shared with other OAuth providers (Outlook, MS To Do)

Not designed here, listed so the other designs agree:

- The browser hand-off dialog ("Waiting for you to finish in the browser", Cancel) and `IBrowserLauncher` belong in the
  app, not in a provider. Microsoft will likely use MSAL's own loopback flow but should show the same dialog.
- Secret fields per source: `refresh_token` here. Providers document their field names; none stores an access token.
- The same Linux secret-store fallback applies; a refresh token is the most valuable thing we store, so the weak PIN
  fallback matters more than for CalDAV passwords. Surface that in the UI when the fallback is active.
- Model gap: `CalendarEvent` has no meeting-link field. Proposal: add `Uri? MeetingUrl` as a small, separate PR once the
  Outlook design agrees. Until then it stays in the raw payload.

## Security

- Tokens, authorization codes, `state`, the verifier and the auth URL are never logged or put in exception messages or
  `ToString()`. Error diagnostics pass through `DiagnosticText.Scrub`.
- Loopback listener: `127.0.0.1` only (never `0.0.0.0`), random port, single use, short lifetime, `state` checked,
  responds to nothing but the redirect path. PKCE means an intercepted code is useless without the verifier.
- Only `https://accounts.google.com`, `https://oauth2.googleapis.com` and `https://www.googleapis.com` are contacted;
  redirects off those hosts are refused and the bearer token is never sent elsewhere.
- Event content is untrusted input: `System.Text.Json` with size limits, no HTML rendering of `description` (Google
  descriptions may contain HTML; the details pane shows text).
- Removing the source deletes its secret fields in the same operation, and the UI offers a link to
  `myaccount.google.com/permissions` for revoking access on Google's side. Revoking the refresh token at
  `https://oauth2.googleapis.com/revoke` on removal is done best-effort.
- Fixtures contain made-up data only.

## Testing

- **Mapper tests** from recorded JSON fixtures: timed with and without `timeZone`, all-day, multi-day all-day, recurring master
  with `EXDATE`/`RDATE`, moved occurrence, cancelled occurrence, deleted master, `freeBusyReader` event without a title, declined by
  self, `workingLocation`, missing `start`, two `RRULE`s.
- **Sync tests** against a fake `HttpMessageHandler`: first sync across several pages, delta with updates and deletions, empty
  delta, `confirmedEmpty`, HTML body → `SourceDataException`, 410 on token and on page token → `SyncStateExpiredException`,
  403 reasons, 429 with `Retry-After`, 404 on a calendar.
- **Auth tests**: PKCE challenge against the RFC 7636 appendix B vector; `state` mismatch rejected; listener bound to loopback
  only; missing granted scope reported; `invalid_grant` → `SourceAuthException`; concurrent refresh happens once; no token, code
  or verifier appears in any log, exception message or `ToString()`.
- **Optional integration test** (`AMT_GOOGLE_REFRESH_TOKEN`, `AMT_GOOGLE_CLIENT_ID`, ...) against a throwaway account, off by
  default and never in CI.
- **Manual before release**: a personal account and a Workspace account, with a recurring series that has a moved and a
  cancelled occurrence, an all-day event, an event in another zone, a shared calendar, and the Holidays calendar.

## Implementation plan

1. `GoogleEventMapper` and fixtures (no I/O).
2. `GoogleCalendarClient` and `GoogleSource` with a fake handler; plugs into the sync engine with a refresh token supplied by hand.
3. `GoogleAuthFlow`, `GoogleTokenProvider`, the sign-in view model and dialog.
4. Docs: update [roadmap.md](roadmap.md), the decision log in [README.md](README.md), and add an "Implementation notes" section here.

Each step is its own PR; the provider is usable by step 3.

## Implementation notes

Implemented in `src/AllMyTasks.Providers.Google` (kind `google`) with offline tests in `tests/AllMyTasks.Providers.Google.Tests`,
shared with [Google Tasks](google-tasks-provider.md#implementation-notes). **Nothing has been run against a live Google account
or the real Google Cloud project**: the code and tests follow the documented API shapes and recorded-shape fixtures only.

Differences from the sections above, all deliberate:

- **One source, two switches.** The form has *Google Calendar* and *Google Tasks* switches; both off means both on. The sign-in
  asks only for the scopes of the enabled services. Enabling a service later means editing the source and signing in again
  (a new consent for all scopes) instead of incremental authorization; revisit with a proper sign-in view.
- **Sign-in is the first sync, as for Microsoft.** There is no add-account view yet: after the source is saved, the first sync
  opens the browser once per app run; a failure says "edit this source and save it". Same limitation and same follow-up as
  [providers.md](providers.md#configuration-and-credentials).
- **Scopes.** `openid` (not `email profile`) plus the narrow read scopes. The ID token is read only for its `sub`, which is kept
  in the secret store (`account-sub`) so that signing in as a different account for the same source is refused. If the user
  unticks a permission on the consent screen the sign-in fails and names the missing scope.
- **Client id and secret.** Config fields `clientId` (text) and `clientSecret` (password, so it lives in the secret store), or
  build properties `AMT_GOOGLE_CLIENT_ID` and `AMT_GOOGLE_CLIENT_SECRET` that embed a default client as assembly metadata. This
  replaces the `Google:ClientId` / environment-variable names of the sketch; they follow `AMT_MICROSOFT_CLIENT_ID`. A client id typed
  into the source is always paired with the secret typed into the source, never with the built-in secret.
- **Secret fields** are `refresh-token` and `account-sub`, like the Microsoft provider's `refresh-token`. `granted_scopes` is not
  stored: the scopes are derived from the two switches and checked once, at sign-in.
- **No `GoogleAuthFlow`/`GoogleTokenProvider` split.** `GoogleAuth` does PKCE, the loopback listener (`LoopbackReceiver`,
  `127.0.0.1`, single use, 5 minutes, `state` checked), code exchange and refresh with one in-flight refresh per source.
  `GoogleClient` retries once with a fresh access token after a 401, honours `Retry-After` up to 30 s itself and passes longer
  waits to the engine, and refuses any address outside `www.googleapis.com` and `tasks.googleapis.com` before attaching the token.
- **Meeting links** are not mapped: there is still no `MeetingUrl` on `CalendarEvent` (open question 7 is accepted but is its own PR).
  `Url` is the event's `htmlLink`.
- **Descriptions** that contain HTML are converted to plain text.
- **Declined events:** the attendee entry with `self: true` and `responseStatus: declined` maps to `Cancelled`, as decided.
- **Not done:** revoking the token at Google when a source is removed, the "Sign in again" button, the link to
  `myaccount.google.com/permissions`, `include_granted_scopes`.

Not individually confirmed yet, although the provider as a whole has been tried on a real account: the exact `scope` string Google returns in the
token reply, `prompt=consent select_account` behaviour, that `fields` does not disturb sync tokens, the response shape of a
cancelled single occurrence in a delta, and whether `calendarList` `selected` hides holiday calendars as expected.

## Open questions

**Decided 2026-10-26 (maintainer):** all proposals below are accepted. Jarek owns the Google Cloud project, consent screen and verification, submitted before the first public release (#1); client id/secret reach builds as release-workflow build properties (#2); one `google` source for Calendar and Tasks (#4); declined invitations map to `Cancelled` (#5); no history limit in v1 (#6); `MeetingUrl` is added to `CalendarEvent` in a small PR agreed with the Outlook design (#7).

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Who owns the Google Cloud project and OAuth consent screen, and when do we submit for verification? | The maintainer, before the first public release; alpha testers are added as test users and warned about 7-day refresh tokens |
| 2 | How do the client id/secret get into builds without tripping secret scanning? | Release-workflow build properties, never committed literals |
| 3 | Per-source "bring your own client" fields in the UI? | No. Configuration and environment variables only, as in the Google Tasks design |
| 4 | One `google` source for calendar and tasks? | Yes, settled together with the Google Tasks design; maintainer to confirm when approving both |
| 5 | Declined invitations: hide, or show greyed out? | Hide (map to `Cancelled`) in v1; revisit with an attendee model |
| 6 | Very large calendars (Holidays, long-lived work calendars) make the first sync slow, and sync tokens forbid `timeMin`. Limit history at all? | No in v1. If needed: a first sync with `timeMin` and no sync token, polling with `updatedMin`, trading delete detection for size |
| 7 | Add `MeetingUrl` to `CalendarEvent`? | Yes, in a small PR agreed with the Outlook design |
