# Design system

Tracked in [#94](https://github.com/jkruza/allmytasks/issues/94). **Status: proposal, rollout step 1 started.** The tokens and
classes exist; the views move over one by one, see [Rollout](#rollout).

## Goal

One small, written set of visual rules so the app, the [project site](project-site.md) and the icon look like the same
product, in light and dark, and so the calendar and task views put the important things first. It extends
Avalonia's `FluentTheme` instead of replacing it: Fluent supplies control templates, focus and accessibility,
we supply tokens and a handful of classes.

## Today (what exists)

- `App.axaml` loads `FluentTheme` with `RequestedThemeVariant="Default"` (follows the OS).
- Three style classes, defined in `App.axaml`:
  - `ToggleButton.seg`: segmented view switcher. Flat, 13 px, 70 % opacity; checked is full opacity, semibold, with a
    `SystemControlBackgroundBaseLowBrush` pill.
  - `Button.tool` / `DropDownButton.tool`: quiet toolbar buttons. Transparent, no border, 13 px.
  - `DropDownButton.chip` (+ `.active`): 12 px pill, 1 px `SystemControlForegroundBaseLowBrush` border; active uses
    `SystemControlHighlightAccentBrush` and semibold.
- Views pull colours from Fluent's `SystemControl*` brushes and from per-collection accents in view models
  (`Accent`, `ChipBackground`, `ChipForeground`). Sizes are literals (11, 12, 13) in each `.axaml`.
- The site defines its own tokens in `site/index.html` (`--s000/100/200`, `--bd`, `--ink`, `--mut`).

## Tokens

Neutrals come from the site, which already matches Fluent's greys; accents come from the logo and the collection palette.

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `s100` (canvas) | `#f3f3f3` | `#202020` | Window background |
| `s000` (surface) | `#ffffff` | `#2d2d2d` | Cards, grid cells, panes |
| `s200` (raised/hover) | `#e6e6e6` | `#3a3a3a` | Hover, selected pill, today column tint |
| `bd` (border) | `#d6d6d6` | `#454545` | Hairlines, grid lines |
| `ink` | `#1b1b1b` | `#f3f3f3` | Primary text |
| `mut` | `#5c5c5c` | `#b3b3b3` | Secondary text, times, counts |

| Accent | Value | Use |
| --- | --- | --- |
| `blue` | `#3b82f6` | App accent: selection, focus, today marker, links |
| `green` | `#10b981` | Done, success |
| `amber` | `#f59e0b` | Due soon, warnings |
| `red` | `#ef4444` | Overdue, errors |
| Collection palette | `#3b82f6 #10b981 #f59e0b #8b5cf6 #14b8a6 #ec4899 #ef4444 #6b7280` | Per-collection colour (source identity) |

Overdue *text* uses `AmtOverdueBrush` (`#c62828` light, `#f07178` dark) rather than the `red` accent, so it keeps 4.5:1 on `s000`; `red` stays for dots and bars.

Rules: text on a tint uses `ink`, never the accent. Collection colour is shown as a 3 px bar or 8 px dot, and as a 15 %
tint for event chips, never as large solid fills. Overdue is the only red in normal use, so it keeps its meaning.

Type: Segoe UI, then the platform UI font (`-apple-system`, Inter, system-ui). Scale: 11 meta, 12 secondary, 13 body and
toolbar, 15 row title, 20 view title, 28 day number in Day view. Weights: 400 and 600 only. Monospace (Cascadia Mono,
Consolas, Menlo) for code and keys only. Spacing: 4 px grid (4, 8, 12, 16, 24). Radius: 3 chips, 6 cards and panes, 12
pills. No shadows except menus and flyouts.

## Mapping to Avalonia

Define the tokens once in `Styles/Tokens.axaml` as a `ResourceDictionary` with `ThemeDictionaries` for `Light` and `Dark`,
under an `Amt` prefix, and alias Fluent's accent so Fluent controls follow ours:

| Resource key | Light / Dark | Notes |
| --- | --- | --- |
| `AmtCanvasBrush`, `AmtSurfaceBrush`, `AmtRaisedBrush`, `AmtBorderBrush` | table above | New |
| `AmtInkBrush`, `AmtMutedBrush` | table above | Replace `SystemControlForegroundBaseHighBrush` / `...MediumBrush` in our views |
| `AmtAccentBrush`, `AmtDoneBrush`, `AmtSoonBrush`, `AmtDangerBrush` | blue, green, amber, red | Dots and bars; `AmtDangerBrush` is `#ef4444` |
| `AmtOverdueBrush`, `AmtWarningBrush` | red / amber, darker in light | Text colours that keep 4.5:1; `AmtAccentTintBrush` is the accent at 15 % |
| `SystemAccentColor` | `#3b82f6` | Set on `FluentTheme.Palettes` (`Light`/`Dark` `Accent`) so default controls follow |
| `AmtFontSizeMeta/Small/Body/Title/Heading/Display` | 11 / 12 / 13 / 15 / 20 / 28 | `x:Double` |
| `AmtSpace1..6`, `AmtRadiusChip/Card/Pill` | 4..24, 3 / 6 / 12 | `x:Double` and `CornerRadius` |

Existing classes keep their names and move onto the tokens: `seg` uses `AmtRaisedBrush` for the checked pill;
`tool` hover uses `AmtRaisedBrush`; `chip` border uses `AmtBorderBrush`, `.active` uses `AmtAccentBrush`. New classes
(`card`, `meta`, `title`, `heading`, `dot`, `badge`) are plain `Style` selectors in `Styles/Controls.axaml`, included from
`App.axaml`. Always `DynamicResource` so the theme can change at runtime. Compiled bindings and `x:DataType` stay as they are.

## Views: clear, then quiet

Principle: **time and status first, source second, chrome last.** Each view answers one question and shows only what is
needed to answer it.

- **Day (agenda).** Question: "what do I do today?" Big date header (Display) with a muted weekday and counts. An
  *All day and due* strip on top (tasks and all-day events), then the timed list with a thin hour rail and a blue "now"
  line. Overdue tasks sit in their own collapsed-by-default-off group above, in red text for the count only.
- **Week.** Question: "how does the week look?" Seven columns on `AmtSurfaceBrush`, hairline `AmtBorderBrush` separators, today
  column tinted with `AmtRaisedBrush`, header shows weekday muted and day number in ink with a blue filled circle for
  today. All-day and due items in a header band; timed events as 15 % tinted blocks with a 3 px left bar in the
  collection colour. Overlaps share width, no overlap text clipping below 36 px height (show title only).
- **Month.** Question: "where are the busy and free days?" Cell shows day number and up to three chips, then "+N".
  Weekend cells slightly recessed; days from adjacent months at 50 % opacity; today has a blue number badge. Tasks
  are dots with a check glyph, events are tinted chips, so they differ without colour.
- **Upcoming.** Question: "what is next?" A flat list grouped by relative day (Today, Tomorrow, Thursday, Next week),
  group headers sticky and muted, each row: time (muted, fixed width), title, collection dot, optional location or
  join link as meta. Overdue first.
- **All tasks.** Question: "find and review." Dense list (28 px rows), filter bar as chips (saved filters first), group
  by collection or due date, due date right-aligned and coloured only when overdue or due today, priority as a small
  left glyph rather than a column.
- **Inbox.** Question: "what still needs a date?" Same list as All tasks, but with roomier rows (36 px), a count badge
  on the switcher, and a calm empty state ("Nothing without a date"). The primary affordance, once write support exists, is
  a date picker on the row; until then, rows are read-only.

Shared: one **now** treatment (blue), one **overdue** treatment (red text, no fill), one **selected** treatment (`AmtRaisedBrush` plus
2 px blue left bar), and a details pane that uses the same card style. Keyboard focus rings come from Fluent unchanged.

## Logo

`assets/logo.svg` (this folder) is the vector source of the three-lines mark: bars 22 / 16 / 10 wide, 5 tall, 3 apart,
radius 2, `#3b82f6`, `#10b981`, `#f59e0b`. `scripts/make-icons.py` remains the generator of `app.png`, `app.ico` and
`tray.png` ([app-icon.md](app-icon.md)); the SVG is for the site and for the design tool.

## Accessibility

Text contrast at least 4.5:1 on its surface in both variants (`mut` on `s000` is 6.9:1 light, 6.6:1 dark). Status never relies on
colour alone: overdue adds text ("Overdue 2d"), tasks add a glyph. Avalonia 12 has no high-contrast theme variant, so the `Amt*` keys define Light and Dark only; a high-contrast mapping stays open until Avalonia exposes one.

## Rollout

1. Add `Styles/Tokens.axaml` and `Styles/Controls.axaml`, move `seg`, `tool`, `chip` onto the tokens, set the Fluent accent, migrate `EntryRow` and `EntryChip` ([#97](https://github.com/jkruza/allmytasks/issues/97), done). Shared classes: text roles `meta small title heading display section empty overdue error warning mono`; surfaces `card badge hairline-bottom hairline-left selected`; identity `dot bar chip-box`; calendar grids `cell` (+ `today`, `recessed`) and `daynum` (+ `today`).
2. Replace literal font sizes and brush lookups in views with tokens, one view per pull request.
3. Apply the per-view changes above in this order: Day ([#98](https://github.com/jkruza/allmytasks/issues/98), done), Upcoming ([#99](https://github.com/jkruza/allmytasks/issues/99), done), Week ([#100](https://github.com/jkruza/allmytasks/issues/100), done), Week, Month, All tasks / Inbox.
4. Keep `site/index.html` variables in sync with the token table (same names where possible).
5. Add the accent setting: a Settings choice (Blue, OS accent, palette colours), applied by swapping `AmtAccentColor` and the Fluent accent at runtime.

## Non-goals

A custom control template set, a third-party theme package, a dark/light switch inside the app (it follows the OS),
icon font or illustration work, or any change to data, providers or Core.

## Decisions

- **App accent is selectable in Settings.** Choices: Blue (default, `#3b82f6`), the operating system's accent colour, and the
  other colours of the collection palette. `AmtAccentBrush` and Fluent's accent follow the choice, in light and dark. The OS
  option reads the system accent where Avalonia exposes it and falls back to Blue where it does not (some Linux desktops). The
  setting is stored with the other settings ([settings.md](settings.md)). Collection colours are unaffected, and overdue
  stays red whatever the accent. Implementing it is rollout step 5.
- **Dense lists show the collection colour as a dot**, 8 px, before the title, in All tasks and Inbox. Calendar chips keep the
  3 px left bar, because there it also separates adjacent events.
