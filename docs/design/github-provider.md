# GitHub provider

Project `AllMyTasks.Providers.GitHub`, source kind `github`. Implements `ITaskSource` only. Tracking issue:
[#72](https://github.com/jkruza/allmytasks/issues/72). **Status: implemented, read-only.** The API details come
from GitHub's public documentation and have not been tried against a live account (see [Unverified](#unverified)).
Where the code differs from the design, [Implementation notes](#implementation-notes) says so.

## Scope

In version 1:

- **github.com**.
- Two kinds of work item, both shown as tasks:
  - open **issues and pull requests assigned to me**;
  - open **pull requests where my review is requested**.
- Each repository that has at least one such item becomes a `TaskList` collection named `owner/repo`.
- Read-only.

Not in version 1: notifications, Actions runs, Discussions, Projects (v2) boards, issues I only created or follow,
GitHub Enterprise Server, writing back.

## Why issues become tasks

GitHub has no due date on an issue. The mapping is deliberate and simple:

| GitHub | Model |
| --- | --- |
| Issue or PR title with `#number` | `Title`, e.g. `Fix crash on start (#42)` |
| Milestone `due_on` | `TaskDue` date-only; none when there is no milestone due date (it lands in the [Inbox](inbox-view.md)) |
| Labels | `Labels` (names) |
| `state` open / closed (merged shown as Completed) | Open / Completed |
| Review requested | Label `review requested` added by the provider; the item is the PR |
| Draft PR | Label `draft` |
| `html_url` | `Url` |
| `updated_at` | `LastModified` |
| Priority | None (GitHub has none; labels such as `P1` are not interpreted) |

Most GitHub work therefore shows up undated, which is why the [Inbox](inbox-view.md) matters for this provider.
Reading Projects (v2) date fields would give real due dates and is a possible later addition.

## Authentication

A **personal access token**, pasted by the user, kept in the OS secret store. A fine-grained token with read
access to issues, pull requests and metadata on the repositories of interest is the recommended kind; a classic
token with `repo` (private) or no scope (public only) also works. Device-flow OAuth would avoid pasting a token
but needs a registered OAuth app, so it is deferred with the other OAuth providers.

## Sync

- Assigned items: `GET /issues?filter=assigned&state=open` (issues and PRs the token can see), paged.
- Review requests: the search API with `is:pr is:open review-requested:@me`.
- Use `If-None-Match` with each query's ETag; a 304 does not count against the primary rate limit, so frequent refresh is cheap.
- Items that disappear from the open lists (closed, unassigned, review done) are reconciled by re-fetching the cached
  ids that were open and not seen this time, in small batches.
- Primary and secondary rate limit responses map to the standard backoff; the `Retry-After` and `X-RateLimit-Reset`
  headers set the next attempt.

## Testing and security

Fixtures for issue, PR, draft PR, merged PR and milestone; fake HTTP handler for ETag/304, paging and rate-limit
responses. The token never reaches logs or the database. Titles and bodies from repositories are untrusted text:
displayed as plain text only.

## Decisions

Decided by Jarek, 2026-11-01. The proposals of the earlier open questions are accepted:

- Version 1 shows items assigned to me and review requests only; issues I created or am mentioned in are not included.
- GitHub Enterprise Server is not in version 1.
- Items are grouped by repository only.

## Future refinements

- Per-source options to include issues I created and items that mention me.
- GitHub Enterprise Server as a base URL setting on the same provider, like GitLab.
- Grouping by organisation, if repository-only grouping proves too fine.
- Device-flow OAuth, together with the other OAuth providers.
- Real due dates from Projects (v2) date fields.


## Unverified

Whether `/issues?filter=assigned` returns what is expected for fine-grained tokens (it should include issues and pull
requests in repositories the token can read; organisation repositories that need token approval are *assumed* to
show up once approved), search-API paging limits, and the ETag behaviour of search results (*assumed* to behave like
other REST endpoints). Also *assumed*: that `review-requested:@me` resolves `@me` for a fine-grained token, that the
search result carries `draft` and `labels`, and that a rate-limit 403 carries `Retry-After` or
`X-RateLimit-Remaining: 0`. Sub-teams: `review-requested:@me` covers requests to the user, not to a team the user is in.

## Implementation notes

What was built, and where it differs from the design above. No live GitHub account or token was available, so the
points under [Unverified](#unverified) and marked *assumed* are untested against the real service.

- **Sources and fields.** Kind `github`, one field: the personal access token (a password field, kept in the secret
  store only), plus the usual refresh interval. There is no setting for a host: github.com is fixed in code, the token
  is sent only to `api.github.com` as a bearer token, redirects are never followed, bodies are capped at 16 MB and a
  request times out after 30 seconds. Error messages and diagnostics never contain the token, the address or the body.
- **Requests.** `GET /issues?filter=assigned&state=open&per_page=100&page=N` and
  `GET /search/issues?q=is:pr is:open review-requested:@me archived:false&per_page=100&page=N`, with the
  `application/vnd.github+json` accept header and `X-GitHub-Api-Version: 2022-11-28`. Paging continues while a page is
  full: up to 30 pages for assigned items and the search API's limit of 10 pages (1000 results) for review requests.
- **One fetch per refresh.** The engine syncs per collection, but both queries cover every repository, so the source
  fetches once and every repository is served from that result (kept for two minutes; a failed fetch is not kept).
  The repository list (`GetCollectionsAsync`) comes from the same result, so a repository with no open item for the
  user has no list.
- **Snapshots instead of reconciling.** Every sync is a full snapshot of the repository's open items. Items that were
  closed, unassigned or reviewed are missing from the snapshot and the engine removes them, so the design's "re-fetch
  the cached ids that were not seen" step is not needed. The consequence is that a closed item **disappears** from the
  app instead of being shown as Completed; the mapper still maps a closed or merged item to Completed if one is ever
  returned. Showing recently closed items is a possible later addition.
- **ETags.** Each page is remembered with its ETag and asked for again with `If-None-Match`; a 304 reuses the remembered
  page and does not count against the rate limit. The ETags live in memory for the life of the app, one per page
  address, at most 200 pages.
- **Rate limits.** HTTP 429, or 403 with `Retry-After` or `X-RateLimit-Remaining: 0`, is "unavailable" with the wait
  taken from `Retry-After`, else from `X-RateLimit-Reset`; the engine backs off until then. Any other 401 or 403 is
  "token rejected" and stops retrying until the user edits the source. 5xx and network failures are "unavailable".
  A search answer with `incomplete_results: true` is treated as a failure so a partial list never wipes the cache.
- **Mapping.** Id: the numeric `id` of the issue or pull request (unique across repositories). Title:
  `title (#number)`. Notes: the body as plain text, cut at 20 000 characters. Due: the date part of the milestone's
  `due_on`, as written, as a date-only due. Labels: the label names, then `review requested` (when the item came from
  the review search, also when it is assigned too) and `draft`. Link: `html_url`, kept only when it is an `https`
  address on `github.com`. Repository: the `repository.full_name`, else the last two segments of `repository_url`,
  and only a name made of letters, digits, `_`, `.` and `-` on both sides of one `/`; an item without a usable
  repository is skipped with a warning (the warning has the id, not the content).
- **Out of version 1, as designed:** notifications, Actions, Discussions, Projects (v2), issues I only created or
  follow, GitHub Enterprise Server, device-flow OAuth and writing back.

Tests (`tests/AllMyTasks.Providers.GitHub.Tests`) use a fake HTTP handler and cover collections, the shared fetch,
the review label and de-duplication, labels and draft, milestone dates, closed and merged items, skipped items, link
safety, request headers and queries, paging, ETag and 304, incomplete search, auth, rate-limit, server and redirect
errors, unreadable and oversized replies, token handling and the factory. An integration test against a real token
(gated on an environment variable, like the other providers plan) is not written.

