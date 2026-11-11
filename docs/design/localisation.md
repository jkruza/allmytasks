# UI localisation

Tracked in [#143](https://github.com/jkruza/allmytasks/issues/143). **Status: proposed, version 2.**

## Goal

Let the UI be translated. Dates, times and the first day of the week already follow the OS culture from the first
version (roadmap open question 9, [settings.md](settings.md#week-start)); the texts do not. Polish is the first
target language because the owner and likely early users read it, English stays the source language.

## Design

- Strings move from `.axaml` and code into resource files (`.resx`) per area, English as the neutral resource,
  `pl` as the first translation. Keys are descriptive (`Sources.AddButton`), not the English text.
- Avalonia bindings read strings through a small `Loc` markup extension or a typed strings class, both compiled
  and checked at build time, consistent with compiled bindings.
- Core stays free of UI text where possible. Where Core builds text (for example `RecurrenceDescriber`,
  `EntryDetailsBuilder`) it takes an `IStringProvider`, so tests can use a fixed English provider.
- Language setting in Settings: "System default" (default) or a language list. The UI language changes on the next
  start in the first cut; live switching is a later improvement.
- Plurals ("3 more") use a small plural helper with the CLDR rules for each shipped language, not string
  concatenation. Date and number formatting continues to use the chosen culture.
- Provider text coming from services (task titles, notes) is never translated.

## Process

- English is the review source; a CI check lists keys missing in a translation as a warning, not a failure.
- Translations arrive by pull request; `CONTRIBUTING.md` gets a short section. Machine translation is not merged
  without a human check.

## Risks

Long German or Polish strings overflow the toolbar and chips, whose widths are tuned in [toolbar.md](toolbar.md);
check each translation at the minimum window width. Right-to-left languages are out of scope until asked for.

## Testing

Every key exists in English; translations contain the same placeholders as the source; plural helper per language;
a pseudo-locale (accented, 30 per cent longer) used in a manual overflow check.

## Open questions

- Start with the extraction work only (no new language) to keep the first PR reviewable? Proposal: yes, then add Polish.
