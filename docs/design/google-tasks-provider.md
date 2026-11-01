# Google Tasks provider

A task-only provider for [Google Tasks](https://developers.google.com/workspace/tasks) (the
lists behind the Tasks side panel in Gmail and Google Calendar and the Google Tasks mobile
app). Project: `AllMyTasks.Providers.Google`, shared with the Google Calendar provider.
Kind: `google`.

Tracking issue: [#26](https://github.com/jkruza/allmytasks/issues/26).

Status: implemented read-only, together with Google Calendar (see [Implementation notes](#implementation-notes)). v1 is read-only, like every other provider
(see [providers.md](providers.md#write-support-later)).

## Scope

In v1:

- Sign in with a Google account (personal Gmail or Workspace) through the OAuth 2.0
  installed-app flow.
- List the account's task lists; each becomes a `CollectionInfo` of kind tasks.
- Sync tasks per list, incrementally, including completed tasks and deletions.
- Subtasks (`ParentKey`), notes, due date, completion time, link to the task in Google.

Not in v1 (so absence is a decision, not a bug):

- Writing (create, edit, complete, move, delete). Needs the read-write scope; see
  [Scopes](#scopes).
- Recurrence, priority, labels and start date. The Google Tasks API has none of them
  (see [Mapping](#mapping-to-the-model)).
- Task assignment from Google Chat spaces and Docs, other than showing the title and link.
- Google Workspace service accounts / domain-wide delegation. Users sign in as themselves.
- Showing Google Calendar events. That is the Calendar provider's job; the two share
  sign-in only.

## One account, two capabilities

Google Calendar and Google Tasks use the same Google account and the same OAuth client.
Design decision: **one `google` source per Google account**, which implements `ITaskSource`,
`ICalendarSource` or both depending on the scopes the user granted. This matches the
"either or both" rule in [providers.md](providers.md#shape) and means the user signs in
once.

Consequences:

- A shared `GoogleAuth` component (authorisation, token refresh, scope bookkeeping) lives
  in `AllMyTasks.Providers.Google`. Tasks and Calendar clients take an `IGoogleAccessTokenProvider`
  and do not know about OAuth.
- The "Add account" flow asks which services to enable (Tasks, Calendar). Enabling one later
  uses **incremental authorisation**: request only the missing scope and merge the result,
  rather than asking the user to sign in from scratch.
- `ISourceFactory.Create` yields a source whose `GetCollectionsAsync` returns task lists
  when Tasks is granted and calendars when Calendar is granted.

This needs to be agreed with the Google Calendar design before either is built; see
[Open questions](#open-questions).

## Authentication

### Flow

OAuth 2.0 for installed apps (RFC 8252), authorisation code with **PKCE** and a **loopback
redirect** (`http://127.0.0.1:<random port>`):

1. App starts a short-lived `HttpListener` on a random loopback port.
2. App opens the system browser at Google's authorisation endpoint with `code_challenge`
   (S256), `state`, `scope`, `access_type=offline`, `prompt=consent` on first sign-in.
3. Google redirects to the loopback address; the listener validates `state`, reads `code`,
   answers with a "You can close this tab" page and shuts down.
4. App exchanges `code` + `code_verifier` for an access token (about one hour) and a
   refresh token.

No embedded web view: Google blocks sign-in from embedded browsers, and the system browser
gives the user their existing session, passkeys and 2FA. This is the same hand-off that
[providers.md](providers.md#configuration-and-credentials) reserves for an `IAuthFlow`.
Where no browser can be opened (headless Linux), the UI shows the URL to copy; the redirect
still has to reach the loopback listener, so the device flow is **not** an option for Tasks
(Google does not allow it for this scope). Revisit if that proves a real blocker.

### The OAuth client

The app ships with a Google Cloud OAuth client of type "Desktop app". For installed apps
Google documents the client secret as non-confidential; it is not what protects the user
(PKCE and the user's consent do). It still goes through configuration, not source:
`ClientId` and `ClientSecret` come from a build-time property, and forks and distro
packagers register their own client. The repo never contains the project's production
values in a file a contributor must edit.

Publishing the client has real consequences that the project owner must handle before a
release can use Google:

- The consent screen must be **in production** and the app **verified** if the scope is
  sensitive or restricted. Check the scope's classification in the Google API Services
  documentation at that time; do not assume.
- Apps left in **Testing** have refresh tokens that expire after 7 days and are limited to
  listed test users. Fine for development, unusable for releases.
- Verification needs a privacy policy URL, a home page and an app-owned domain. Without
  them the app can only be run by developers with their own client.

Until verification is done, README and `docs/` should say Google sign-in is for developers
and testers, and describe "bring your own client id" (config values `Google:ClientId` /
`Google:ClientSecret`, or `ALLMYTASKS_GOOGLE_CLIENT_ID` / `ALLMYTASKS_GOOGLE_CLIENT_SECRET`).

### Scopes

| Scope | Used for | When |
| --- | --- | --- |
| `https://www.googleapis.com/auth/tasks.readonly` | Read lists and tasks | v1 |
| `https://www.googleapis.com/auth/tasks` | Create, update, complete, delete | Only when write support lands; requested then, via incremental authorisation |
| `openid email profile` | Name the account (`DisplayName`) and tell two accounts apart | v1 |

Least privilege: v1 requests read-only. Asking for write access "to be ready" would show a
scarier consent screen for something the app does not do yet.

### Token storage

Per [storage-and-sync.md](storage-and-sync.md#credentials), tokens live only in the OS
secret store through `ISecretStore`, under the source id:

| Field | Value |
| --- | --- |
| `refresh_token` | Long-lived; the only thing needed to sign in again |
| `access_token` | Optional cache; fine to drop and re-fetch |
| `granted_scopes` | Space-separated, to know which capabilities are enabled |

Not stored in `SourceConfig`: the Google account email (non-secret) may be, as the
`username`-like display field. Tokens never appear in logs, exception messages,
`ToString()` or the raw payload.

Access tokens are refreshed on demand (when missing, or within 60 seconds of expiry, or on
a 401), with one in-flight refresh per source so concurrent syncs share the result.

### Auth failures

| Condition | Maps to |
| --- | --- |
| `invalid_grant` on refresh (revoked, password change on some accounts, 7-day Testing expiry, 6 months unused) | `SourceAuthException`; the engine marks the source "needs attention" and the UI offers "Sign in again" |
| 401 after a successful refresh | `SourceAuthException` |
| 403 with reason `insufficientPermissions` | `SourceAuthException` (scope missing, re-consent needed) |
| 403 `rateLimitExceeded` / `userRateLimitExceeded`, 429 | `SourceUnavailableException`; honour `Retry-After`, otherwise backoff |
| 403 `accessNotConfigured` (Tasks API disabled in the Cloud project) | `SourceException` with a diagnostic naming the API; this is a project setup problem, not the user's |
| 5xx, network, TLS, timeout | `SourceUnavailableException` |

"Sign in again" must replace the stored tokens under the **same source id** and check the
returned account is the same one (`sub` claim), so cached tasks are not silently attached to
a different person.

## API usage

Google Tasks REST API v1, base `https://tasks.googleapis.com/tasks/v1`.

| Need | Call |
| --- | --- |
| Task lists | `GET /users/@me/lists` (paged, `maxResults` up to 100) |
| Tasks in a list | `GET /lists/{tasklist}/tasks` (paged, `maxResults` up to 100) |

Responses are JSON; `System.Text.Json` with source-generated contexts for the few DTOs
(`TaskList`, `Task`, `Links`). No Google client library: the app uses two read calls, the
official `Google.Apis.*` packages are large, reflection-heavy and complicate trimming and
AOT. A hand-written client over `HttpClient` is smaller and easier to test with a fake
`HttpMessageHandler`. This is a package decision for the implementing PR to confirm.

Request details that matter:

- Always send `Authorization: Bearer`, `Accept-Encoding: gzip`, and a `fields` parameter
  that asks only for what the model needs.
- `tasks.list` is called with `showCompleted=true`, `showHidden=true` and, for deltas,
  `showDeleted=true`. Without `showHidden`, tasks the user "cleared" from the completed
  section vanish from results; without `showCompleted`, completed tasks vanish.
- Paging: follow `nextPageToken` until absent.
- The API has a per-project and per-user quota. The sync cost is one call per list per
  page, so the default sync interval and a cap on concurrent requests per source keep a
  user with many lists well inside it.

## Sync

### The API has no sync token

Unlike Google Calendar, the Tasks API has **no `syncToken`**. The nearest thing is
`updatedMin` (RFC 3339), which returns tasks changed at or after that time. Combined with
`showDeleted=true` it also returns tombstones (`deleted: true`). That is enough for a real
delta, but the token is a timestamp, not a server-issued cursor, so correctness depends on
how the client handles it.

### State token

`SyncState.Token` is a small JSON document, opaque to everyone else:

```json
{ "v": 1, "updatedMin": "2026-10-25T21:58:03.120Z", "fullAt": "2026-10-20T08:00:00Z" }
```

- `updatedMin`: the largest `updated` value seen in the previous sync, **from the server's
  clock**, never `DateTime.UtcNow` (avoids local clock skew).
- `fullAt`: when the last full snapshot was taken, used to schedule the safety resync.

### Algorithm

```
SyncTasksAsync(collection, previous):
  if previous == null  -> full snapshot
  parse previous; unknown "v" or unparsable -> throw SyncStateExpiredException
  if now - fullAt > FullResyncInterval (default 7 days) -> full snapshot
  else delta:
    GET tasks?updatedMin = previous.updatedMin - overlap (default 2 minutes)
         showCompleted=true&showHidden=true&showDeleted=true
    deleted == true  -> DeletedItemIds
    otherwise        -> Upserts (map; per-item failure -> SyncWarning)
    new updatedMin = max(updated) over everything returned, or unchanged if nothing returned
    return SyncResult.Delta(...)

full snapshot:
  GET tasks?showCompleted=true&showHidden=true      (no showDeleted, no updatedMin)
  return SyncResult.Snapshot(items, state{updatedMin = max(updated), fullAt = now},
                             confirmedEmpty = true when the list really returned zero tasks)
```

Why these choices:

- **Overlap window.** `updatedMin` is inclusive but writes made at nearly the same time as
  the last read can be missed by replication lag. Re-reading the last couple of minutes is
  cheap, and upserts are idempotent. Items returned twice are deduplicated by id.
- **Periodic full snapshot.** A timestamp cursor cannot prove nothing was missed. A weekly
  snapshot bounds how long a missed change (or a tombstone Google garbage-collected before
  we asked) can stay wrong. `IsFullSnapshot` makes the engine delete cached tasks absent from it.
  The interval is a constant in code, not a user setting.
- **Deleted list.** If `tasks.list` returns 404 for a list, the list was deleted: the
  collection disappears from `GetCollectionsAsync`; the source reports it so the engine
  drops its tasks (collection removal is the engine's job, see storage-and-sync).
- **Empty snapshot.** A full snapshot of an empty list sets `ConfirmedEmpty` only when the
  server returned a well-formed 200 with no `items`. Any parse oddity is a
  `SourceDataException`, never an empty snapshot.
- `previous` older than anything Google can answer is not a failure mode the API reports,
  so `SyncStateExpiredException` is thrown only for tokens we cannot parse or whose version
  we do not know. The weekly full snapshot covers the rest.

### Task lists as collections

`GET /users/@me/lists` gives `id`, `title`, `updated`. Mapping:

| Google | `CollectionInfo` |
| --- | --- |
| `id` | `Id` |
| `title` | `DisplayName` |
| (none) | `Color` = null; the user can set a local override |
| (always) | `Kind` = tasks, `IsReadOnly` = true in v1 |

The default list is the one whose `id` the API returns first or whose title is the
account's default, but the API does not flag it, so the provider does not try to detect it.
The "My Tasks" name is localised by Google, so it is shown as returned.

Lists are fetched on every sync pass (one cheap call), so renames and new lists show up
without a manual refresh. `list.updated` is not used as a cursor.

## Mapping to the model

| Google Tasks field | `TaskItem` |
| --- | --- |
| `id` | `Key.ItemId`; `Key.CollectionId` is the list id |
| `title` | `Title` (empty stays empty; the view substitutes) |
| `notes` | `Notes` |
| `due` | `Due` as `DueDate` (see below) |
| `status` `needsAction` / `completed` | `Status` = `Open` / `Completed` |
| `completed` | `CompletedAt` |
| `parent` | `ParentKey` (same source and list) |
| `webViewLink` | `Url` (fallback: `selfLink` is an API URL and is **not** used) |
| `updated` | `LastModified` |
| `links[]` (email, Docs) | not modelled in v1; kept in the raw payload |
| `hidden` | not a status; hidden tasks are completed tasks the user cleared. Still mapped as `Completed` |
| `deleted` | removal via `DeletedItemIds` |
| `position` | not modelled; ordering is a view concern |
| `etag` | kept with the raw payload for the future write path (`If-Match`) |
| (none) | `Priority.None`, `Labels` empty, `Start` null, `Recurrence` null |

### Due dates

Google stores a **date**, not a time. The API documents that on write the time part is
discarded, and on read it is returned as an RFC 3339 timestamp at `T00:00:00.000Z`. The
provider therefore:

- takes the **UTC calendar date** of `due` and returns `DueDate(DateOnly)`, never a
  `DueDateTime`;
- does not convert to the user's zone (a naive conversion would make "due Friday" become
  "due Thursday" for anyone west of UTC);
- documents that Google Tasks created in the Calendar UI with a time ("due 15:00") show as
  date-only in the API; the app cannot show the time.

### Subtasks

Google Tasks allows one level of nesting through `parent`. The child's `ParentKey` uses
the same source and list. Edge cases handled and tested:

- The parent is missing from a delta (parent unchanged, child changed): the child upsert
  still carries `ParentKey`; the store resolves it against the cache.
- The parent was deleted but the child was not returned: after a delta the child may point at a
  missing task. The store treats a dangling `ParentKey` as a top-level task; the weekly
  snapshot corrects the data.
- `parent` equal to the task's own id or a cycle: ignored, warning.

### Completed and hidden tasks

Completed tasks sync (they feed "completed" filters in [views.md](views.md#all-tasks)).
Large histories are common (years of completed items), so the first sync can be many pages.
v1 syncs everything; if that proves slow, a `completedMin`-style cut-off (the API supports
`completedMin`) can be added as a documented setting without changing the model.

## Errors and per-item problems

- Network and server errors follow the table in [Auth failures](#auth-failures) and
  [providers.md](providers.md#errors).
- A task that cannot be mapped (missing `id`, `status` not one of the two known values,
  `completed` set while `needsAction`) becomes a `SyncWarning` with the id, never fails the
  sync. Unknown `status` values map to `Open` with a warning, since Google may add states.
- Response bodies are never copied into exception messages; `Diagnostics` carries only the
  HTTP status, the Google error `reason`, and the request path with ids but without the
  query string.

## Privacy and security

- Tokens: only the OS secret store; never config, DB, logs, exceptions, crash reports.
- The loopback listener binds `127.0.0.1` only, accepts exactly one request that carries a
  valid `state`, and has a timeout (5 minutes).
- All requests are `https`; no option to disable certificate validation.
- Task text (titles and notes) is personal data. It is cached in the local SQLite database
  as for every provider, and never sent anywhere except back to Google when write support
  exists. Test fixtures use made-up data only.
- "Remove account" revokes the refresh token with Google's revocation endpoint
  (best effort, offline is fine) **and** deletes the secret store entries, cache and
  collections for the source.
- The app's Google API use must stay within Google's API Services User Data Policy
  (limited use). The README states what data is read and that none leaves the device.

## UI notes

Kept small; the schema-driven form in [providers.md](providers.md#configuration-and-credentials)
cannot express OAuth, so the Google source supplies its own add-account view model:

1. "Sign in with Google" button; a status line while the browser is open; Cancel.
2. After consent: account email shown, checkboxes for services (Tasks now; Calendar once
   that provider exists), list picker (visibility per task list).
3. On `SourceAuthException`: the source row shows "Sign in again".

No Google branding is invented: use Google's published sign-in button guidelines when the
button is built.

## Testing

All against a fake `HttpMessageHandler` with recorded, made-up JSON; no network in unit
tests.

- **Mapping**: due date at `T00:00:00.000Z` becomes the same `DateOnly` in zones east and
  west of UTC; completed with `completed` timestamp; hidden; subtask; empty title; notes
  with newlines; unknown status.
- **Sync**: first sync is a snapshot; delta with changes and tombstones; overlap
  deduplication; empty delta keeps `updatedMin`; weekly full snapshot replaces the cache;
  empty list `ConfirmedEmpty`; paging across 3 pages; unparsable or unknown-version token
  throws `SyncStateExpiredException`.
- **Auth**: refresh when expired; one refresh for concurrent calls; `invalid_grant` becomes
  `SourceAuthException`; 429 with `Retry-After`; account mismatch on re-sign-in is refused.
- **Loopback listener**: wrong `state` rejected, second request ignored, timeout.
- **Secrets**: a test asserts tokens and codes never appear in logs, exception messages or
  `ToString()` output of any type in the provider.
- **Optional manual test**: a throwaway Google account with a few lists. Documented, not in
  CI.

## Libraries

| Need | Choice |
| --- | --- |
| HTTP | `HttpClient` via `IHttpClientFactory`, one named client per provider |
| JSON | `System.Text.Json`, source-generated |
| OAuth / PKCE | Small in-house component (a few hundred lines, easy to test); the listener and PKCE are standard. Revisit `Duende.IdentityModel.OidcClient` only if Microsoft and Google OAuth end up sharing more than the loopback helper |
| Google API client | Not used (see [API usage](#api-usage)) |

Any added package follows the `CLAUDE.md` rule: version in `Directory.Packages.props`, run
`dotnet restore`, commit the `packages.lock.json` files.

## Rollout

1. Shared `GoogleAuth` + loopback helper + token store, with tests (also used by Calendar).
2. Task lists and one-list full snapshot.
3. Delta sync, tombstones, weekly resync.
4. Add-account UI and "Sign in again".
5. Docs: README note on Google sign-in status, bring-your-own-client-id instructions.

Dependencies: the sync engine and secret store from M1 to M3 of the [roadmap](roadmap.md)
and an `IAuthFlow` seam in the add-account UI. The OAuth client verification (see
[The OAuth client](#the-oauth-client)) is a project task outside the code and gates a public
release of this provider.

## Implementation notes

Implemented in `AllMyTasks.Providers.Google` next to the calendar code; the sign-in, client id handling and every difference from this
design that is not task-specific are in the
[Google Calendar implementation notes](google-calendar-provider.md#implementation-notes). Task-specific points:

- Sync follows the algorithm above: `updatedMin` minus a two-minute overlap with `showDeleted=true` for deltas, a full snapshot
  (no `showDeleted`) on the first sync and once a week. The state is `{"v":1,"updatedMin":...,"fullAt":...}`; an unparsable or
  unknown-version state throws `SyncStateExpiredException`. For an empty list `updatedMin` starts at the local time of the snapshot
  because no server timestamp exists yet; the weekly snapshot bounds any skew.
- `ConfirmedEmpty` needs a `tasks#tasks` document with no items; anything else that parses is not trusted as empty, and a body that
  does not parse is a `SourceDataException`.
- A 404 on a list keeps the cache and adds a warning, like a missing calendar.
- Tasks with an unreadable due date are skipped with a warning; an unknown status is open with a warning; a completion time on
  an open task is dropped with a warning; a task that is its own parent loses the parent with a warning.
- Hand-written client; no `Google.Apis.*` packages. No package was added.
- Not individually confirmed on a real account (the provider as a whole has been tried): whether the API ever sends a due time, how Chat/Docs-assigned tasks appear, and the exact
  tombstone behaviour of `showDeleted` with `updatedMin`.

## Open questions

**Decided 2026-10-26 (maintainer):** the leanings below are accepted: one `google` source per account for Tasks and Calendar, shared `GoogleAuth` classes in `AllMyTasks.Providers.Google`, and all completed tasks synced in v1. Jarek owns the Google Cloud project and verification (see the Google Calendar design).

| Question | Leaning |
| --- | --- |
| One `google` source per account for Tasks and Calendar, or two separate source kinds? | One source (above). Needs agreement with the Google Calendar design. |
| Where do the shared `GoogleAuth` classes live? | `AllMyTasks.Providers.Google`; Microsoft providers get their own project and may share a generic loopback/PKCE helper in Core |
| Sync completed tasks older than N months? | Sync all in v1; revisit on evidence |
| Does Google ever deliver tasks with a due time through the API? | Treated as date-only; confirm against a live account while implementing |
| Which scopes does Google classify as sensitive, and how long does verification take? | Check with the Google Cloud console before choosing a release date |
| Android/iOS-only "Tasks assigned to me" from Chat/Docs: do they appear in `tasks.list`? | Assume yes as ordinary tasks; verify |
