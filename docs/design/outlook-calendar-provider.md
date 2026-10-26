# Outlook calendar provider (Microsoft Graph, read-only)

Calendar events from Outlook.com / Hotmail / Live (personal Microsoft accounts) **and** Microsoft 365
work or school accounts, through the Microsoft Graph API. Project: `AllMyTasks.Providers.Microsoft`,
shared with the Microsoft To Do provider ([microsoft-todo-provider.md](microsoft-todo-provider.md)).

Status: design only. Nothing here is implemented. Read-only in v1: no event is ever created, changed,
answered or deleted, and `CollectionInfo.IsReadOnly = true`.

## Scope

In v1:

- Sign in with a personal Microsoft account or a work/school account, using the same flow.
- List the account's calendars (own, shared with the user, subscribed holiday calendars) and let the
  user hide the ones they do not want, as for CalDAV.
- Sync events incrementally with Graph delta queries: titles, times, all-day events, location, notes,
  online-meeting link, categories, status, free/busy, recurring series and their exceptions.
- Several Microsoft accounts at once (a personal and a work account, or two tenants). Each is its own
  source.

Not in v1 (decisions, not oversights):

- Writing, RSVP, creating meetings. See [providers.md](providers.md#write-support-later).
- Attendees, organizer, reminders, attachments. The model has no place for them yet
  ([details-pane.md](details-pane.md)); the raw payload keeps them for later.
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
  Auth/MicrosoftAuth         MSAL wrapper (below)
  Graph/GraphClient          HttpClient wrapper: auth header, paging, throttling, error mapping
  Calendar/...               this document
  Todo/...                   microsoft-todo-provider.md
```

### App registration

One registration owned by the project, a **public client** (no client secret exists to leak):

| Setting | Value |
| --- | --- |
| Supported account types | Accounts in any organizational directory **and** personal Microsoft accounts |
| Platform | "Mobile and desktop applications", redirect URI `http://localhost` (MSAL picks a free port) |
| Delegated permissions | `User.Read`, `Calendars.Read`, `Tasks.Read` (To Do), `offline_access` |
| Allow public client flows | Yes (needed for device code fallback) |

- The **client id is not a secret** and is committed (`MicrosoftApp.ClientId`). Anyone who builds from a
  fork can override it by configuration and register their own app; the docs say how.
- The registration must be made in a tenant the maintainer controls (a personal account cannot own a
  multi-tenant registration). Publisher verification is recommended: without it, many work tenants
  refuse user consent (see [Work and school accounts](#work-and-school-accounts)). This is a manual
  maintainer task tracked in the issue, not something code can do.
- Least privilege: `Calendars.Read`, never `ReadWrite`, until write support is designed.

### Sign-in

MSAL.NET (`Microsoft.Identity.Client`) `PublicClientApplication`, authority
`https://login.microsoftonline.com/common`, which resolves to the right tenant, or to the consumer
tenant, from the account typed in.

1. **Interactive, system browser** (default on all three OSes): MSAL opens the default browser and
   listens on a loopback port. No embedded web view, so password managers, passkeys, MFA and
   conditional access work as in the browser.
2. **Device code** (fallback when no browser can be launched, e.g. headless or remote Linux): the
   dialog shows a short code and `microsoft.com/devicelogin`. Some work tenants block device code via
   conditional access; the error is shown as is.
3. **Silent** on every sync: `AcquireTokenSilent` with the stored account; refresh happens inside MSAL.

After sign-in the source stores in `SourceConfig` (not secret): `homeAccountId`, `tenantId`, the
sign-in name (`user@contoso.com`), and an `accountType` of `personal` or `work` (tenant id
`9188040d-6c67-4c5b-b112-36a304b66dad` is the consumer tenant). `DisplayName` defaults to
`Outlook (user@contoso.com)`.

Sign-in is not a schema-driven form; per [providers.md](providers.md#configuration-and-credentials) a
provider may supply a view model. The minimal Core abstraction, to be refined when implemented:

```csharp
public interface IInteractiveSignIn          // lives in Core; the Avalonia app shows the UI
{
    // Called by the provider when it needs the user to act, e.g. show a device code.
    void ShowDeviceCode(string userCode, Uri verificationUri);
}
```

### Token storage

Tokens never touch the database or config files ([decision 6](README.md#decision-log)). MSAL's token
cache is serialised in its `BeforeAccess`/`AfterAccess` callbacks into `ISecretStore` under
`(sourceId, "msal-cache.N")`. We deliberately do **not** use `Microsoft.Identity.Client.Extensions.Msal`:
it would add a second, parallel secret store (own Keychain/libsecret/DPAPI handling and its own
plaintext fallback) beside ours, and the Linux PIN-file rules in
[storage-and-sync.md](storage-and-sync.md#linux) would not apply to it.

The cache blob is larger than some backends allow per entry (Windows Credential Manager caps a
credential at 2560 bytes; an account's cache is several KB). The provider therefore base64-encodes the
blob and splits it into fixed-size fields (`msal-cache.0`, `msal-cache.1`, ...) plus a count, and
reads them back the same way. `ISecretStore.DeleteAsync(sourceId)` removes them all when the source is
removed. Chunking sits in the Microsoft project, not in `ISecretStore`, so the store keeps its simple
contract.

Removing a source also calls `RemoveAsync` on the MSAL account. It does **not** revoke the grant at
Microsoft; the UI says where to do that (`account.microsoft.com/privacy/app-access` for personal,
`myapps.microsoft.com` for work).

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
- **Throttling.** `429` and `503` with `Retry-After`: wait that long (capped, default 2 minutes) up to
  3 attempts inside the client. If still throttled, throw `SourceUnavailableException` that **exposes
  the server's wait** (`RetryAfter` property, added to the exception in the implementing PR) so the
  engine schedules the next attempt no earlier than that. Without the header, exponential backoff via
  `SourceBase.RetryAsync`. Calendars are synced sequentially per source, never in parallel.
- **Error mapping:**

  | Graph / MSAL | Exception | Notes |
  | --- | --- | --- |
  | `MsalUiRequiredException`, `invalid_grant`, 401 after a refresh | `SourceAuthException` | "Sign in again"; includes password change, MFA reset, token revoked, conditional access |
  | 403 `ErrorAccessDenied`, `AADSTS65001` / `AADSTS90094` / `AADSTS650056` at sign-in | `SourceAuthException` | Consent or admin approval needed, see below |
  | 404 `MailboxNotEnabledForRESTAPI`, `MailboxNotSupportedForRESTAPI` | `SourceException` | "This mailbox is not in Exchange Online" (on-premises) |
  | 404 on a known calendar | warning, collection marked missing | Not auto-deleted, same rule as CalDAV |
  | 410 `Gone`, `syncStateNotFound`, `resyncRequired` | `SyncStateExpiredException` | Engine re-syncs from scratch once |
  | 429, 503, 504, network, TLS, timeout | `SourceUnavailableException` | Keeps cache |
  | Other 4xx | `SourceException` | |

- **Logging.** Log method, path template (`/me/calendars/{id}/calendarView/delta`), status and
  duration. Never log query strings (they carry `$deltatoken`/`$skiptoken` and date ranges), headers,
  bodies or the account name.

## Personal vs work accounts

The sync code is identical; only these differ.

| | Personal (MSA) | Work or school |
| --- | --- | --- |
| Sign-in | Consumer sign-in page, optional passkey | Tenant branding, MFA, conditional access |
| Consent | User consents themselves | Tenant policy: user consent, or **admin approval required** |
| Calendars | Default calendar, birthdays, holidays | Plus shared calendars, delegate-visible ones |
| Mailbox | Created with the account | May be missing, on-premises or disabled |
| Refresh token lifetime | Long, sliding | Tenant policy; sign-in frequency and device compliance can force re-auth |
| Sensitivity | Private events visible to the owner | Shared calendars may expose only free/busy ("limited details") |

### Work and school accounts

Things that will go wrong for real users, with the intended behaviour:

- **Admin consent required** (`AADSTS90094`, `AADSTS65001`, "Need admin approval"). The Add source dialog
  shows a plain explanation: the organisation requires an administrator to approve the app, with the
  application (client) id and the permissions requested, so the user can forward it. No retry loop.
  `Calendars.Read` and `Tasks.Read` are normally user-consentable, but tenants can restrict consent to
  verified publishers or disable it.
- **Conditional access** (MFA every N hours, compliant device, trusted location). Surfaces as
  `MsalUiRequiredException` when silent sign-in is not enough; the source is marked "needs attention"
  with a Sign in again button. An unmanaged desktop app may be blocked outright by "require compliant
  device"; we cannot work around that and say so.
- **Calendar shared with limited details.** Subject may read "Busy". Shown as is.
- **Tenant switch.** Same human, different tenants: separate sources, separate caches.

## Collections

`GET /me/calendars` (all calendar groups; follows `@odata.nextLink`).

| Graph `calendar` | `CollectionInfo` |
| --- | --- |
| `id` | `Id` |
| `name` | `DisplayName` (suffix with owner when the calendar is shared and owned by someone else) |
| `hexColor` (`#RRGGBB`) | `Color`; if empty or `auto`, none, so the app picks one |
| `canEdit` | not used in v1; `IsReadOnly` is always `true` |
| `isDefaultCalendar` | default calendar sorts first, and is the only one visible on first run for sources with more than 5 calendars |

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

### Refresh interval

Polling only; the global default of 15 minutes applies ([storage-and-sync.md](storage-and-sync.md)),
overridable per source. The first sync of a large calendar runs calendar by calendar so the first
calendar's events appear before the last finishes.

## Mapping to the model

| Graph `event` | `CalendarEvent` |
| --- | --- |
| `id` (immutable) | `Key.ItemId`; `Key.CollectionId` = calendar id; `RecurrenceId` stays `null` (see above) |
| `subject` | `Title` (empty → empty string; the view adds the placeholder) |
| `start`/`end` + `isAllDay` | `TimedEvent` or `AllDayEvent`, see below |
| `originalStartTimeZone` | `TimedEvent.TimeZoneId` as IANA |
| `body.content` (requested as text) | `Description` |
| `location.displayName` (else `locations[].displayName` joined with `; `) | `Location` |
| `onlineMeeting.joinUrl`, else `webLink` | `Url` |
| `isCancelled` or `responseStatus.response = declined` | `Status = Cancelled` |
| `showAs = tentative` or `responseStatus.response = tentativelyAccepted` | `Status = Tentative` |
| otherwise | `Status = Confirmed` |
| `showAs = free` | `Availability.Free` |
| `busy`, `tentative`, `oof`, `workingElsewhere`, `unknown` | `Availability.Busy` (finer value only in the raw payload) |
| `categories` | `Categories` |
| `lastModifiedDateTime` | `LastModified` |
| `type`, `seriesMasterId`, `changeKey`, `attendees`, `organizer`, `sensitivity`, ... | raw payload only |

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
| OAuth, token cache | `Microsoft.Identity.Client` (MSAL.NET), MIT | The supported library; handles PKCE, loopback, device code, refresh. System-browser flow needs no UI package |
| Graph HTTP | `HttpClient` via `IHttpClientFactory` + own thin client | No `Microsoft.Graph` SDK, see above |
| JSON | `System.Text.Json` source generator | Already in the framework |

Package additions follow `CLAUDE.md`: version in `Directory.Packages.props`, `dotnet restore`, commit
lock files. Verify licence and current version at that time. MSAL and the app's trimming/AOT settings
(if any) need a smoke test per OS.

## Testing

- **Mapping tests** from fixture JSON (made-up data only): timed, all-day, multi-day all-day, UTC,
  Windows zone names, unknown zone, cancelled, declined, tentative, free, no subject, no body, online
  meeting, `@removed`, malformed item among good ones.
- **Sync tests** with a fake `HttpMessageHandler` serving recorded pages: first sync across several
  `nextLink` pages, empty delta, update, delete, window roll → snapshot, `410` → `SyncStateExpiredException`,
  a `nextLink` pointing to another host is refused and **no `Authorization` header is sent**.
- **Throttling tests:** `429` with `Retry-After`, without, repeated.
- **Auth tests:** a fake `IPublicClientApplication` for silent success, `MsalUiRequiredException`,
  consent errors; assert tokens and delta links never appear in logs, exceptions or `ToString()`.
- **Secret chunking tests:** round-trip a cache blob of 1 KB, 5 KB, 50 KB; deletion removes all chunks.
- **Opt-in manual checklist** (not CI): one personal account and one work tenant, including a calendar
  with a recurring series with exceptions, an all-day event created in another zone, and a shared
  calendar. Throwaway accounts only; no real data in the repo.

## Security and privacy

- Public client, no secret anywhere. Tokens only in the OS secret store.
- Bearer token only to `graph.microsoft.com` (and MSAL's own calls to `login.microsoftonline.com`).
- Least-privilege scopes, read-only. The consent screen is the user's check on that; the README states
  the permissions and why.
- Event content (titles, notes, locations) is personal data: not logged, not in exception messages,
  not sent anywhere else, not in test fixtures. `SourceException.Diagnostics` may carry the Graph error
  `code` and request id, never the event text.
- No telemetry. Microsoft sees normal API traffic from the user's own sign-in.
- Treat Graph responses as untrusted input: page and size caps, bounded loops (a delta round stops
  after 500 pages and reports `SourceException`), no following of links off-host.

## Milestone sketch

A candidate for the "Later" part of the [roadmap](roadmap.md):

1. `AllMyTasks.Providers.Microsoft` skeleton, MSAL sign-in (interactive + device code), cache in
   `ISecretStore` with chunking, `GraphClient` with pinning and throttling. Add-source dialog
   (account type shown, consent errors explained). *Done when* a personal and a work account can sign
   in, restart the app, and stay signed in.
2. Calendars list and `calendarView/delta` sync with the window, mapping and tests. *Done when* real
   events appear in all four views and incremental sync is visible in the source status.
3. Hardening from the manual checklist (zones, all-day, shared calendars, tenant policies).

## Open questions

**Decided 2026-10-26 (maintainer):** all proposals below are accepted. Notably: Jarek owns the Entra app registration and publisher verification is pursued before work-account support is announced (#1); v1 stores expanded occurrences without `Recurrence` (#2); window -3/+13 months (#3); declined meetings are shown as `Cancelled` (#4); `retryAfter` on `SourceUnavailableException` goes into the implementing PR (#8). Shared calendars and the Windows broker stay out of v1.

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Who owns the Entra app registration, and is publisher verification worth doing? Unverified multi-tenant apps are blocked from user consent in many tenants, which would make "work accounts" fail for those users | Jarek creates it in a tenant he controls; pursue verification before announcing work-account support. Needs a decision before implementation |
| 2 | Accept storing expanded occurrences with no `Recurrence` (deviation from decision 4), or invest in master + RRULE mapping now? | Accept for v1, add `SeriesId` later, revisit when calendar-wide delta reaches v1.0 |
| 3 | Window size: -3/+13 months, rolled when under 9 months remain | Start with this; make it a setting only if users ask |
| 4 | Declined meetings: show as cancelled (Outlook hides them) or drop them from the sync? | `Cancelled`, so the view's existing cancelled handling applies and the user can see why a slot is free |
| 5 | Birthdays calendar and holiday calendars: do they support `calendarView/delta`? | Test on a real account; if not, fall back to a full read of the window for those calendars and diff locally |
| 6 | Shared calendars from other users (`Calendars.Read.Shared`) | Not in v1; ask for the scope only when a user needs it |
| 7 | Windows broker (WAM) for single sign-on with the Windows account | Later, optional, Windows-only package |
| 8 | Allow `retryAfter` on `SourceUnavailableException`? It is a small change to Core exceptions | Yes, in the implementing PR; the To Do provider needs it too |
