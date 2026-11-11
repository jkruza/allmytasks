# Settings layout

The Settings dialog ([settings.md](settings.md)) grew from one setting to about twenty and is now a
single column of seven sections, roughly 2000 px tall at 440 px wide. This document replaces that
column with side navigation. Tracked in issue #154.

## Goals

- Any setting is at most one click from the dialog opening, with no long scrolling.
- Adding a setting is still one row in one page; adding a topic is one more page.
- Keyboard and screen-reader friendly with the standard Avalonia controls, no custom navigation.

## Non-goals

- A search box over settings. About twenty settings do not need it; revisit past roughly forty.
- Changing any setting, default or its persistence. Only the layout changes.
- Per-source settings, which stay in the Sources window.

## Decision

A **side navigation** (a `TabControl` with the strip on the left) over horizontal tabs: five or
more labelled topics fit a vertical list without wrapping or overflowing, the list grows downward
as topics are added, and it is the pattern of the Windows, macOS and GNOME settings the user already
knows. A single-column accordion was rejected because it still scrolls and hides settings behind clicks.

## Pages

| Page | Contents |
| --- | --- |
| **Appearance** | Accent colour |
| **Calendar** | Week start; overlapping events and all-day busy ([conflict-highlight.md](conflict-highlight.md)); duplicates ([duplicate-detection.md](duplicate-detection.md)); the Upcoming workload strip and its limits ([workload-glance.md](workload-glance.md)) |
| **Notifications** | Show reminders, lead time for events, task reminders, time for date-only tasks, snooze ([reminders.md](reminders.md)) |
| **System** | Launch at login and start in tray ([launch-at-login.md](launch-at-login.md)); keep running in the tray ([system-tray.md](system-tray.md)); check for new versions ([update-check.md](update-check.md)) |
| **About** | Version, build, links to the site and GitHub, built-in sign-in client status ([about-dialog.md](about-dialog.md)) |

Inside a page, related rows sit under the existing quiet section headings (the Calendar page has
*Calendar* and *Workload*; System has *Startup*, *Window* and *Updates*).
Workload sits on the Calendar page because it is about the days of the calendar, and a page of its
own would hold three rows.

## Dialog

- About 700 × 520 px, resizable, with a minimum size so the navigation never clips. It was a fixed
  440 px column that sized itself to its content.
- The navigation is about 160 px wide; each page scrolls on its own and keeps its text at a readable
  width (about 460 px) instead of stretching on a wide window.
- The first page (Appearance) is selected when the dialog opens. The last page is not remembered:
  the dialog is short-lived and the order is stable.
- Changes apply as they are made; one **Close** button, bottom right, outside the pages so it is
  always visible. Escape closes.
- Colours, type and spacing come from the [design system](design-system.md); the navigation uses
  the Fluent tab styling mapped to the accent.

## Adding a setting

A row on the page where it belongs, plus the steps in [settings.md](settings.md#implementation-notes).
A new topic is a new `TabItem` in `SettingsWindow.axaml`; keep pages to about eight rows.

## Rollout

Single PR, no migration, no setting or persisted key changes.
