# Reminders and notifications

Tracked in [#55](https://github.com/jkruza/allmytasks/issues/55). Implemented for the core, the scheduler, the Linux
notifier and the in-app fallback; the Windows and macOS native notifiers wait for the installers (open question 4). See
[Implementation notes](#implementation-notes) for where the code differs from the sketches below.

Desktop notifications shortly before an event starts and when a task is due. The tray icon and
launch-at-login ([system-tray.md](system-tray.md), [launch-at-login.md](launch-at-login.md)) let the app run all
day, which is what makes this worth building.

## Goals

- A notification a configurable time before each event, and at the due time of each task.
- Clicking a notification opens the app on that item; **Snooze** and **Dismiss** work from the notification where the
  OS allows actions, and from an in-app fallback otherwise.
- No duplicates after a restart, a sync or a re-expanded recurrence; no flood after the machine was asleep.
- Same behaviour on Windows, macOS and Linux behind one Core abstraction.

## Non-goals

- Reminders when the app is not running. If it is not running there is nothing to fire; the settings page says so and
  points to launch at login.
- Syncing provider reminders (`VALARM`, Google `reminders`, Outlook `reminderMinutesBeforeStart`) into the app. v1
  uses the app's own settings for every item. Reading per-event provider reminders is a possible later addition.
- Sounds, mobile or e-mail notifications, per-item custom reminders created in the app.
- Reminders for all-day events (see open questions).

## Rules

| Item | Fires at | Not for |
| --- | --- | --- |
| Timed event | start minus lead time (default 10 minutes) | cancelled events, events that already ended |
| Task with a due date-time | the due time | completed or cancelled tasks |
| Task with only a due date | a configurable time of day on that date (default 09:00 in the display zone) | completed or cancelled tasks |
| Overdue task | not repeated; the due-time notification was the reminder | |

Recurring events and tasks use expanded occurrences (see [domain-model.md](domain-model.md#recurrence)), so each
occurrence has its own reminder. Collections the user hid in the shared filter do not produce reminders.

## Core

```csharp
public sealed record ReminderSettings(
    bool Enabled, TimeSpan EventLead, bool Tasks, TimeOnly DateOnlyTaskTime, TimeSpan SnoozeLength);

public sealed record Reminder(ItemKey Key, DateTimeOffset FireAt, ReminderKind Kind, string Title, string? Detail);

public static class ReminderPlanner
{
    public static IReadOnlyList<Reminder> Plan(
        IEnumerable<CalendarEvent> events, IEnumerable<TaskItem> tasks,
        ReminderSettings settings, TimeZoneInfo displayZone, DateTimeOffset from, DateTimeOffset to);
}
```

`Plan` is a pure function over a time window, so DST, recurrence and filter behaviour is tested without a timer.

`ReminderScheduler` owns the clock:

1. Plans the window `[now - catchUp, now + 36 h]` at start, after every sync that changed items, after a settings
   change and after the system wakes from sleep.
2. Sleeps until the next `FireAt` (one timer, not one per reminder) and delivers everything that is due.
3. Records each delivery in the store (`ReminderLog`: `ItemKey`, occurrence start or due time, state `Shown`,
   `Snoozed(until)` or `Dismissed`). Planning skips anything already logged, which is what prevents duplicates.
4. Prunes log rows for items that ended more than a day ago.

Catch-up: on start and on wake, reminders that were due within the last 15 minutes and are still relevant (event not
yet ended, task still open) are delivered once; older ones are skipped, and if more than three were skipped a single
notification says "N reminders missed". This is a constant for now, not a setting.

An item that changes after a sync (event moved, task completed or rescheduled) is re-planned; a reminder already
shown for the old time is not shown again, and a moved event gets a new reminder at its new time.

## Platform notifications

Avalonia has no OS-level notification API, only in-window ones, so each OS needs its own delivery behind:

```csharp
public interface INotifier
{
    bool SupportsActions { get; }
    Task ShowAsync(Reminder reminder, CancellationToken ct);
    event EventHandler<NotificationActivated> Activated;   // click, Snooze, Dismiss, Join
}
```

| OS | Mechanism | Requirement |
| --- | --- | --- |
| Windows | Toast notifications (Windows App SDK or the Community Toolkit notifications package) | The app needs an app user model id registered through a Start menu shortcut, which the installer creates; `dotnet run` and the portable zip need a fallback |
| macOS | `UNUserNotificationCenter` through a small native interop | Only works for an app bundle with a bundle id; unbundled runs fall back |
| Linux | `org.freedesktop.Notifications` over D-Bus (Tmds.DBus) | A notification daemon, present on all common desktops; actions only if the daemon advertises them |

The choice of library on each OS is made in the implementation PR, with the packaging work in
[packaging.md](packaging.md) in view: Windows and macOS notifications are only reliable for an installed, packaged
app. The fallback is an in-app notification window (a small topmost window with the same Open / Snooze / Dismiss
actions) so reminders still work for portable and development runs.

Notification text comes from third-party calendars and is treated as plain text, truncated to a fixed length, with no
markup interpreted.

## Settings

A **Notifications** section in the [settings dialog](settings.md), stored in `AppSettings` like the other options:

| Setting | Default |
| --- | --- |
| Show reminders | on |
| Remind before events | 10 minutes (choices: at start, 5, 10, 15, 30, 60) |
| Remind about tasks | on |
| Time for tasks that have a date but no time | 09:00 |
| Snooze for | 10 minutes |

A hint under the master switch states that reminders only appear while the app is running, with a link to the
launch-at-login option. On Linux the tray caveat applies only to the tray, not to notifications.

## Interaction with other features

- [Meeting links](meeting-url-and-attendees.md): when an event has a `MeetingUrl`, the notification gets a **Join**
  action in addition to Open.
- [Complete tasks](complete-tasks.md): a **Done** action on task notifications can be added once completion exists;
  not part of this issue.

## Tests

- `ReminderPlanner`: timed, all-day (none), date-only tasks, recurring occurrences, DST gap and overlap days, hidden
  collections, cancelled and completed items, window edges.
- `ReminderScheduler` with a fake clock and a fake `INotifier`: next-fire sleeping, dedupe across restarts (log),
  re-plan after a moved event, catch-up and the "N missed" summary, snooze, wake from sleep.
- Notifier adapters are thin; their manual check status is tracked the same way as the tray and launch-at-login work.

## Implementation notes

What shipped, and where it differs from the sketches above.

- **Planner input.** `ReminderPlanner.Plan` takes the `AgendaEntry` list from `IAgendaService` instead of raw events and
  tasks. The agenda service already expands recurrences, drops hidden collections and resolves times into the display
  zone, so the planner stays a pure function and the rules are not written twice. All-day events are recognised by
  `IsAllDay` and produce nothing.
- **Reminder identity.** `Reminder` also carries `Occurrence` (event start or due instant) and `EndsAt`. The key plus the
  occurrence identifies a reminder: a recurring series shares one `ItemKey`, and a moved event is a different
  occurrence, so it gets a new reminder. `EndsAt` is the end of the event, or a day after a task's due time.
- **Scheduler.** Every wake (timer, sync that changed items, settings change, a snooze) runs one pass: it reads the agenda
  for the window, plans, delivers what is due and arms the single timer for the next reminder. The timer never sleeps
  longer than two minutes, which is how a wake from system sleep or a clock change is noticed without a platform
  hook. There is no cached plan to go stale.
- **Window and catch-up.** A pass looks back to the last check (at most 24 hours), so reminders that came due while the
  app was closed are seen. Those due within 15 minutes are delivered; older ones are marked dismissed, and more than three
  produce one "N reminders missed" notification. A first start, and turning reminders back on, count nothing as missed.
  Events that already ended are never announced.
- **Log.** `reminder_log` (migration 004) stores the whole reminder with its state, so a snoozed reminder survives a restart.
  A snoozed reminder is only shown again if the item is still in the plan: a completed task, a cancelled event or a moved
  event drops it. The time of the last check is kept in the `settings` table (`reminders.lastCheck`). Rows are pruned a
  day after the reminder's `EndsAt`.
- **At most once.** A reminder is logged as shown before the notifier is called, so a crash or a failing notifier loses a
  notification instead of repeating it every few minutes.
- **Linux.** `LinuxNotifier` runs `notify-send` (libnotify) rather than speaking D-Bus directly. It needs no new
  package, handles the daemon for us, and with libnotify 0.8 or newer gives Open (clicking the notification), Snooze and
  Dismiss, read from what the program prints when the notification closes. An older `notify-send` without `--action` is
  detected from its fast failure and used without actions. A missing program, or no notification daemon, raises
  `NotifierUnavailableException`, and `FallbackNotifier` switches to the in-app window for the rest of the session.
  The body is escaped because daemons may render markup; the title is passed after `--` so a title starting with a dash is
  not an option. A direct D-Bus notifier (Tmds.DBus) remains possible if actions turn out unreliable.
- **In-app fallback.** `InAppNotifier` shows a small topmost window (`ReminderWindow`) in the bottom-right corner with
  Open, Snooze and Dismiss. It does not take focus. It is the only notifier on Windows and macOS for now.
- **Settings.** `AppSettings` has `remindersEnabled`, `eventReminderMinutes`, `remindAboutTasks`,
  `dateOnlyTaskReminderTime` (`HH:mm`) and `snoozeMinutes`; invalid stored values fall back to the defaults. A
  date-only time that falls in a DST gap moves to the next valid time, and one that happens twice uses the first.
- **Open.** Open shows the main window (also from the tray), opens the task's details, or switches to the event's day
  and opens its details. The **Join** action arrives with the meeting links work ([#57](https://github.com/jkruza/allmytasks/issues/57)).

## Manual check status

Unit-tested on Linux in CI: planner, scheduler (fake clock and notifier), the log in SQLite, settings and the `notify-send`
adapter against a fake process. Not run against a real desktop yet: the actual `notify-send` output on GNOME, KDE and
others (the action name it prints, what it prints when the notification just closes, the click on the notification), the
in-app window's position and stacking on each OS, and the Settings section. These need a check on a real session.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Reminders for all-day events (for example a birthday)? | Not in v1; a later "evening before" option |
| 2 | Honour provider reminders per event? | Not in v1 |
| 3 | Per-collection reminder switch? | Not in v1; hiding a collection already silences it |
| 4 | Build a Windows/macOS notification path before the installers exist? | Core, scheduler, Linux and the in-app fallback first; Windows and macOS adapters land with the packaging issues. Done as proposed |
