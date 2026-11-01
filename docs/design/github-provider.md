# GitHub provider

Project `AllMyTasks.Providers.GitHub`, source kind `github`. Implements `ITaskSource` only. Tracking issue:
[#72](https://github.com/jkruza/allmytasks/issues/72). **Status: designed, not implemented.** The API details come
from GitHub's public documentation and have not been tried against a live account (see [Unverified](#unverified)).

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

Whether `/issues?filter=assigned` returns what is expected for fine-grained tokens, search-API paging limits, and
the ETag behaviour of search results.
