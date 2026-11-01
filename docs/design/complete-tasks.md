# Complete tasks (Todoist)

Tracked in [#54](https://github.com/jkruza/allmytasks/issues/54).

**Scope note (2026-11-01):** the product is now a read-only overview and write support is a version 2 goal ([product-scope.md](product-scope.md), [write-support.md](write-support.md)). This feature was built first and stays as an opt-in, off-by-default **preview of version 2**; it will not be extended to other providers before version 2.

**Status: implemented** for Todoist. Where the implementation differs from the design below, "Implementation notes" at the end says so. Not yet verified against a live Todoist account (see [Unverified](#unverified)).

The first write support: tick a Todoist task in the app to complete it, untick it to reopen it. This is
the "complete tasks first" step from the [roadmap](roadmap.md), limited to Todoist so the write path is
proven on one provider before Google Tasks and Microsoft To Do get it.

## Goals

- A checkbox on every row of **All tasks** and in the task details pane completes or reopens a task.
- The change shows immediately, works offline, and survives a restart.
- Nothing is lost silently: a change that cannot be delivered is shown as failed, not dropped.
- Users who want a read-only app can keep it read-only.

## Non-goals

- Create, edit, move or delete tasks; calendar events.
- Google Tasks and Microsoft To Do (follow-ups once this has settled).
- Completing from a notification or the tray (possible with [reminders](reminders.md) later).
- Showing recently completed tasks; that remains the open question in [todoist-provider.md](todoist-provider.md).

## Provider interface

[providers.md](providers.md#write-support-later) already reserves separate write interfaces. This adds the
task one, with only what is needed now:

```csharp
public interface IWritableTaskSource : ITaskSource
{
    Task<WriteResult> CompleteAsync(ItemKey key, Guid changeId, CancellationToken ct);
    Task<WriteResult> ReopenAsync(ItemKey key, Guid changeId, CancellationToken ct);
}

public enum WriteOutcome { Applied, AlreadyInState, NotFound }   // failures throw SourceException
public sealed record WriteResult(WriteOutcome Outcome);
```

`Applied` and `AlreadyInState` both mean the user's intent is met. `NotFound` (deleted remotely) removes the
pending change and shows a notice; the next sync removes the task. Other failures use the existing
`SourceException` hierarchy (auth, rate limit, transient, permanent).

A read-only source does not implement the interface, and the UI shows no checkbox for tasks in a collection
where `Collection.IsReadOnly` is true or whose source does not implement it. Until then the glyph stays display only.

## Todoist mapping

Todoist's Sync API takes commands in `POST /api/v1/sync`, each with a client-generated `uuid`; the reply maps
each `uuid` to `"ok"` or an error object. The `uuid` makes a retry idempotent.

| App action | Command | Notes |
| --- | --- | --- |
| Complete | `item_close` | What Todoist's own checkbox does: a non-recurring task is completed, a recurring one moves to its next due date |
| Reopen | `item_uncomplete` | Only offered for tasks that are completed in the cache |

Subtasks: Todoist completes the subtasks of a completed parent. The local overlay does the same, so the UI
does not flicker before the next sync.

Which command is right for recurring tasks and for parents with subtasks is **not verified** against a live
account (see [Unverified](#unverified)).

## Opt-in per source

A Todoist personal token always has read and write access ([todoist-provider.md](todoist-provider.md#security)),
so the token itself cannot enforce read-only. The source gets a switch instead:

| Field | Kind | Default | Notes |
| --- | --- | --- | --- |
| `allowChanges` | Boolean | off | *Allow All My Tasks to change tasks in Todoist (preview)*; help text says it is a preview of a version 2 feature |

Existing sources keep working as before until the user switches it on. While off, the source does not expose
`IWritableTaskSource` behaviour to the UI (no checkbox). The Security text in the Todoist doc changes from
"only calls read endpoints" to "calls write endpoints only for actions the user takes, and only when
`allowChanges` is on".

## Pending changes

The store gets a small queue, so the UI never waits for the network:

```csharp
public sealed record PendingChange(
    Guid Id,                 // also the Todoist command uuid
    ItemKey Key,
    PendingKind Kind,        // Complete | Reopen
    DateTimeOffset CreatedAt,
    int Attempts,
    PendingState State,      // Queued | Failed
    string? Error);
```

Flow:

1. The user ticks a task. A view-model calls `ITaskChangeService.CompleteAsync(key)`.
2. The service writes a `PendingChange` (replacing an opposite pending change for the same task, so tick then
   untick cancels out) and raises a change event. The UI shows the task as completed.
3. The sync engine drains the queue per source, before pulling, one command batch per sync. On success the
   change is removed and a normal sync follows, which brings the real state (for example the next due date of a
   recurring task).
4. On a transient failure the change stays `Queued` and is retried with the source's backoff. On a permanent
   failure it becomes `Failed`; the task shows a warning marker with the error and a retry or discard action.

### Overlay

Reading tasks applies the pending changes on top of the cached items (`PendingOverlay`, a pure function in Core).
That is what keeps a sync that returns before the push from flipping the checkbox back. When the sync after a
successful push brings the real data, the overlay for that change is gone.

### Conflicts

| Situation | Result |
| --- | --- |
| Task already completed remotely | `AlreadyInState`, change removed |
| Task deleted remotely | `NotFound`, notice shown, change removed |
| Token revoked | Auth error; source shows its normal sign-in problem, changes stay queued |
| Task changed remotely in another field | No conflict: only the completion flag is sent |

## Recurring tasks

Completing a recurring task does not end it: Todoist moves it to the next occurrence. After the push, the next
sync returns the task as open with the new due date. In the meantime the overlay shows it completed, and a
short notice says "Next: <date>" once the real data arrives.

Undo: after completing a non-recurring task, a notice offers **Undo** for a few seconds (it cancels a still queued
change or sends `item_uncomplete`). Undo is not offered for recurring tasks, because reverting a recurrence step is
not something the API supports safely.

## UI

- The existing display-only checkbox glyph in the All tasks row becomes a button. The details pane gets the same
  control and keyboard access (Space on a focused row toggles it).
- A task with a `Failed` pending change shows a warning icon; its tooltip and the details pane carry the error and
  Retry / Discard.
- Source status shows "N changes waiting" when the queue for a source is not empty.
- Tasks in a read-only source or collection look and behave as today.

## Tests

- `PendingOverlay`: complete, reopen, tick-then-untick, subtasks, task missing from the cache.
- `ITaskChangeService` and queue: coalescing, restart persistence, retry and backoff, failure states (fake clock).
- Todoist: command body for each action, mapping of the reply per `uuid` (ok, error, mixed), idempotent retry,
  secrets absent from logs and exceptions (same checks as the read path).
- Sync engine: queue drained before pull; a pull between enqueue and push does not undo the change.
- Optional integration test against a throwaway account, gated by `AMT_TODOIST_TOKEN`, off in CI.

## Implementation notes

Code: `AllMyTasks.Core/Changes` (queue types, `PendingOverlay`, `PendingOverlayStore`, `TaskChangeService`),
`IWritableTaskSource` in `AllMyTasks.Core/Providers`, the push step in `SyncEngine`, `WritableTodoistSource` in the Todoist
project, and `SqlitePendingChangeStore` with migration `006_pending_changes.sql`. Differences from the design:

- **Change id is passed to the provider.** `CompleteAsync` and `ReopenAsync` take the `PendingChange.Id`, which the Todoist
  provider sends as the command `uuid`, so a retry after a timeout is a repeat of the same command.
- **One request per change, not one batch.** The engine sends the queued changes of a source one after another, oldest first,
  before it pulls. Batching several commands into one request is possible later; it only changes the provider.
- **Who sees the overlay.** Views read through `PendingOverlayStore`, a decorator over the cache that applies the pending
  changes to the tasks it returns. The sync engine writes to the undecorated cache, so the overlay never lands in the database.
- **Allow switch.** With `allowChanges` off the factory creates a plain `TodoistSource`; with it on, a `WritableTodoistSource`.
  Editing a source recreates it, so the switch takes effect at once. Queued changes of a source that no longer allows changes
  become `Failed` ("Changes are switched off for this source") and can be discarded.
- **Read-only lists.** `TaskChangeService` refuses a change for a collection with `IsReadOnly` and for a source that does not
  implement `IWritableTaskSource`; the UI then shows no checkbox. Todoist's lists report `IsReadOnly` unless the source allows changes, so switching the option on takes effect for the checkbox after the next sync (which saves the lists again).
- **Failure handling in the engine.** An unreachable source or rejected token stops the sync as before and leaves the change
  queued (the attempt count goes up). Any other `SourceException` marks only that change `Failed` with the provider's short
  message and the rest of the queue goes on. `NotFound` drops the change and sets a source warning.
- **Todoist memo.** The provider remembers one reply per sync token for two minutes; a write clears that memory so the pull
  that follows does not return a reply from before the change.
- **UI.** In All tasks the glyph is a button when the task can be changed; Space on a focused row toggles it, Enter opens
  details. The details pane has a "Mark as done" or "Mark as not done" button and, for a failed change, the error with Retry
  and Discard. A notice bar below the toolbar shows "Completed ..." with Undo for eight seconds (not for recurring tasks), and
  the sync summary shows "N changes waiting" or "N changes failed".
- **Not done yet.** The "Next: <date>" notice for a recurring task once the real data arrives. A tick or untick that races
  with the engine sending the same change can end with the source and the screen disagreeing until the next sync, which then
  shows the source's state.
- **Migration number.** `006`, after the meeting-link (`004`) and reminders (`005`) migrations that merge first. Until those land, a database created from this branch is at version 6 and would skip them, so use a fresh data directory when switching between branches.

## Docs updated with the implementation

[todoist-provider.md](todoist-provider.md) (scope, security, config schema), [providers.md](providers.md) (write
interface), [storage-and-sync.md](storage-and-sync.md) (pending changes table, sync order),
[views.md](views.md#all-tasks), [task-details.md](task-details.md), the README status paragraph and the
[roadmap](roadmap.md).

## Unverified

To check against a live Todoist account before the docs call this verified:

- `item_close` on a recurring task and on a parent with subtasks, and what the next incremental sync returns.
- `item_uncomplete` on a task completed through `item_close`.
- The shape of `sync_status` errors (task not found, rate limit) and the `Retry-After` handling on write commands. The provider assumes a task that is gone is reported with the words "not found" in the error text; any other error is a failed change.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Opt-in switch per source, or a global "allow changes" setting? | Per source, off by default |
| 2 | Offer Undo for recurring tasks? | No |
| 3 | Show the failed-change queue anywhere beyond the row marker? | Not in v1; the source status line is enough |
