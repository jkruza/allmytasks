# Details pane and calendar entry details

Clicking an event or dated task in any calendar view shows its details in a pane on the
right of the main window. This doc covers the pane itself (a host other detail views, such
as task details, plug into) and what the calendar entry details show. v1 is read-only, like
the rest of the app.

## Goals

- Click (or keyboard-activate) any entry in Day, Week, Month or Upcoming and see everything
  the app knows about it, without leaving the view.
- One pattern for "show details of the selected thing", shared with the task details work.
- Read-only. Nothing here writes back to a provider.

## Non-goals

- Editing, deleting, RSVP. See [providers.md](providers.md#write-support-later).
- **Attendees.** `CalendarEvent` has no attendee data yet and the iCalendar mapper drops
  `ATTENDEE` and `ORGANIZER`. The pane shows only what the model holds; attendees need a
  model and mapper change first and are a follow-up. The field list below is built so an
  extra row is a small change.
- Rendering HTML descriptions or auto-linking text inside descriptions.

## Decision: a docked pane, not a popover or a dialog

| Option | For | Against |
| --- | --- | --- |
| Popover anchored to the entry | Close to the click | Hard to place in Week and Month where entries sit at window edges and are tiny; long descriptions need a scrolling popup; selected text and links inside popups behave differently per platform |
| Modal dialog | Plenty of room | Blocks the window for a read-only glance; needs the dialog-owner handling in `DialogService`; one more window to manage on every click |
| **Docked right pane** | Same behaviour in every view; no window or popup ownership issues; room for long text; stays open while the user clicks through entries; keyboard and screen reader friendly | Takes about 320 px from the content while open |

The pane wins because it is the only option that needs no per-view placement logic and no
extra windows. It is closed by default, so the cost is paid only while the user is reading
details. Week and Month use uniform grids, so they simply get narrower.

## The pane host

`CalendarShellViewModel` exposes one property:

```csharp
object? SelectedDetails   // null = pane closed
```

`MainWindow.axaml` docks a pane on the right. It is visible when `SelectedDetails` is not
null and renders it with a `ContentControl` whose `DataTemplate`s map details view models
to views. A header with a Close button sits above the content.

- `ShowDetails(...)` sets `SelectedDetails`; `CloseDetailsCommand` sets it to null.
- **Esc** closes the pane (handled in `MainWindow`, ignored while typing in a text box).
- The pane closes when the user changes view mode or date, because what was selected is no
  longer on screen. It stays open across background syncs and shows the data as it was when
  it was opened; click the entry again to refresh.
- Adding another kind of details is: a view model, a view, a `DataTemplate` entry.

Entries are activated through the `EntryActivation` attached behaviour (`Controls/`). Setting
`EntryActivation.Enabled="True"` on an entry template makes it focusable, opens details on
click and on Enter or Space, and finds the shell through the window's `DataContext`. This
keeps `EntryItem` and the per-view view models free of references to the shell.

## What the calendar entry details show

Built in Core by `EntryDetailsBuilder` from an `AgendaEntry` so it is unit-tested; the view
model only exposes it. Rows that have no value are not shown.

| Row | Source | Notes |
| --- | --- | --- |
| Title | `Title` | Empty title shows "(no title)" like the other views |
| When | `Start`/`End`/`IsAllDay` of the **occurrence** | Display time zone. All-day: date or date range with day count. Multi-day timed: both dates. Never the series' first start |
| Repeats | `Recurrence` | Plain-language summary such as "Every 2 weeks on Monday, Wednesday, 10 times". Monday to Friday reads "Every weekday". Rules the summariser does not understand show "Repeats (custom rule)". Overrides add "This occurrence was changed" |
| Where | `Location` | Plain text |
| Calendar | collection name + colour | Plus the source name, e.g. "Work, from Fastmail" |
| Status | `Status` | Only shown when Tentative or Cancelled |
| Show as | `Availability` | Only shown when Free |
| Categories | `Categories` | Comma separated |
| Link | `Url` | A button; only `http` and `https` links are openable, other schemes are shown as text |
| Description | `Description` | Plain text, selectable, scrolls. Capped at 20 000 characters with a "…" so a huge feed value cannot stall the UI |
| Last changed | `LastModified` | Small print, when the provider supplies it |

Dated tasks that appear in the calendar views open the same pane with the fields that
exist for tasks (title, due, list, status, priority, labels, notes, link). The dedicated
task details view model replaces this once it lands; the host does not change.

### Text safety

Descriptions, locations and titles come from third-party feeds. They are only ever shown
as text in selectable text blocks, never parsed as markup, and links are only opened from
the explicit link row after a scheme check.

## Core types

```csharp
public sealed record DetailRow(string Label, string Value);

public sealed record EntryDetails(
    string Title,
    bool IsTask,
    string AccentColor,                // collection colour, #RRGGBB
    IReadOnlyList<DetailRow> Rows,     // When, Repeats, Where, Calendar, Status, ...
    Uri? Link,                         // http/https only, else null (the raw value is a row)
    string? Description);

public static class EntryDetailsBuilder
{
    public static EntryDetails Create(AgendaEntry entry, TimeZoneInfo zone, string? sourceName);
}

public static class RecurrenceDescriber
{
    public static string Describe(Recurrence recurrence);
}
```

## Accessibility

- Entries are focusable and announce their existing spoken text; Enter or Space opens
  details.
- Opening moves focus to the pane's Close button. Closing leaves focus on the window, so the
  keyboard shortcuts keep working.
- The pane has an automation name ("Details for <title>").

## Testing

- `EntryDetailsBuilder`: timed single-day, timed multi-day, all-day single and multi-day,
  zero-length point, display zone differing from the stored zone, missing optional fields
  produce no rows, empty title, long description cap, non-http links, tasks.
- `RecurrenceDescriber`: daily, weekly with days and interval, monthly, yearly, `COUNT`,
  `UNTIL`, `RDATE`-only, unknown rule.
- UI wiring (pane, click, Esc) is checked by hand on Windows, Linux and macOS: see the
  manual checklist in the pull request.
