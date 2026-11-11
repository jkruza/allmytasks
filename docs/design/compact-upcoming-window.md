# Compact upcoming window

Tracked in [#141](https://github.com/jkruza/allmytasks/issues/141). **Status: proposed, version 2.**

## Goal

A small window that shows the next few entries, so the overview can sit on a corner of the screen without the full
app. [reminders.md](reminders.md) lists "compact upcoming widget" as still open, and [views.md](views.md#upcoming)
calls Upcoming the likely tray or compact view.

## Design

- Opened from the tray menu ([system-tray.md](system-tray.md)) and from a shortcut; remembers position and size.
- Content: the same `UpcomingList` as the Upcoming view, limited to the next N entries (default 8) plus an Overdue
  count. Row: time, title, collection colour dot, source badge. A "Now" marker when something is on.
- Click a row: opens the main window on that entry's details. No editing, no checkboxes before version 2 writes.
- Options: always on top (off by default), opacity, hide when the main window is focused.
- Narrow layout, minimum about 280 px wide, using the design-system tokens, light and dark.

## Platform notes

- Always-on-top works on Windows and macOS; on Linux Wayland it is a compositor decision and may be ignored.
  Say so in Settings rather than hiding the option.
- Some Linux desktops cannot show a tray flyout, so a normal small window is the common denominator; a native
  flyout is not planned.

## Core changes

None in logic: it reuses `UpcomingList` with a different limit. A `CompactUpcomingViewModel` binds it and the
window position is stored in settings (same approach as [details-pane-resize.md](details-pane-resize.md)).

## Testing

Limit and overdue count, "Now" marker, empty state, restored bounds clamped to the visible screens.

## Open questions

- Single window shared with the main app process (yes) or a separate process? Proposal: same process.
- Does it need its own refresh rate? Proposal: no, it follows the store-changed notification.
