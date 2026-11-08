# Resizable details pane

Issue: [#124](https://github.com/jkruza/allmytasks/issues/124). Version 1. Builds on
[details-pane.md](details-pane.md).

## Problem

The details pane is a fixed 340 px. Long titles, notes and meeting rows are cramped, and on a
wide monitor there is space to spare. Users want to size it themselves.

## Behaviour

- A thin splitter sits between the main view and the pane. Dragging it left makes the pane
  wider, right makes it narrower. The cursor changes to a horizontal resize cursor over it.
- With the splitter focused, the left and right arrow keys resize in steps (the Avalonia
  `GridSplitter` default). It has the automation name "Resize details pane".
- The splitter and the pane are hidden together; a closed pane takes no space.
- The width is remembered across restarts and shared by all kinds of details (events, tasks).
  It is kept when the pane is closed and reopened.

## Limits

| Limit | Value | Why |
| --- | --- | --- |
| Default | 340 px | The previous fixed width |
| Minimum | 260 px | Rows with a label and a value stay readable |
| Maximum | 640 px | Past this, lines of notes get too long to read |
| Main view minimum | 360 px | Day, Week and Month must stay usable with the pane open |

When the window is too narrow for the requested width, the main view minimum wins over the
maximum, and the pane never goes below its own minimum. Shrinking the window re-fits the pane;
the stored width is not changed by that, so growing the window restores the chosen width.
The window minimum width (640) still fits both minimums.

## Design

- Core: `DetailsPaneWidth` (`Settings/`) holds the constants and `Clamp(width, windowWidth)`.
  It also turns NaN and infinity into the default, so a bad stored value cannot break layout.
  Unit-tested in `DetailsPaneWidthTests`.
- Persistence: the key `view.details` (`{"width":340}`), written through `ViewStateStore`
  like `view.shell`. It is deliberately not part of `AppSettings`: saving it must not make the
  shell reload its data (`SettingsService.Changed`), and it is not a Settings dialog option.
  A missing or unreadable value gives the default. Saving never throws into the UI.
- Shell: `CalendarShellViewModel.DetailsWidth` and `SetDetailsWidth`, which clamps and saves.
  Restored with the rest of the shell state.
- View: `MainWindow` body is a `Grid` with the view, a `GridSplitter` and the pane. The pane
  column is 0 wide while closed and gets its width, minimum and maximum in code-behind
  (`ApplyPaneWidth`). The width is saved when a drag or key press on the splitter ends, not
  on every pixel.

## Out of scope

- Resizing by touch gestures beyond what `GridSplitter` already supports.
- Docking the pane at the bottom or the left, or a per-view width.
- A Settings dialog field for the width. Dragging is the setting.

## Testing

- Core: clamp within, below and above limits; narrow windows; unknown window width; non-finite
  values.
- UI wiring is checked by hand on Windows, Linux and macOS (see the pull request): drag
  wider and narrower, hit both limits, shrink and grow the window with the pane open, close
  and reopen, restart the app, arrow keys on the focused splitter.
