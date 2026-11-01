# GitLab provider

Project `AllMyTasks.Providers.GitLab`, source kind `gitlab`. Implements `ITaskSource` only. Tracking issue:
[#73](https://github.com/jkruza/allmytasks/issues/73). **Status: designed, not implemented.** API details come from
GitLab's public documentation and have not been tried against a live instance (see [Unverified](#unverified)).

## Scope

In version 1:

- **gitlab.com and self-managed** instances: the source has a base URL, default `https://gitlab.com`.
- Open **issues assigned to me**, and open **merge requests assigned to me or awaiting my review**.
- Each project with at least one such item becomes a `TaskList` collection named by its path with namespace.
- Read-only.

Not in version 1: To-Do list items, epics, pipelines, boards, writing back, OAuth.

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
  re-fetching cached open ids not seen, as for GitHub.
- 429 responses map to the standard backoff, using `Retry-After`.

## Testing and security

Fixtures for issue with and without due date, MR, draft MR and a self-managed base URL with a path prefix; fake
HTTP handler for paging and 429. Token never in logs or database; instance text is shown as plain text.

## Open questions

- Show To-Do list items (GitLab's own inbox of mentions and review requests)? Proposal: later, as an option.
- Group-level scope for large instances (limit to given groups)? Proposal: only if volume demands it.

## Unverified

Exact behaviour of `scope=assigned_to_me` with reviewer requests across GitLab versions, the minimum supported
GitLab version, and `updated_after` semantics for items that change assignee.
