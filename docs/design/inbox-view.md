# Inbox view (tasks without a date)

Tracked in [#74](https://github.com/jkruza/allmytasks/issues/74). **Status: designed, not implemented.**

## Goal

A version 1 view for triage: every open task from every source that has no due date. Undated tasks never show on
the calendar grids or in Upcoming, so without this they are easy to forget. Todoist's Inbox, GitHub issues
without a milestone and Jira issues without a due date all end up here.

## Why not just use All tasks

[All tasks](views.md#all-tasks) can already filter "Has no due date". But that is a setting someone has to
find and apply, and it is not visible next to Day, Week, Month and Upcoming. The Inbox is that preset promoted to a
view of its own, because "what has no date yet" is one of the three questions the app exists to answer.

## Design

- A fifth entry in the view switcher ([toolbar.md](toolbar.md)): Day, Week, Month, Upcoming, **Inbox**, All tasks.
- It reuses the All tasks list, rows, details pane and source/collection visibility. No new query code: it is a
  fixed `TaskFilter` of `Status = Open` and `Due = None`, grouped by collection by default, sorted by last
  modified, newest first.
- The status and due filters are fixed (not editable). Text filter, source/collection, priority and label
  filters still work, because narrowing an inbox is useful.
- A count of matching tasks is shown on the switcher entry, so a growing inbox is noticed without opening it.
- Subtasks follow the rule for All tasks: nested under the parent, or shown at top level when the parent is
  filtered out.
- Empty state: "Nothing without a date. Everything open has one."
- Read-only: the checkbox appears only when a source has opted in to changes ([write-support.md](write-support.md)).

## Core changes

`DueFilter` gains no new value; `TaskFilter.Inbox` is a static preset built from existing values, and the view
model chooses it. The count comes from the same `ITaskListService` with a count-only call. Tests: the preset
excludes dated, completed and cancelled tasks; includes tasks from every visible collection; respects hidden
collections; subtask rule.

## Open questions

- Should a task with a start date but no due date count as undated? Proposal: yes, the Inbox is about due dates.
- Should a task that stays in the Inbox for a long time be marked as stale? Proposal: not in the first cut.
