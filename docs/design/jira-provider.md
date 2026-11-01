# Jira provider

Project `AllMyTasks.Providers.Jira`, source kind `jira`. Implements `ITaskSource` only. Tracking issue:
[#71](https://github.com/jkruza/allmytasks/issues/71). **Status: designed, not implemented.** Everything about
Jira's API below comes from its public documentation and has not been tried against a live site; the
[Unverified](#unverified) list says what to check first.

## Scope

In version 1:

- **Jira Cloud** sites (`https://<site>.atlassian.net`).
- Open issues assigned to the signed-in user: JQL `assignee = currentUser() AND statusCategory != Done`.
- Each issue becomes a task: key and summary as title, `duedate` as a date-only due date, priority, labels, status
  category, parent (for subtasks), link to the issue, last updated.
- Each Jira project that has at least one such issue becomes a `TaskList` collection, named by project key and name.
- Read-only.

Not in version 1: issues the user only reports or watches (an optional extra JQL per source is an open question),
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

## Open questions

- Extra JQL per source ("also issues I report", "a given project")? Proposal: one optional JQL field, off by default.
- Jira Server / Data Center with personal access tokens? Proposal: a later change to the same provider (API v2 paths).
- Show the Jira sprint or board? Proposal: no, the app is not a board.

## Unverified

Exact paging fields of the search endpoint, `Retry-After` behaviour, whether `statusCategory` can be used in JQL on
every site, and the API token header format all need a check against a real Cloud site before this is called done.
