# Todoist provider (read-only)

The first task-only provider. Project: `AllMyTasks.Providers.Todoist`, source kind
`todoist`. It implements `ITaskSource` and nothing else, which is the point: it proves the
model and sync engine work for a provider that has no calendar at all. Tracking issue:
[#27](https://github.com/jkruza/allmytasks/issues/27).

Read-only in v1, like the rest of the app: no create, edit or complete, and every
collection has `IsReadOnly = true`.

## Scope

In v1:

- Add an account by pasting a Todoist **API token**.
- Every non-archived, non-deleted project (including Inbox and shared projects) becomes a
  `TaskList` collection.
- Active tasks with title, description, due date or date-time, priority, labels, sub-task
  parent, a link to the task, and last-changed time.
- Tasks completed since the last sync appear as `Completed` until the next full resync.
- Incremental refresh with the Sync API's `sync_token`.

Not in v1 (documented so absence is a decision, not a bug):

- Writing back.
- OAuth sign-in (see [Authentication](#authentication)).
- Sections: tasks are shown in their project; the section name is not shown.
- Comments, attachments, reminders, filters, assignees, deadlines and durations.
- Converting Todoist's recurrence text to an RRULE (see [Recurrence](#recurrence)).
- Completed-task history older than the current cache.

## Authentication

Todoist offers a personal API token (Settings, Integrations, Developer) and OAuth 2. v1
uses the token.

| | Personal token | OAuth |
| --- | --- | --- |
| Setup for the user | Copy one string | Browser hand-off |
| Needs an app registration | No | Yes |
| Needs a client secret | No | **Yes**: the token exchange takes `client_secret` and, as far as the docs say, there is no PKCE |
| Fits a public desktop app | Yes | Poorly: a secret shipped in an open-source binary is not secret |

So OAuth waits for the shared OAuth design (`IAuthFlow`, see
[providers.md](providers.md#configuration-and-credentials)) and a decision on whether
shipping a public client id/secret pair is acceptable. The token field is a normal secret
field, so switching to OAuth later changes only the factory and how the secret is
obtained, not the source.

Config schema (`TodoistFactory`):

| Field | Kind | Secret | Notes |
| --- | --- | --- | --- |
| `token` | Password | yes | Help text says where to find it and that it grants full account access |
| refresh interval | Number | no | Shared `SyncScheduler.IntervalKey`; blank means 15 minutes |

The token is only ever sent as `Authorization: Bearer ...` to `https://api.todoist.com`.
The host is fixed in code, not configurable, so a typo or a malicious config cannot send
the token elsewhere. A 401/403 becomes `SourceAuthException` ("needs attention").

## Transport

Plain `HttpClient` with `System.Text.Json` source generation, no third-party SDK: the
surface used is two request shapes and a handful of DTOs, and it avoids adding a
dependency to the locked package set. One `SourceBase` subclass owns the client;
`HttpMessageHandler` is injectable for tests.

Base URL `https://api.todoist.com/api/v1`. Todoist's API v1 replaced the older REST v2
and Sync v9; the provider targets v1 only. Requests have a 30 s timeout and a response
size cap, and redirects to another host are not followed (the token must not travel
with them).

## Sync strategy

Todoist syncs per **account**, not per project, while the engine syncs per **collection**
with its own `SyncState`. The provider bridges the two.

```
POST /sync   sync_token=<token or *>   resource_types=["projects","items"]
-> { sync_token, full_sync, projects[], items[] }   (incremental: only what changed)
```

- `GetCollectionsAsync` runs the same call with the stored account token (or `*`) and
  returns the project list. Projects with `is_deleted` or `is_archived` are dropped.
- `SyncTasksAsync(collection, previous)`:
  - `previous == null`: full sync (`sync_token=*`), return the collection's tasks as a
    **snapshot** (`IsFullSnapshot = true`).
  - otherwise the state token is the Todoist `sync_token` this collection was last synced
    from; the provider asks for changes since it and returns a **delta** filtered to the
    collection.
- **One request per distinct token.** A source keeps a small in-memory memo of
  `request token -> response`. In a refresh round every collection that last synced from
  the same token reuses one response, so a 30-project account costs one request, not 30.
  The memo lives only for the process and is bounded (a few entries, short expiry).
- A token Todoist rejects as invalid, or a `full_sync: true` reply to an incremental
  request, throws `SyncStateExpiredException`; the engine retries once with `null`.

### Items that move between projects

An item that moves from project A to B shows up in the delta with its new `project_id`
only. B gets it as an upsert. For A, the delta for each collection includes, as
`DeletedItemIds`, every item id in the response that does **not** belong to it (plus
items flagged `is_deleted`). Deleting an id the cache never held is a no-op, and the list
is bounded by the delta size. This needs no knowledge of the store, which the provider
must not have.

### Completed tasks

Active tasks come back from a full sync; a task completed since the last sync comes back
once in the incremental response with `checked: true` and maps to
`TaskItemStatus.Completed` with `CompletedAt`. After a full resync, completed tasks are
gone, because a snapshot removes anything not returned. Showing recently completed tasks
reliably is an [open question](#open-questions).

### Rate limits and errors

| Condition | Exception | Engine behaviour |
| --- | --- | --- |
| 401, 403 | `SourceAuthException` | Needs attention |
| 429 | `SourceUnavailableException`, honours `Retry-After` | Backoff, keep cache |
| 5xx, timeout, DNS, TLS | `SourceUnavailableException` | Backoff, keep cache |
| Invalid sync token | `SyncStateExpiredException` | Re-sync once |
| Malformed JSON for the whole response | `SourceDataException` | Fail the sync |
| One item that cannot be mapped | warning on the result | Skip the item |

Messages name only the exception type or status code, never the URL, headers or body.

## Mapping to the model

| Todoist | Model |
| --- | --- |
| project `id` | `CollectionInfo.Id` (`DisplayName` = project name, `Kind` = `TaskList`) |
| project `color` (a name such as `berry_red`) | `CollectionInfo.Color` via a lookup table of Todoist's palette to hex; unknown name gives `null` |
| item `id` | `ItemKey.ItemId` (string; Todoist ids are opaque strings) |
| `content` | `Title` |
| `description` | `Notes` (empty becomes `null`) |
| `due.date` only a date | `DueDate` |
| `due.date` with a time and `Z` | `DueDateTime` (UTC), `TimeZoneId` from `due.timezone` when present |
| `due.date` with a time and no zone ("floating") | `DueDateTime` resolved in the **display zone** at sync time; see below |
| `priority` 4 / 3 / 2 / 1 | `High` / `Medium` / `Low` / `None` |
| `labels` (names) | `Labels` |
| `parent_id` | `ParentKey` (same collection) |
| `checked`, `completed_at` | `Status` = `Completed`, `CompletedAt` |
| `updated_at` | `LastModified` |
| task link `https://app.todoist.com/app/task/<id>` | `Url` |
| `due.is_recurring`, `due.string` | see [Recurrence](#recurrence) |
| `deadline`, `duration`, `responsible_uid`, `section_id`, `day_order` | raw payload only |

Todoist's API priority is inverted from what the UI shows: API `4` is the red "P1" in the
app. The mapping above is by API value, and a test pins it.

Floating times ("every day at 9:00" for a user who travels) have no zone. The provider
keeps the wall-clock time and resolves it with the zone the source is configured to use
(the OS zone by default). Resolving at sync time means a laptop that changes zone shows
the new offset after the next refresh, which matches how Todoist itself behaves.

Mapping quirks to handle and test:

- Items whose `project_id` is not in the project list (archived or inaccessible) are
  skipped with a warning.
- `parent_id` pointing at a parent in another collection or not present: `ParentKey` is
  set anyway; the details pane already handles a missing parent.
- A `parent_id` cycle cannot loop the details pane (existing rule), but the mapper does
  not trust it either.
- Empty `content` is kept as `""`; the view shows its placeholder.
- Due strings with a fractional-second suffix, with and without `Z`.
- Extremely long descriptions are stored intact; the details pane caps them.

## Recurrence

Todoist stores recurrence as natural language (`due.string`, for example `every other
Monday`), not RRULE, and a recurring task carries only its **next** occurrence as `due`;
completing it moves `due` forward. Converting free text to RFC 5545 is lossy and locale
dependent, and a wrong rule would put phantom occurrences on the calendar views.

v1 therefore sets `Recurrence = null`. The task appears once, at its next due date, which
is what Todoist's own Today and Upcoming show for the near term. `due.string` stays in the
raw payload so it is not lost. Showing "Repeats: every other Monday" in the details pane
needs a small additive model field, which is an [open question](#open-questions).

## Libraries

None beyond the BCL: `HttpClient`, `System.Text.Json`. Core stays free of Todoist types;
the provider references Core and nothing else.

## Testing

- **Mapping tests** from fixture JSON: date-only, UTC time, zoned time, floating time,
  all four priorities, labels, sub-task, completed, empty content/description, unknown
  project colour.
- **Sync tests** with a fake `HttpMessageHandler` serving recorded responses: first sync
  snapshot, empty delta, upsert, delete, item moved between projects (old collection gets
  the delete), `full_sync: true` on an incremental request, 429 with `Retry-After`, 401.
- **Memo test**: two collections with the same token cause exactly one HTTP request.
- **Security tests**: the token never appears in `ToString()`, exception messages, logs or
  stored config; the token is not sent to a different host after a redirect.
- All fixtures use made-up data. Never commit a real Todoist export or token.
- Optional integration test against a throwaway account, gated by `AMT_TODOIST_TOKEN`, off
  by default and never run in CI (same approach as the CalDAV one).

## Security

- Token in the secret store only; never in `SourceConfig`, logs, or exception text.
- A Todoist token has full read/write access to the account. The setup form says so, and
  the provider only ever calls read endpoints. Users who want to limit risk can revoke the
  token in Todoist at any time.
- Fixed host, TLS required, no cross-host redirects.
- Responses are untrusted input: size cap, tolerant JSON parsing (unknown fields ignored,
  missing optional fields allowed), and item-level failures become warnings.

## Shared concerns with other providers

Written so the Google and Microsoft designs can lean on the same decisions:

- **Account-level token, collection-level state.** If another provider also syncs per
  account (Microsoft Graph delta is per list, Google `syncToken` is per list), they need
  nothing like the memo; Todoist is the odd one out. The memo lives in this provider, not
  in Core.
- **Moved items** are handled by listing foreign ids as deletes; any provider whose delta
  cannot say where an item came from can use the same trick.
- **OAuth** is deferred here but is the main thing the Google and Microsoft providers
  need from Core: `IAuthFlow`, refresh-token storage under `(sourceId, "refresh_token")`
  in `ISecretStore`, and a loopback redirect listener. Todoist can adopt it later if the
  client-secret question is settled.
- **Recurrence text** that cannot be mapped to an RRULE needs one shared answer in the
  model; Microsoft To Do recurrence maps cleanly, Todoist does not.

## Open questions

**Decided 2026-10-26 (maintainer):** v1 stays with personal API tokens only; OAuth is not pursued for now. Until the maintainer says otherwise, the other items default to out of v1: no recently completed tasks, no `RecurrenceText`, no section names, and project favourite/order does not affect sorting. Each can be reopened with its own issue.

- Show recently completed tasks (for example "done today")? Needs either keeping
  completed items across full resyncs or calling Todoist's completed-tasks endpoint.
- Add `TaskItem.RecurrenceText` (display-only) so unmapped recurrence can still be shown?
- Show section names (a prefix or a group in All tasks)?
- Should a project's Todoist favourite flag or order influence sort order?
- OAuth: acceptable to ship a public client id/secret, or stay with tokens only?
- Confirm at implementation time against the live API: exact rate limits, the shape of
  incremental `checked` items, and the `Retry-After` header on 429. This design was written
  from the public Sync API v1 documentation without a live account.
