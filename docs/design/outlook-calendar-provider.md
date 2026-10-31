# Outlook calendar provider (Microsoft Graph, read-only)

Tracking issue: [#32](https://github.com/jkruza/allmytasks/issues/32).

Calendar events from Outlook.com / Hotmail / Live (**personal Microsoft accounts**) through the Microsoft Graph
API. Project: `AllMyTasks.Providers.Microsoft`, shared with the Microsoft To Do provider
([microsoft-todo-provider.md](microsoft-todo-provider.md)).

Status: implemented for personal accounts. Read-only: no event is ever created, changed, answered or
deleted, and `CollectionInfo.IsReadOnly = true`. **Work and school (Entra ID) accounts are not supported
yet**; see [Work and school accounts](#work-and-school-accounts-not-in-v1) for why and what it would take.
The code has been tested against recorded-shape fixtures only; it has not yet been run against a live
account (see [Testing](#testing)).

## Scope

In v1:

- Sign in with a personal Microsoft account in the system browser.
- List the account's calendars (own, shared with the user, subscribed holiday calendars) and let the
  user hide the ones they do not want, as for CalDAV.
- Sync events incrementally with Graph delta queries: titles, times, all-day events, location, notes,
  online-meeting link, categories, status, free/busy, recurring series and their exceptions.
- Several Microsoft accounts at once. Each is its own source.

Not in v1 (decisions, not oversights):

- Work and school accounts (see below).
- A project-owned application registration shipped in the source. The client id is configuration; see
  [Application (client) id](#application-client-id).
- Writing, RSVP, creating meetings. See [providers.md](providers.md#write-support-later).
- Reminders, attachments. Meeting link, organiser and attendees are mapped as display-only data
  ([meeting-url-and-attendees.md](meeting-url-and-attendees.md)).
- Other people's calendars via delegate access (`Calendars.Read.Shared`), room/resource calendars,
  Microsoft 365 group calendars.
- Exchange on-premises mailboxes. Graph only serves mailboxes in Exchange Online.
- National clouds (US Government, China/21Vianet). Different authority and Graph host; the host is a
  single constant so it can be added later.
- Push notifications (Graph change notifications need a public endpoint; a desktop app polls).
- Windows broker (WAM) single sign-on. Possible later without changing the design.

## Why Graph, and not CalDAV or `.ics`

| Option | Personal accounts | Work accounts | Verdict |
| --- | --- | --- | --- |
| Published `.ics` link | Yes (user must publish the calendar) | Often disabled by the tenant | Works today via the `ical` provider; read-only snapshot with long, uncontrollable lag. Documented as the no-sign-in fallback |
| CalDAV | Not offered | Not offered | Not an option |
| Exchange EWS / IMAP-style access | Basic auth gone | EWS retired in Exchange Online | Not an option |
| **Microsoft Graph** | Yes | Yes | Chosen: one API, delta sync, same code for both account types |

## Microsoft identity and Graph client (shared with To Do)

This section is canonical for every Microsoft provider. The To Do design links here and covers only
what is specific to tasks.

### One source per account, both capabilities

Kind `microsoft`. One source is one signed-in account and may implement both `ICalendarSource` and
`ITaskSource` (the type check in [providers.md](providers.md#shape) finds out which). Which features
are on is a per-source setting (Calendar, To Do), so a user can add an account for calendar only. The
sign-in requests the scopes of the enabled features only; turning a feature on later triggers one
extra consent step.

```
AllMyTasks.Providers.Microsoft
  MicrosoftSourceFactory     ISourceFactory, kind "microsoft", bespoke sign-in view model
  MicrosoftSource            SourceBase + ICalendarSource + ITaskSource, delegates per feature
  MicrosoftAuth              browser sign-in (PKCE), refresh tokens in ISecretStore (below)
  Graph/GraphClient          HttpClient wrapper: auth header, paging, throttling, error mapping
  Calendar/...               this document
  Todo/...                   microsoft-todo-provider.md
```

### Application (client) id

Microsoft only issues tokens to a registered application, and there is **no sign-in route that skips
registration**. Researched and rejected:

| Option | Verdict |
| --- | --- |
| Reuse the client id of a Microsoft first-party app (Azure CLI, Graph PowerShell, Graph Explorer, Office) | Not done. These ids belong to Microsoft's own products; using them from a third-party app impersonates those products, is not covered by the Microsoft identity platform terms, and Microsoft can block or restrict them at any time. |
| Ask the user to paste an access token from Graph Explorer | Tokens last about an hour and cannot be refreshed. Unusable for a sync app. |
| Basic auth, IMAP/EWS, CalDAV for Outlook.com | Basic auth is gone; Outlook.com offers no CalDAV. Not an option. |
| A published `.ics` link for the calendar | Works today with the `ical` provider and needs no registration. Read-only snapshot with a long lag; no To Do. This is the registration-free route for calendars. |
| **Registered public client (this design)** | Registration is free, takes a few minutes, needs no secret and no publisher verification for personal accounts. |

So the id is **configuration, never hard-coded**:

1. If the source has an *Application (client) ID*, that is used.
2. Otherwise the id built into the app is used. A release build embeds it from the
   `AMT_MICROSOFT_CLIENT_ID` environment variable at build time (an assembly metadata entry, not a source
   file). The repository holds no id.
3. With neither, the first sync fails with `SourceAuthException` saying what to do.

A client id is public by design (it is sent in every sign-in URL), so embedding it in release binaries is
fine; keeping it out of the repository only means forks do not share the maintainer's registration.

#### Register an app (once per maintainer or user)

The current, fuller steps are in [../oauth-setup.md](../oauth-setup.md); how the id reaches a build is in [oauth-client-configuration.md](oauth-client-configuration.md).

1. Sign in to the [Microsoft Entra admin center](https://entra.microsoft.com) and open
   **App registrations → New registration**. A personal Microsoft account without a directory first needs a
   free tenant; the portal offers to create one.
2. **Supported account types:** *Personal Microsoft accounts only* (shown as *Any Entra ID Tenant + Personal
   Microsoft accounts* in some portal versions; both work, because the app always signs in against the
   `consumers` authority).
3. **Redirect URI:** platform *Mobile and desktop applications*, value `http://localhost` (any port matches).
4. **API permissions** (delegated, Microsoft Graph): `User.Read`, `Calendars.Read`, `Tasks.Read`,
   `offline_access`. Personal accounts consent themselves; no admin consent exists.
5. Copy the **Application (client) ID**. Paste it into the source's *Application (client) ID* field, or build
   the app with `AMT_MICROSOFT_CLIENT_ID=<id> dotnet publish ...`.

Least privilege: `Calendars.Read`, never `ReadWrite`, until write support is designed.

### Sign-in

OAuth 2.0 authorization code flow with PKCE, written directly against the token endpoint (about 300 lines,
no MSAL dependency), authority `https://login.microsoftonline.com/consumers`.

1. **Interactive, system browser.** Saving the source and running its first sync starts a loopback listener
   on `127.0.0.1` (random port), opens the authorization address in the default browser (https only) and
   waits up to 5 minutes for the redirect to `http://localhost:{port}/`. The `state` is checked; requests
   with a different state, or none, are answered 404 and ignored. There is no embedded web view, so password
   managers, passkeys and MFA work as in the browser.
2. **Silent** on every later sync: the stored refresh token is redeemed for a new access token (kept in memory
   only, reused until two minutes before expiry). Microsoft rotates refresh tokens; the new one is stored each
   time.
3. **Sign in again:** when the refresh token is rejected (`invalid_grant`, `interaction_required`, a changed
   permission set) it is deleted and the sync fails with `SourceAuthException`, so the source shows *needs
   attention*. Editing and saving the source creates a fresh source instance, whose first sync opens the browser
   again. A background sync opens the browser **at most once per source instance**, so a 15-minute timer never
   throws browser windows at the user.

Known limits of this shape, accepted for v1: there is no in-app "Sign in" button or progress dialog (the form is
schema-driven), no device-code fallback for machines without a browser, and the account name is not shown. A
proper sign-in view model is a follow-up.

Nothing about the account is stored except the refresh token (secret store). `DisplayName` is whatever the user
typed.

### Token storage

Tokens never touch the database or config files ([decision 6](README.md#decision-log)). Only the **refresh
token** is stored, as one short secret field `refresh-token` per source in `ISecretStore`; it fits every backend
(including the 2560-byte Windows credential limit), so no chunking is needed. Access tokens live in memory.
`ISecretStore.DeleteAsync(sourceId)` removes the token when the source is removed.

Removing a source does **not** revoke the grant at Microsoft; users do that at
`account.microsoft.com/privacy/app-access`.

### Graph client

A thin class over `HttpClient`, not the full Graph SDK (large dependency, trimming/AOT friction, and we
need five request shapes). Models are small DTOs for only the fields we read, parsed with
`System.Text.Json` source generation.

- **Host pinning.** Every request URL, including `@odata.nextLink` and `@odata.deltaLink` values returned
  by the server and stored in sync state, must have scheme `https` and host `graph.microsoft.com`,
  otherwise the request is refused with `SourceDataException` and **the bearer token is never sent**.
  Redirects are not followed automatically.
- **Headers on every call:** `Authorization: Bearer ...` (never logged),
  `Prefer: IdType="ImmutableId"` (item ids survive moves between folders),
  `Prefer: outlook.timezone="UTC"`, `Prefer: outlook.body-content-type="text"`,
  `Prefer: odata.maxpagesize=100`.
- **Throttling.** `429` and `503` with `Retry-After`: wait that long inside the client when it is 30 seconds or less (up to
  3 attempts). If still throttled, throw `SourceUnavailableException` that **exposes
  the server's wait** (`RetryAfter` property); the sync engine schedules the next attempt no earlier than that (capped at an hour). Without the header, exponential backoff via
  `SourceBase.RetryAsync`. Calendars are synced sequentially per source, never in parallel.
- **Error mapping:**

  | Graph / token endpoint | Exception | Notes |
  | --- | --- | --- |
  | `invalid_grant`, `interaction_required`, `consent_required`, 401/403 from Graph | `SourceAuthException` | "Sign in again"; includes password change, MFA reset, token revoked, conditional access |
  | `invalid_client`, `unauthorized_client`, `access_denied` at sign-in | `SourceAuthException` | Wrong client id, an app that does not accept personal accounts, or the user declined |
  | 404 `MailboxNotEnabledForRESTAPI`, `MailboxNotSupportedForRESTAPI` | `SourceException` | "This mailbox is not in Exchange Online" (on-premises) |
  | 404 on a known calendar | warning, collection marked missing | Not auto-deleted, same rule as CalDAV |
  | 410 `Gone`, `syncStateNotFound`, `resyncRequired` | `SyncStateExpiredException` | Engine re-syncs from scratch once |
  | 429, 503, 504, network, TLS, timeout | `SourceUnavailableException` | Keeps cache |
  | Other 4xx | `SourceException` | |

- **Logging.** Log method, path template (`/me/calendars/{id}/calendarView/delta`), status and
  duration. Never log query strings (they carry `$deltatoken`/`$skiptoken` and date ranges), headers,
  bodies or the account name.

## Work and school accounts (not in v1)

Dropped for now (maintainer decision, 2026-10-26). The sync code would be identical; what is missing is
everything around it, and none of it can be tested without a work tenant:

- The app registration must be multi-tenant and, to avoid "needs admin approval" in many tenants, publisher
  verified. That is a manual maintainer task.
- Failure modes needing their own handling and messages: admin consent required (`AADSTS65001`, `AADSTS90094`),
  conditional access and compliant-device policies, mailboxes that are on-premises or unlicensed, shared
  calendars exposing only free/busy.
- The `common`/`organizations` authority and a tenant-aware account label.

Meanwhile a work calendar can be added through its published `.ics` link with the `ical` provider, if the
tenant allows publishing.

## Collections

`GET /me/calendars` (all calendar groups; follows `@odata.nextLink`).

| Graph `calendar` | `CollectionInfo` |
| --- | --- |
| `id` | `Id` |
| `name` | `DisplayName` (suffix with owner when the calendar is shared and owned by someone else) |
| `hexColor` (`#RRGGBB`) | `Color`; if empty or `auto`, none, so the app picks one |
| `canEdit` | not used in v1; `IsReadOnly` is always `true` |
| `isDefaultCalendar` | not used; every calendar is visible on first run and the user hides the ones they do not want |

Calendars that disappear from the list are removed with their events; a calendar answering 404 keeps
its cache and adds a warning, as for CalDAV. Visibility and colour overrides are local, as for CalDAV.

## Sync strategy

### Why a window, and what Graph gives us

Graph v1.0 offers two ways to read events, and neither is a complete replacement for the model's
"store the series once, expand in Core" rule ([decision 4](README.md#decision-log)):

| Endpoint | Returns | Delta in v1.0 |
| --- | --- | --- |
| `/me/calendars/{id}/events` | Single events and **series masters** (with `patternedRecurrence`). Exceptions are not in the list | No (calendar-wide delta is beta only) |
| `/me/calendars/{id}/calendarView?startDateTime&endDateTime` | **Occurrences and exceptions** already expanded by the server, plus single events, for a date range | **Yes**, `/calendarView/delta`, per calendar and per range |

**Decision: v1 uses `calendarView/delta` and stores each occurrence as a plain, non-recurring event.**

Why:

- Only this path has supported delta sync, so a sync after the first is cheap and deletions are
  reported.
- Exchange has already resolved time zones, DST, `Monthly on the 2nd Tuesday`, "last weekday",
  moved/cancelled occurrences and exceptions. Re-deriving them from `patternedRecurrence` means
  translating Outlook's recurrence model (including `firstDayOfWeek`, `index`, the series' own time
  zone and non-Gregorian types such as Hijri) into RRULE; the mapping is lossy and is the classic place
  for calendar bugs.
- Exceptions are not available from the events list in v1.0, so a master-based design would need a
  second, per-series request (`/instances`) anyway.

Costs, accepted knowingly:

- This deviates from decision 4 for this provider: `CalendarEvent.Recurrence` is `null` and the series
  link is only in the raw payload (`seriesMasterId`), so the details pane cannot say "repeats weekly".
  Proposed follow-up, not part of this design: an optional `SeriesId` on `CalendarEvent`.
- Only events inside the window exist locally.
- The window must be rolled forward (below).

Upgrade path: when Graph ships calendar-wide `events/delta` in v1.0 and exposes exceptions (it is
beta today), a second mapping can store masters with `Recurrence` and drop the window. Mapping code for
single events is shared. Revisit then, behind the same state-token format (a version field is
included).

### The window

- First sync: `[start of month − 3 months, start of month + 13 months)` in UTC.
- Window bounds are part of the state; they never change while a delta chain is running, because the
  tokens encode them.
- **Roll:** when less than 9 months of future remains (about every 4 months), or on user request
  ("Refresh fully"), the next sync starts a new chain with a new window and returns a **full snapshot**
  (`IsFullSnapshot = true`), so anything outside the new window is deleted by the engine. Cost: one
  full read, a few thousand events at most for a busy calendar.
- If the user navigates beyond the window, views simply show nothing there. Window size is a constant in
  code, not a setting, in v1. See open questions.

### Algorithm per calendar

```
sync(collection, previous):
  state = parse(previous)                     # {v, deltaLink, windowStart, windowEnd}
  if state is null or window must roll:
      url = /me/calendars/{id}/calendarView/delta?startDateTime=..&endDateTime=..
      snapshot = true
  else:
      url = state.deltaLink; snapshot = false
  loop:
      GET url (host-pinned)
        410 / syncStateNotFound  -> throw SyncStateExpiredException
      for each item in value:
          if has "@removed"       -> deleted.Add(id)
          else                    -> upserts.Add(map(item))   # per-item failure => warning
      if @odata.nextLink: url = it; continue
      else: deltaLink = @odata.deltaLink; break
  return snapshot ? Snapshot(upserts, newState) : Delta(upserts, deleted, newState)
```

- State token: JSON `{ "v": 1, "deltaLink": "...", "start": "...", "end": "..." }`. The delta link is a
  URL on `graph.microsoft.com` holding opaque server state; it carries no credentials, but it is still
  not logged, and `SyncState.ToString()` already hides the token.
- Unchanged calendar: Graph answers with an empty delta page and a new `deltaLink`, which becomes the
  new state; the engine sees an empty delta.
- The same event can appear on several pages of one round: the last one wins (dedupe by id before
  returning).
- Delta tokens expire when Exchange's token cache evicts them (not a fixed lifetime). That surfaces as
  `syncStateNotFound`/410, handled as above. A source that has been offline for weeks simply re-syncs.
- An event moved out of the window, or occurrences of a series that was shortened, arrive as
  `@removed`; all `@removed` entries are treated as deletions regardless of `reason`.
- `$select` is not supported on `calendarView/delta`; we get full events and map what we need. Payload
  size is bounded by the page size (100) and a response cap (5 MB per page).
- A subscribed holiday calendar ("Polskie święta") was reported (#67) to show events as "(no title)": the delta reply
  appears to carry no `subject` for them. For any event that comes back without one, the sync reads that event on its
  own with `GET me/events/{id}?$select=subject` (at most 50 such reads per sync; the rest wait for the next sync, with a
  warning). A failed read keeps the event with an empty title. This is unverified against a live account.

### Refresh interval

Polling only; the global default of 15 minutes applies ([storage-and-sync.md](storage-and-sync.md)),
overridable per source. The first sync of a large calendar runs calendar by calendar so the first
calendar's events appear before the last finishes.

## Mapping to the model

| Graph `event` | `CalendarEvent` |
| --- | --- |
| `id` (immutable) | `Key.ItemId`; `Key.CollectionId` = calendar id; `RecurrenceId` stays `null` (see above) |
| `subject` | `Title` (empty → empty string; the view adds the placeholder). If the delta reply has no subject, it is read with `GET me/events/{id}?$select=subject`, see below |
| `start`/`end` + `isAllDay` | `TimedEvent` or `AllDayEvent`, see below |
| `originalStartTimeZone` | `TimedEvent.TimeZoneId` as IANA |
| `body.content` (requested as text) | `Description` |
| `location.displayName` (else `locations[].displayName` joined with `; `) | `Location` |
| `webLink` | `Url` |
| `onlineMeeting.joinUrl`, else `onlineMeetingUrl`; `https` only, else dropped | `MeetingUrl` |
| `organizer.emailAddress` (`isOrganizer` sets `IsSelf`) | `Organizer` |
| `attendees[]`: `emailAddress`, `type` (`required`, `optional`, `resource`), `status.response` (`accepted` and `organizer` → Accepted, `tentativelyAccepted` → Tentative, `declined` → Declined, else NeedsAction) | `Attendees` (first 200, real count in `AttendeeCount`) |
| `isCancelled` or `responseStatus.response = declined` | `Status = Cancelled` |
| `showAs = tentative` or `responseStatus.response = tentativelyAccepted` | `Status = Tentative` |
| otherwise | `Status = Confirmed` |
| `showAs = free` | `Availability.Free` |
| `busy`, `tentative`, `oof`, `workingElsewhere`, `unknown` | `Availability.Busy` (finer value only in the raw payload) |
| `categories` | `Categories` |
| `lastModifiedDateTime` | `LastModified` |
| `type`, `seriesMasterId`, `changeKey`, `sensitivity`, ... | dropped: the model has no raw-payload field yet |

Time handling, the part that goes wrong:

- With `Prefer: outlook.timezone="UTC"` every `dateTimeTimeZone` arrives as UTC with no offset in the
  string; it is parsed as UTC (the offset is attached explicitly, never `DateTime.Parse` local-time
  semantics).
- `originalStartTimeZone` is a **Windows** zone name (`W. Europe Standard Time`), an IANA name, `UTC`
  or occasionally a custom/unknown name. Windows names are converted with
  `TimeZoneInfo.TryConvertWindowsIdToIanaId` (as the `ical` provider does); unknown → `TimeZoneId = null`
  plus a warning. Instants are always right because they come from UTC; the zone id is only the
  authoring zone.
- **All-day events** (`isAllDay`): Exchange stores them as midnight-to-midnight in the event's own zone.
  The UTC instant is converted back to `originalStartTimeZone` and the **date** is taken; the end is
  already exclusive (midnight of the day after). If the converted start is not at midnight (a zone
  mismatch), round to the nearest date and add a warning. Needs verification with real accounts in
  several zones, including DST days, before the provider is declared done.
- Multi-day all-day events keep `EndExclusive`; zero-length timed events are legal (`End == Start`).
- Events with `end < start` (seen in corrupt data) get `End = Start` and a warning.
- `Prefer: outlook.body-content-type="text"` makes Exchange convert HTML bodies; no HTML parsing or
  rendering happens in our code, and the body is never treated as markup. Very long bodies are kept
  whole (truncation is a view concern).
- Text fields are untrusted: no HTML, no `javascript:`/`file:` links. `Url` is accepted only when it is
  an absolute `https` URI, otherwise `null` with a warning.

Per-item failures (a malformed event) become `SyncWarning`s with the item id; they never fail the sync
([providers.md](providers.md#errors)).

## Libraries

| Need | Choice | Notes |
| --- | --- | --- |
| OAuth | Own code over `HttpClient` | Authorization code + PKCE + refresh is small and fully testable offline; MSAL would add a second token cache beside `ISecretStore`. Revisit MSAL if device code, broker sign-in or work accounts are added |
| Graph HTTP | `HttpClient` via `IHttpClientFactory` + own thin client | No `Microsoft.Graph` SDK, see above |
| JSON | `System.Text.Json` source generator | Already in the framework |

No new packages were needed.

## Testing

Implemented in `tests/AllMyTasks.Providers.Microsoft.Tests`, all offline with a fake `HttpMessageHandler` and
made-up data:

- **Mapping:** timed, all-day (several zones, a DST change, not-at-midnight), Windows zone names, unknown zone,
  cancelled, declined, tentative, free, no subject, malformed item among good ones, unsafe links.
- **Sync:** first sync across `nextLink` pages, empty delta, update and `@removed`, window roll to a snapshot,
  `410` → `SyncStateExpiredException`, a saved or returned address on another host is refused and the token is
  not sent there, a missing calendar keeps the cache and warns, a page with neither link is an error.
- **Throttling:** `429` with a short `Retry-After` is waited out inside the client; a long one is thrown with
  `RetryAfter`, which the sync engine honours (`Backoff.Delay(failures, serverWait)`).
- **Auth:** the full sign-in against a loopback listener (PKCE, state check, forged redirect ignored, declined),
  refresh with token rotation, revoked token deleted, no client id, per-source client id wins, tokens never in
  messages or diagnostics, bearer token only to `graph.microsoft.com`.

**Not yet done, needed before calling this finished:** a manual run against a throwaway personal account
(consent screen, a recurring series with exceptions, an all-day event created in another zone, birthday and
holiday calendars for question 5, behaviour of `calendarView/delta` on them). No Graph response from a real
account has been seen by the code yet; the fixtures follow the documented shapes.

## Security and privacy

- Public client, no secret anywhere. The refresh token only in the OS secret store.
- Bearer token only to `graph.microsoft.com`; the refresh token only to `login.microsoftonline.com`. The loopback listener binds to `127.0.0.1` only, accepts one redirect with the right `state`, and closes.
- Least-privilege scopes, read-only. The consent screen is the user's check on that; the README states
  the permissions and why.
- Event content (titles, notes, locations) is personal data: not logged, not in exception messages,
  not sent anywhere else, not in test fixtures. `SourceException.Diagnostics` may carry the Graph error
  `code` and request id, never the event text.
- No telemetry. Microsoft sees normal API traffic from the user's own sign-in.
- Treat Graph responses as untrusted input: page and size caps, bounded loops (a delta round stops
  after 500 pages and reports `SourceException`), no following of links off-host.

## Milestone sketch

1. Done: `AllMyTasks.Providers.Microsoft` with browser sign-in, refresh token in `ISecretStore`, `GraphClient` with
   pinning and throttling, calendars list and `calendarView/delta` sync with the window, mapping and tests.
2. Next: manual verification on a live personal account (see Testing); fix whatever Graph does differently.
3. Later: an in-app sign-in view (progress, account name, device code), work and school accounts, shared calendars.

## Open questions

**Decided 2026-10-26 (maintainer):** v1 stores expanded occurrences without `Recurrence` (#2); window -3/+13 months (#3);
declined meetings are shown as `Cancelled` (#4); `RetryAfter` on `SourceUnavailableException` (#8, done). Shared calendars
and the Windows broker stay out of v1. **Also 2026-10-26:** work accounts dropped for now, and no registration is required by the code: the client id
is configuration (#1 below). Microsoft still requires *some* registration; see [Application (client) id](#application-client-id).

| # | Question | Status |
| --- | --- | --- |
| 1 | Who owns the Entra app registration, and is publisher verification worth doing? | Superseded: personal accounts need no verification and the client id is configuration (see [Application (client) id](#application-client-id)). Revisit with work accounts |
| 2 | Accept storing expanded occurrences with no `Recurrence`, or master + RRULE now? | Accepted for v1, add `SeriesId` later, revisit when calendar-wide delta reaches v1.0 |
| 3 | Window size | -3/+13 months, rolled when under 9 months remain; a setting only if users ask |
| 4 | Declined meetings | `Cancelled` |
| 5 | Do birthday and holiday calendars support `calendarView/delta`? | Open; needs a live account. Holiday events were reported without a subject in the delta reply (#67), worked around with a per-event read. If delta is not supported at all, fall back to a full read of the window and diff locally |
| 6 | Shared calendars from other users (`Calendars.Read.Shared`) | Not in v1 |
| 7 | Windows broker (WAM) for single sign-on | Later, optional |
| 8 | `RetryAfter` on `SourceUnavailableException` | Done |
