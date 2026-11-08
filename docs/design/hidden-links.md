# Hidden raw links in the details pane

Tracked in [#121](https://github.com/jkruza/allmytasks/issues/121). **Status: implemented (version 1).**

## Goal

The [details pane](details-pane.md) should not show raw URLs. A link is an **Open link** button; the address is
the button's tooltip, so it can still be checked before clicking.

## Today

- Task details already have an **Open link** button and show no URL text.
- Calendar entry details show the same button **and** a "Link" row with the full URL, so the address is on screen
  twice. Long provider URLs (Outlook `webLink`, Jira, Google) are noisy and wrap badly in a narrow pane.
- The Join button for online meetings shows only the host as small print (kept, see below).

## Design

- Remove the **Link** row from `EntryDetailsBuilder`. The openable `http`/`https` link stays in `EntryDetails.Link`.
- **Open link** (task and event details) gets `ToolTip.Tip` with the full address.
- **Join** gets the meeting address as its tooltip too. The host line under it stays: it is short, and it makes a
  lookalike link visible without hovering (see [meeting-url-and-attendees.md](meeting-url-and-attendees.md)).
- A link with another scheme (`mailto:`, `file:`, `javascript:`) is not openable and, with the row gone, is not
  shown at all. It was never actionable, and Core still never passes it to the launcher.
- Out of scope: URLs inside descriptions and notes. That text is handled with the notes and description
  formatting work, not here.

## Tests

- `EntryDetailsBuilderTests`: no "Link" row for any link; `Link` still set for `http`/`https` and null for other
  schemes.
- The view model exposes the tooltip text (`LinkTooltip`, `MeetingTooltip`) from the validated `Uri`, so they are
  the same value that would be opened.

## Decisions

| # | Question | Decision |
| --- | --- | --- |
| 1 | Show non-http links as text? | No; not openable, so not shown |
| 2 | Keep the host under Join? | Yes; it is a short safety cue, not a raw link |
