# Microsoft To Do provider (read-only)

Tracking issue: [#24](https://github.com/jkruza/allmytasks/issues/24).

A task provider for [Microsoft To Do](https://learn.microsoft.com/en-us/graph/todo-concept-overview),
for **personal Microsoft accounts** (outlook.com, hotmail, live), through the Microsoft Graph To Do API.

Status: implemented for personal accounts, read-only. **Work and school accounts are not supported yet**; the
reasons and what they need are in
[outlook-calendar-provider.md](outlook-calendar-provider.md#work-and-school-accounts-not-in-v1). The code has
only been exercised with fixtures, not a live account (see [Testing](#testing)).

It lives in the shared Microsoft project, `AllMyTasks.Providers.Microsoft`, next to the
Outlook calendar provider. A source of kind `microsoft` implements `ITaskSource`,
`ICalendarSource`, or both. This doc covers only the To Do half. Sign-in, token handling and
the Graph HTTP client are designed once in
[outlook-calendar-provider.md](outlook-calendar-provider.md#microsoft-identity-and-graph-client-shared-with-to-do)
and are summarised here only where To Do depends on them.

Read-only in v1: no `POST`, `PATCH` or `DELETE`, and `CollectionInfo.IsReadOnly = true`.

## Scope

In v1:

- Sign in with a personal Microsoft account; several accounts at once, one source each.
- List the user's To Do lists (including the default Tasks list, Flagged email, and lists
  shared with the user) as task-list collections.
- Sync tasks incrementally per list with Graph delta queries.
- Title, notes, due date, start date, status, completion time, importance, categories,
  recurrence (shown, not expanded), and checklist items as subtasks.

Not in v1 (documented so absence is a decision, not a bug):

- Writing (complete, edit, create, delete). Needs `Tasks.ReadWrite`; see
  [Write support later](#write-support-later).
- Attachments, linked resources, reminders (`isReminderOn`, `reminderDateTime`).
- Planner tasks and Microsoft Teams tasks that are not in To Do lists.
- Microsoft Graph for national clouds (US Government, China). The Graph host is a constant in
  one place so it can be made configurable.
- Application permissions / client-credential flows. This is a desktop app acting for a user.

## Sign-in and permissions

Designed once in the Outlook doc and shared: [Application (client) id](outlook-calendar-provider.md#application-client-id)
(there is no registration-free sign-in; the id is configuration) and [Sign-in](outlook-calendar-provider.md#sign-in).
The To Do specifics:

| Topic | Decision |
| --- | --- |
| Authority | `https://login.microsoftonline.com/consumers`: personal accounts only. |
| Scopes | `Tasks.Read`, `User.Read`, `offline_access`, plus `Calendars.Read` when the same source also reads the calendar. Least privilege: not `Tasks.ReadWrite` until writing exists. |
| Flow | Authorization code + PKCE in the system browser with a loopback redirect, on the first sync after the source is saved; the refresh token (the only stored credential) is redeemed silently afterwards. |
| One source, both features | The `microsoft` source reads Outlook calendars and To Do lists with one sign-in. The form has two switches; with both off it reads both. Changing them asks for a new sign-in, because the permissions change. |

Failure modes:

| Situation | Graph / token signal | Result |
| --- | --- | --- |
| Refresh token revoked, expired, or permissions changed | `invalid_grant`, `interaction_required`, `consent_required` | Token deleted; `SourceAuthException`; source marked "needs attention"; edit and save the source to sign in again. |
| Wrong or unusable client id | `invalid_client`, `unauthorized_client` | `SourceAuthException` naming the client id as the thing to check. |
| Account has no To Do mailbox | HTTP 404 `MailboxNotEnabledForRESTAPI` | `SourceException` ("no calendar or To Do mailbox that Graph can read"). Not retried. |
| `401`, `403` from Graph | permission or policy | `SourceAuthException`. |

## Mapping lists to collections

`GET /me/todo/lists` (paged with `@odata.nextLink`) on every `GetCollectionsAsync`. The list
count is small, so lists use a plain GET; only tasks use delta (the list delta endpoint exists
and can be adopted if it proves worthwhile).

| `todoTaskList` | `CollectionInfo` |
| --- | --- |
| `id` | `Id` |
| `displayName` | `DisplayName`. The default list (`wellknownListName` = `defaultList`) is named "Tasks" by Microsoft; kept as returned, and not renamed, because users know it by that name. |
| `wellknownListName` `flaggedEmails` | An ordinary collection; kept visible but listed after the others |
| `isOwner = false` / `isShared = true` | Ordinary collection; shared lists are synced like any other |
| none | `Kind = TaskList`, `Color = null` (To Do lists have no colour in Graph), `IsReadOnly = true` |

A list that disappears from the response is removed with its tasks by the engine's collection
handling. A list that answers 404 on sync keeps its cached tasks and adds a warning, same as
CalDAV.

## Sync strategy

Per list, with `ITaskSource.SyncTasksAsync(collection, previous)`:

```
first sync (previous == null):
  GET /me/todo/lists/{id}/tasks/delta?$expand=checklistItems
      Prefer: odata.maxpagesize=100
  follow @odata.nextLink until a page carries @odata.deltaLink
  return full snapshot (IsFullSnapshot = true), state = the deltaLink

later syncs:
  GET <saved deltaLink>
  follow nextLinks, same as above
  entries with "@removed" -> DeletedItemIds; others -> Upserts
  return delta, state = the new deltaLink
```

- The **state token** is the final `@odata.deltaLink` URL. It encodes the query parameters
  (`$expand`) so they are given only on the first call.
- Before following any `nextLink` or `deltaLink`, check the scheme is `https` and the host is
  the Graph host. Graph URLs are never trusted to point anywhere else, and the bearer token is
  never sent to another host. A state token that fails the check is treated as expired.
- **HTTP 410 Gone** (`syncStateNotFound`, `resyncRequired`) throws `SyncStateExpiredException`;
  the engine retries once from scratch.
- An empty delta response is normal. A `200` with no `deltaLink` and no `nextLink` is a
  `SourceDataException`, never an empty snapshot that would clear the cache.
- A task moved to another list gets a **new id** in the new list (the id changes on move), so
  it shows up as a removal in one list and an upsert in another. No special handling.
- **Checklist items and delta.** Delta supports `$expand`, so checklist items arrive embedded
  in their task. It is not documented whether editing only a checklist item bumps the
  parent's `lastModifiedDateTime` and so appears in the next delta. Until verified against
  real accounts, the provider returns a **full snapshot every 24 hours** (state token carries
  the time of the last snapshot) in addition to deltas, and on a manual "Refresh now". See
  [Open questions](#open-questions).
- Responses are capped (default 20 MB per page) and every request has a timeout.

### Throttling and errors

Graph throttles with `429` and a `Retry-After` header; the shared client honours it. Mapping
to the provider error hierarchy:

| Condition | Exception |
| --- | --- |
| Network, DNS, TLS, timeout, `5xx`, `429` | `SourceUnavailableException` (carries `RetryAfter` when sent) |
| `401`, `403`, sign-in needed again | `SourceAuthException` |
| `410` on a delta link | `SyncStateExpiredException` |
| Unparseable JSON, missing `id`, wrong shape | `SourceDataException` |
| Single task with bad data (e.g. unparseable date) | Skipped, `SyncWarning(taskId, message)` |

Messages name the error type and Graph error code only. They never include URLs with tokens,
delta links, or `Authorization` values.

## Mapping tasks to the model

`ItemKey(sourceId, listId, taskId)`.

| `todoTask` | `TaskItem` | Notes |
| --- | --- | --- |
| `id` | `Key.ItemId` | |
| `title` | `Title` | Empty titles are allowed by To Do; the view already substitutes one |
| `body` (`content`, `contentType`) | `Notes` | `text` as is. `html`: tags stripped and entities decoded to plain text; the model has no HTML rendering (see [details-pane.md](details-pane.md) non-goals). Raw body kept in the raw payload |
| `dueDateTime` | `Due` | See [Due and start](#due-and-start) |
| `startDateTime` | `Start` | Same rules |
| `status` | `Status` | See below |
| `completedDateTime` | `CompletedAt` | Converted from its `dateTimeTimeZone` |
| `importance` | `Priority` | `high` to High, `normal` to None, `low` to Low. `Medium` is unused: To Do has no middle level |
| `categories` | `Labels` | Category names as given |
| `recurrence` | `Recurrence` | See [Recurrence](#recurrence) |
| `lastModifiedDateTime` | `LastModified` | |
| `@odata.etag` | dropped | Needed for writes later; add with the raw payload |
| `checklistItems[]` | subtasks | See [Checklist items](#checklist-items-as-subtasks) |
| (none) | `Url` | See [Links](#links) |
| `isReminderOn`, `reminderDateTime`, `hasAttachments`, `createdDateTime`, `bodyLastModifiedDateTime`, `linkedResources` | dropped | The model has no raw-payload field yet, so unlisted fields (and the etag) are not kept |

### Status

To Do's app shows only open and completed; Graph has five values.

| `taskStatus` | `TaskItemStatus` |
| --- | --- |
| `notStarted` | `Open` |
| `inProgress` | `InProgress` |
| `waitingOnOthers` | `InProgress` |
| `deferred` | `Open` |
| `completed` | `Completed` |
| unknown future value | `Open`, plus a `SyncWarning` |

`Cancelled` is never produced. Raw status stays in the payload.

### Due and start

`dueDateTime` and `startDateTime` are `dateTimeTimeZone` values: a local date-time string plus
a time zone name. The To Do apps only let users pick a **date**; the stored value is that
date at `00:00:00`. Tasks created by Outlook or other clients can carry a time.

Rules:

- **No `Prefer: outlook.timezone` header.** Asking Graph to convert to another zone would
  shift a stored midnight onto the previous or next day. Values are read exactly as stored.
- A value at exactly `00:00:00` is a **date-only** value: `DueDate(date)` taken straight from
  the date part, with no zone conversion. This is the "due Friday" case.
- Any other time becomes `DueDateTime` using the value's own time zone.
- Time zone names are Windows ids (`W. Europe Standard Time`) or IANA ids, or `UTC`;
  converted with `TimeZoneInfo.TryConvertWindowsIdToIanaId`, the same helper the iCalendar
  provider uses. An unknown zone becomes UTC and a `SyncWarning`.
- `Start` is `DateTimeOffset?` in the model, so a date-only start is the start of that day in
  the user's display zone at read time. Views treat it as "not before that day".

The midnight heuristic misreads a task a user deliberately timed at exactly `00:00`. That is
rare and the cost is showing it as an all-day task, so it is accepted. Needs checking against
real accounts, see [Open questions](#open-questions).

### Checklist items as subtasks

Each `checklistItem` (`id`, `displayName`, `isChecked`, `checkedDateTime`, `createdDateTime`)
becomes a `TaskItem` with `ParentKey` set to the task:

- `ItemId` = `{taskId}/{checklistItemId}` (task ids are not unique to the checklist item
  alone, and the `/` cannot occur in either id).
- `Title` = `displayName`; `Status` = Completed when `isChecked`, else Open;
  `CompletedAt` = `checkedDateTime`.
- `Due` is null; a checklist item is shown under its parent, not as a dated task of its own.
- When a parent is upserted, the provider emits the full current set of its checklist items.
  Items that disappeared from a parent are reported in `DeletedItemIds`; to know which, the
  state token carries, per task, the checklist item ids from the last sync. When a task is
  removed, all its checklist ids are removed with it. The token is capped in size by storing a
  hash plus ids only for tasks that have checklist items.

Checklist items never nest, so the model's one level of `ParentKey` is enough.

### Recurrence

`patternedRecurrence` (a `recurrencePattern` plus a `recurrenceRange`) is mapped to an RFC 5545
`RRULE` string, so the existing `Recurrence` record stays unchanged:

| `pattern.type` | RRULE |
| --- | --- |
| `daily` | `FREQ=DAILY;INTERVAL=n` |
| `weekly` | `FREQ=WEEKLY;INTERVAL=n;BYDAY=<daysOfWeek>` |
| `absoluteMonthly` | `FREQ=MONTHLY;INTERVAL=n;BYMONTHDAY=<dayOfMonth>` |
| `relativeMonthly` | `FREQ=MONTHLY;INTERVAL=n;BYDAY=<daysOfWeek>;BYSETPOS=<index>` |
| `absoluteYearly` | `FREQ=YEARLY;INTERVAL=n;BYMONTH=<month>;BYMONTHDAY=<dayOfMonth>` |
| `relativeYearly` | `FREQ=YEARLY;INTERVAL=n;BYMONTH=<month>;BYDAY=<daysOfWeek>;BYSETPOS=<index>` |

`range.type`: `noEnd` adds nothing, `endDate` adds `UNTIL`, `numbered` adds `COUNT`.
`firstDayOfWeek` maps to `WKST`. An unrecognised pattern gets no `Recurrence` and a
`SyncWarning`; the task itself still syncs.

**Recurring tasks are not expanded.** In To Do, completing a recurring task creates a *new*
task with the next due date, so the list already holds only the current occurrence. The app
shows that task with a repeat indicator and does not synthesise future occurrences. This is
the opposite of calendar events, so the task view logic must not feed task recurrence to
`IRecurrenceExpander`. Confirm in [task-details.md](task-details.md) wording when implemented.

### Links

Graph does not return a web link for a task. The provider builds one from the task id,
`https://to-do.live.com/tasks/id/{url-encoded id}/details` (personal accounts). This is
the To Do web app's deep-link form and is not part of the documented API, so `Url` is
best-effort: if opening one misbehaves, fall back to the web app root (`https://to-do.live.com/tasks/`).
Never include a token in it.

## Source configuration

| Field | Kind | Notes |
| --- | --- | --- |
| Display name | config | Whatever the user types |
| Application (client) ID | config | Optional; falls back to the id built into the app. See the Outlook doc |
| Outlook calendar, Microsoft To Do | config | Two switches; both off means both on |
| Refresh token | secret (`refresh-token`) | The only credential stored |
| Refresh interval | config | Global default 15 minutes, per-source override, as other providers |

No client secret exists: the app is a public client. The application (client) id is public by design but is not
committed: it is entered per source or embedded at build time from `AMT_MICROSOFT_CLIENT_ID`.

## Security

- The refresh token: secret store only. Never log them, the `Authorization` header,
  delta links, or full request URLs (strip the query string before logging).
- Follow `nextLink` and `deltaLink` only to the Graph host over `https`. Do not follow
  redirects to other hosts or from `https` to `http`.
- Parse JSON with `System.Text.Json`, size-capped; treat every string as untrusted (titles
  and bodies can contain anything, including HTML and control characters).
- Fixtures contain made-up data. Never commit a real Graph response.

## Write support later

Not designed in detail. Notes so nothing in v1 blocks it:

- Needs `Tasks.ReadWrite`; the user is asked to consent again, so v1 does not ask for it up
  front.
- `@odata.etag` and the raw payload are kept now. Graph accepts `If-Match` on `PATCH`.
- Smallest, most valuable first: complete a task (`PATCH` `status` = `completed`), through
  the shared `IWritableTaskSource.CompleteAsync` in [providers.md](providers.md#write-support-later).

## Testing

- **Mapper tests** from fixture JSON: all statuses, date-only versus timed due dates in
  several zones including a Windows zone name, importance values, HTML and text bodies,
  every recurrence pattern and range, checklist items, a task with every field absent,
  and an unknown enum value.
- **Sync tests** with a fake `HttpMessageHandler`: first sync across several pages, empty
  delta, delta with updates and `@removed`, `410` gives `SyncStateExpiredException`, a
  delta link pointing at another host is refused, a response with neither link is a
  `SourceDataException`, the 24-hour full snapshot, a removed checklist item.
- **Error tests**: `401`, `403`, `404` mailbox-missing, `429` with `Retry-After`, `503`.
- **Secret tests**: tokens never appear in exception messages or diagnostics; the bearer token goes only to
  `graph.microsoft.com`.
- No test touches the network or a real identity provider; sign-in is tested against a loopback listener in
  the Outlook test suite.
- **Throttling**: a short `Retry-After` is waited out in the client, a long one reaches the engine as
  `SourceUnavailableException.RetryAfter`.
- **Not yet done, needed before calling this finished:** a manual run against a throwaway personal account.
  Check the due-date round trip with the To Do app (date-only, time zones around midnight), checklist-only
  edits appearing in the next delta (open question 2), recurring task completion, the default and a shared
  list, and that `$expand=checklistItems` is accepted by `tasks/delta`. Only fixtures have been used so far.

## Open questions

**Decided 2026-10-26 (maintainer):** the proposals below are accepted, except question 1, which was overtaken the same day: work accounts are dropped for now and the client id is configuration (see the Outlook doc). Questions 2 to 4 are verified against real accounts; that has not happened yet.

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Who owns the Entra app registration, and do we pursue publisher verification? | Superseded: personal accounts only, so no verification is needed; the client id is entered per source or embedded at build time. Revisit with work accounts. |
| 2 | Does a checklist-only edit appear in task delta? | Assume not until tested; the 24-hour full snapshot covers it. Remove it if testing shows delta is reliable. |
| 3 | Is the "midnight means date-only" rule right for every To Do client? | Verify against real accounts; adjust with the `isReminderOn` and zone data if not. |
| 4 | Are the To Do web deep links stable? | Best-effort; fall back to the web app root. |
| 5 | Should body HTML be kept as HTML for a future rich details pane? | Plain text for v1; raw body is kept so nothing is lost. |
| 6 | Show Flagged email as a list, hidden by default, or not at all? | Show it; the user can hide the collection like any other. |
| 7 | Use the Microsoft Graph SDK or a thin `HttpClient`? | Decided in the Outlook doc; this provider needs only about four endpoints, so a thin client is enough. |

## Checklist (from [providers.md](providers.md#provider-checklist))

- [x] Implements `ISource` and `ITaskSource`
- [x] Maps to the common model with tests from fixtures (time zones, date-only)
- [x] Handles `previous == null`, a valid token, and an expired token
- [x] Throws only `SourceException` subtypes across its public surface
- [x] Keeps secrets out of config, logs and exception text (tested)
- [x] Per-item errors become warnings, not sync failures
- [x] Unsupported fields documented (this doc)
- [ ] Verified against a live account
