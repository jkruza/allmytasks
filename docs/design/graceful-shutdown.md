# Graceful shutdown and crash logging

Issue: [#133](https://github.com/jkruza/allmytasks/issues/133).

## Problem

When Windows shut down while the app was running, Windows showed "AllMyTasks.exe - Application Error: unknown
software exception (0xe0434352)". That code is an unhandled .NET exception. The exit clean-up in `App` disposed
everything in one `Exit` handler with no guard, so one throwing step skipped the rest and the exception escaped.
Background work also kept running while its dependencies were disposed (`SyncEngine` disposed its semaphore while a
sync still had to release it, and the reminder scheduler disposed its cancellation source while a tick read its token).

The exact exception was not known because nothing was logged. This design fixes the likely causes and makes a
remaining one visible.

## Design

- `ShutdownSequence` (Core, `Startup/`) holds the exit steps. Each runs in its own `try/catch`; a failure is written
  to the log and the next step still runs. It runs once, and `HasStarted` tells handlers the app is closing.
- Order: reminders, sync scheduler and sync engine first (background work stops and in-flight syncs are cancelled),
  then the tray icon (hidden before it is disposed), the view models, the provider factories, the change service and
  the secret store.
- `SyncEngine.Dispose` cancels running syncs through a linked token. It no longer disposes the semaphore or the token
  source: a sync winding down still releases the semaphore, and releasing a disposed one throws. Neither holds an
  operating system handle, so leaving them to the garbage collector is safe.
- `SyncScheduler` ignores timer ticks after `Dispose` and swallows the errors a closed engine or database raises then.
  `ReminderScheduler` no longer disposes its cancellation source for the same reason.
- `Program.Main` logs unhandled exceptions (`AppDomain.UnhandledException`) and unobserved task exceptions to
  `logs/allmytasks.log` (type, message and stack; no data). An exception that reaches `Main` after shutdown started is
  logged and the process exits with code 0 instead of crashing. Any other unhandled exception is logged and rethrown.
- A UI-thread exception raised after shutdown started is logged and marked handled.

## Limits

An exception on a background thread with no handler still ends the process (this cannot be suppressed in .NET); it is
now logged first. If the Windows dialog still appears, the log shows which exception it was.

## Tests

- `ShutdownSequence`: steps run in order, a failing step does not stop the others or throw, a second run does nothing.
- `SyncEngine`: disposing cancels a running sync, and a sync started afterwards is cancelled rather than throwing
  `ObjectDisposedException`.
- The Windows shutdown itself is platform behaviour and is checked by hand (below).

## Manual check on Windows

1. Start the app and leave it running (also with `--tray`, and with a sync in progress right after start).
2. Shut down or restart Windows, then sign in again.
3. No "Application Error" dialog should appear, and Event Viewer > Windows Logs > Application should have no new
   ".NET Runtime" or "Application Error" entry for `AllMyTasks.exe`.
4. Open `%LOCALAPPDATA%\AllMyTasks\logs\allmytasks.log`. Lines such as `Shutdown step '...' failed` or
   `Exception while exiting` name any step that still fails; please attach them to the issue.
