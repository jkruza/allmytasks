# Jira provider

Project `AllMyTasks.Providers.Jira`, source kind `jira`. Implements `ITaskSource` only. Tracking issue:
[#71](https://github.com/jkruza/allmytasks/issues/71). **Status: implemented, read-only, not yet tried on a live site.** Everything about
Jira's API below comes from its public documentation; the [Unverified](#unverified) list says what to check first.
What the code does where it differs from the first design is under [Implementation notes](#implementation-notes).

## Scope

In version 1:

- **Jira Cloud** sites (`https://<site>.atlassian.net`).
- Open issues assigned to the signed-in user: JQL `assignee = currentUser() AND statusCategory != Done`.
- Each issue becomes a task: key and summary as title, `duedate` as a date-only due date, priority, labels, status
  category, parent (for subtasks), link to the issue, last updated.
- Each Jira project that has at least one such issue becomes a `TaskList` collection, named by project key and name.
- Read-only.

Not in version 1: issues the user only reports or watches (an optional extra JQL per source is a possible later refinement),
sprints and boards, comments, worklogs, custom fields, Jira Server / Data Center, OAuth sign-in, writing back.

## Authentication

| | API token | OAuth 2.0 (3LO) |
| --- | --- | --- |
| Setup | Create a token in the Atlassian account settings, paste site, email and token | Register an app in the Atlassian developer console, browser hand-off |
| Secret kept | The token | A refresh token |
| Needs a project-owned app | No | Yes, with a client secret |

Version 1 uses the **API token** with HTTP Basic (email and token), the same low-ceremony route as Todoist. It
grants the user's own access, so it can read more than this app needs; the setup text says so. The token goes to
the OS secret store; site and email are ordinary source settings. OAuth is left for later because Atlassian's
3LO flow needs a registered app and client secret, which is the same project-level decision as the other OAuth
providers ([oauth-client-configuration.md](oauth-client-configuration.md)).

## Sync

- Search with `GET /rest/api/3/search/jql` (the token-paginated search endpoint), with `fields` limited to what is mapped, so a
  large site stays cheap. Page until the reply says it is the last page.
- First sync: the full JQL above. Later syncs: add `AND updated >= "<last sync minus a small overlap>"` and merge by
  issue id. Issues that leave the result set (reassigned, resolved) are found by a second query for the ids the cache has
  as open, `key in (...)`, in chunks; whatever comes back as Done, or not at all, is closed or removed. Jira has no
  change feed, so this reconciliation is the part to test hardest.
- Rate limits: honour `Retry-After` on 429 and map it to the standard `SourceException` backoff.
- Descriptions are Atlassian Document Format (JSON). Version 1 flattens it to plain text and never renders markup.

## Mapping

| Jira | Model |
| --- | --- |
| `key` + `fields.summary` | `Title` as `PROJ-123 Summary` |
| `fields.duedate` | `TaskDue` date-only; none when empty (the issue lands in the [Inbox](inbox-view.md)) |
| `fields.priority.name` | Highest/High to High, Medium to Medium, Low/Lowest to Low, else None; raw kept |
| `fields.status.statusCategory.key` | `new` to Open, `indeterminate` to InProgress, `done` to Completed |
| `fields.labels` | `Labels` |
| `fields.parent` | `ParentKey` when the parent is also in the cache |
| `fields.project` | Collection |
| `fields.description` | `Notes`, flattened |
| browse URL | `Url` |
| `fields.updated` | `LastModified` |

Jira has due dates but no time of day, so `duedate` is always a date-only due.

## Testing and security

Fixture JSON from the documentation for mapping and paging; a fake HTTP handler for the closed-issue reconciliation,
429 handling and a bad token. The token must never appear in logs, exception text or the database
(tested, as for every provider). The site URL is checked to be https.

## Decisions

Decided by Jarek, 2026-11-01. The proposals of the earlier open questions are accepted:

- Version 1 has no extra JQL. The built-in query is the only one.
- Jira Server / Data Center is not in version 1.
- Sprints and boards are not shown; the app is not a board.

## Implementation notes

- **Setup.** A source has three settings: the site address (https only; anything after the host is dropped), the Atlassian
  account email, and the API token. The email and site are ordinary source settings; the token is in the secret store. The
  credentials are sent as HTTP Basic to the configured host only, redirects are not followed, replies are capped at 16 MB
  and the request times out after 30 seconds. Messages and diagnostics never contain the token, email or address.
- **Every sync is a snapshot.** The design planned an `updated >=` delta plus a `key in (...)` check. The provider does
  not see the cache, and one person's open assigned issues are few, so each sync instead runs the one JQL query,
  pages through all results, and returns the complete list per project. An issue that is resolved or reassigned stops
  appearing and the engine drops it. The sync position is only the time of the snapshot. If a site turns out to have
  thousands of assigned issues, the delta design above is the way to bring the cost down.
- **One request per refresh.** The engine syncs per project; the reply is remembered for two minutes so the projects share
  one search. A failed request is not remembered.
- **Collections** are keyed by the Jira project id and named `KEY Name`. A project with no open assigned issue is not listed.
- **Descriptions** are flattened from Atlassian Document Format to text (paragraphs and list items on their own lines;
  mentions, emoji and links as text).
- **Parents** are linked only when the parent is also among the fetched issues.
- **Page limit.** At most 200 pages of 100 issues are read; more is reported as an error.
- Tests use a fake HTTP handler with fixture JSON for mapping, paging, errors and secret handling.

## Future refinements

- An optional extra JQL field per source (for example "also issues I report", or one project), off by default.
- Jira Server / Data Center with personal access tokens, as a later change to the same provider (API v2 paths).
- OAuth 2.0 (3LO) sign-in, once the project-level app decision is made.


## Unverified

Exact paging fields of the search endpoint (`nextPageToken` and `isLast` are what the code reads), `Retry-After` behaviour, whether `statusCategory` can be used in JQL on
every site, and the API token header format all need a check against a real Cloud site before this is called done.
