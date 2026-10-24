# Settings

The first settings UI: a **Settings** dialog opened from the toolbar. It starts with one
setting, **week start**, and is built so the next settings are a row each, not a redesign.

## Goals

- A Settings dialog that owns persistence of app-wide preferences in one place.
- Week start: **Sunday** or **Monday**. Every view that lays out weeks follows it, and a change
  applies immediately, with no restart.
- Room to grow: adding a setting should touch one record, one view model property and one row.

## Non-goals

- Per-source settings (they stay in the Sources window) and credential/PIN settings (see
  [storage-and-sync.md](storage-and-sync.md#credentials)).
- Saturday as week start. The earlier idea in [views.md](views.md#week) is dropped: the request
  is Sunday or Monday, and locales that start on Saturday fall back to Monday (below).
- Theme, language, time format. Not designed here.

## Week start

`WeekStartSetting` has three values:

| Value | Meaning |
| --- | --- |
| `System` (default) | Follow the OS culture's first day of week |
| `Sunday` | Weeks start on Sunday |
| `Monday` | Weeks start on Monday |

**Default.** `System`, i.e. locale-based. The dialog shows the current result, such as
"System default (Monday)", so the user can see what it resolves to. When the culture's first
day is neither Sunday nor Monday (some Middle East locales use Saturday), `System` resolves to
**Monday**, the ISO 8601 choice, because only those two are supported. The resolution is a pure
Core function, `WeekStart.Resolve(setting, culture)`, with tests.

**Why not a plain Sunday/Monday default.** A fixed default is wrong for half of users on first
run. Locale already drives this today (`CultureInfo.CurrentCulture`), so keeping it as the default
changes nothing for existing users.

### What follows the setting

All code that decides where a week starts takes a `DayOfWeek` from `ViewContext.FirstDayOfWeek`;
the shell is the only place that reads the setting.

- Week view: columns, and the title range (`WeekLayout.StartOfWeek`).
- Month view: grid start and weekday header row (`MonthGrid`).
- Previous/next and "today" in the week view move by 7 days and are unaffected.
- Day, Upcoming and All tasks have no week layout. The "This week" bucket in All tasks is
  relative to now and not part of this change.

"Hide weekends" in the week view still hides Saturday and Sunday by name, whatever the first day.

## Persistence

Stored as JSON under the key `settings.app` in the existing `settings` table (see
[storage-and-sync.md](storage-and-sync.md)), through `ViewStateStore`:

```json
{ "weekStart": "System" }
```

- An unknown or missing value reads as the default. Unknown extra fields are ignored, so an older
  build reading a newer file does not fail.
- Saving never throws into the UI (same rule as other preferences).

`AppSettings` (immutable record) is the single shape. `SettingsService` holds the current value,
loads it at startup, saves on change and raises `Changed`. The shell subscribes and reloads the
current view.

## Dialog

`SettingsWindow` is opened by `DialogService.ShowSettingsAsync`, which uses the existing
`GetDialogOwner` helper so it opens in front of the window the user is working in (the Sources
window included). Opened from a toolbar button next to Sources.

- Changes apply as they are made; one **Close** button. No Save/Cancel, so there is no
  half-applied state.
- Rows are grouped under headings (**Calendar** now; later sections get their own heading).
- `SettingsViewModel` is thin: it exposes the options and writes through `SettingsService`.

## Tests

- `WeekStart.Resolve`: Sunday/Monday explicit, `System` with `en-US` (Sunday), `pl-PL`/`en-GB`
  (Monday), a Saturday-first culture (Monday).
- Settings round trip through a settings store, missing/corrupt/unknown values read as default.
- Shell: changing the setting reloads and the week/month contexts get the new first day.
- Existing month and week tests already cover both first days.

## Rollout

Single PR. No migration (new key in an existing table).

## Implementation notes

- Core: `Settings/WeekStartSetting.cs` (`WeekStartSetting`, `WeekStart.Resolve`) and `Settings/AppSettings.cs`
  (record, tolerant `Parse`, `ToJson`). Tests in `AppSettingsTests`.
- App: `SettingsService` (current value, save, `Changed`), `SettingsViewModel`, `SettingsWindow`,
  `DialogService.ShowSettingsAsync`, a gear button in the toolbar.
- `CalendarShellViewModel` no longer reads `CultureInfo` for the first day; it asks `SettingsService`
  and reloads on `Changed`.
- To add a setting: a property on `AppSettings` (and its `Parse`/`ToJson`), a property on
  `SettingsViewModel`, a row in `SettingsWindow.axaml`.
