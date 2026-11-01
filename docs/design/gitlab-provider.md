# GitLab provider

Project `AllMyTasks.Providers.GitLab`, source kind `gitlab`. Implements `ITaskSource` only. Tracking issue:
[#73](https://github.com/jkruza/allmytasks/issues/73). **Status: implemented, read-only.** API details come from
GitLab's public documentation and have not been tried against a live instance (see [Unverified](#unverified)).

## Scope

In version 1:

- **gitlab.com and self-managed** instances: the source has a base URL, default `https://gitlab.com`.
- Open **issues assigned to me**, and open **merge requests assigned to me or awaiting my review**.
- Each project with at least one such item becomes a `TaskList` collection named by its path with namespace.
- Read-only.

Not in version 1: To-Do list items other than review requests (those are covered by the reviewer query), epics, pipelines, boards, writing back, OAuth.

## Mapping

| GitLab | Model |
| --- | --- |
| Issue / MR title with `#iid` or `!iid` | `Title` |
| Issue `due_date` | `TaskDue` date-only; none when empty (lands in the [Inbox](inbox-view.md)) |
| Merge request | No due date in GitLab; milestone `due_date` when set, else none |
| Labels | `Labels` |
| `state` opened / closed / merged | Open / Completed |
| Draft MR | Label `draft` |
| Reviewer = me | Label `review requested` |
| `web_url` | `Url` |
| `updated_at` | `LastModified` |
| Priority | None (labels such as `priority::1` are not interpreted in version 1) |

## Authentication

A **personal access token** with the `read_api` scope, pasted by the user and kept in the OS secret store.
Self-managed instances accept the same kind of token. The base URL must be https (plain http only for localhost,
as for CalDAV). OAuth is deferred for the same reason as other providers.

## Sync

- Issues: `GET /issues?scope=assigned_to_me&state=opened`, paged by `page`/`per_page` with `x-next-page`.
- Merge requests: `GET /merge_requests?scope=assigned_to_me&state=opened` and
  `GET /merge_requests?reviewer_username=<me>&state=opened` (the username comes from `GET /user`).
- Later syncs add `updated_after=<last sync minus overlap>`; items that left the open lists are reconciled by
  re-fetching cached open ids not seen, as for GitHub. *(Implemented differently, see
  [Implementation notes](#implementation-notes).)*
- 429 responses map to the standard backoff, using `Retry-After`.

## Testing and security

Fixtures for issue with and without due date, MR, draft MR and a self-managed base URL with a path prefix; fake
HTTP handler for paging and 429. Token never in logs or database; instance text is shown as plain text.

## Decisions

Decided by Jarek, 2026-11-01.

- **Review requests are part of version 1.** Merge requests where I am a requested reviewer are shown next to
  assigned ones (the `reviewer_username` query above), labelled `review requested`. This is a requirement, not an
  option, so how reviewer requests behave across GitLab versions is the first item to check
  (see [Unverified](#unverified)).
- The rest of GitLab's To-Do list (mentions, build failures, approvals) is not in version 1.
- There is no group-level scope in version 1.

## Future refinements

- Optional display of the other To-Do list items (mentions and similar), as a per-source option. If review requests
  turn out to be incomplete from `reviewer_username` alone, read To-Do items of the review-requested kind as well
  and de-duplicate by merge request.
- Limit a source to given groups, only if volume on large instances demands it.
- OAuth, together with the other OAuth providers.


## Unverified

Exact behaviour of `scope=assigned_to_me` with reviewer requests across GitLab versions, the minimum supported
GitLab version, and `updated_after` semantics for items that change assignee.

## Implementation notes

Differences from the design above, and the choices made while building it. Nothing here was run against a live
GitLab instance; the tests use a fake HTTP handler.

- **Setup form:** the GitLab address (blank means `https://gitlab.com`; https only, plain http for localhost; a path
  prefix such as `https://example.com/gitlab` is kept; no credentials, query or fragment) and the personal access token
  (`read_api` scope), stored in the secret store. The token goes only in the `PRIVATE-TOKEN` header of requests to that
  address. Redirects are never followed, so the token cannot be sent elsewhere.
- **Full snapshots, not `updated_after`:** the three lists are small (open, assigned to me), so every sync reads them
  whole and each project's sync is a snapshot of its items. An item that was closed, merged, unassigned or
  reassigned is simply not in the lists any more, so no reconciliation by id is needed and the `updated_after`
  semantics question under [Unverified](#unverified) does not arise. One fetch (user, issues, assigned merge
  requests, review-requested merge requests) is shared by the collections that sync within two minutes of it. If
  volume on large instances ever demands it, `updated_after` can be added later.
- **Review requests:** `GET /merge_requests?scope=all&state=opened&reviewer_username=<me>`. `scope=all` is needed
  because the global merge request list otherwise defaults to the ones I created. A merge request that is both
  assigned to me and awaiting my review is shown once, labelled `review requested`.
- **Identity:** the user name comes from `GET /user`. Items are keyed by the instance-wide `id` (`issue-<id>`,
  `mr-<id>`), since the per-project `iid` is only unique within a project; the title carries the `iid`
  (`Title (#12)`, `Title (!5)`). The project path comes from `references.full`.
- **Closed and merged** map to Completed as designed, but only open items are fetched, so they appear only for a
  moment if at all.
- **Paging:** `per_page=100`, following `x-next-page` until it is empty, at most 100 pages per list. Each response is
  capped at 16 MB and each request times out after 30 seconds.
- **Errors:** 401 and 403 are a rejected token (the message mentions the `read_api` scope); 429 is "unavailable" with
  `Retry-After` handed to the engine; 5xx and timeouts are unavailable; 404 and redirects point at the address.
  Messages never contain the token, the address or the response body.
- **Not interpreted:** priority labels, epics, group scope, the To-Do list (other than review requests through the
  reviewer query).
