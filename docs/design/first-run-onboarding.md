# First-run onboarding and empty states

Tracked in [#135](https://github.com/jkruza/allmytasks/issues/135). **Status: implemented, version 1.**

## Goal

A new user who starts the app with no sources should reach "I can see my first events or tasks" without reading the
docs. Today the app is useful only for someone who already knows that a source has to be added first, and which token
or sign-in each provider needs.

## Design

- **Welcome state**, shown instead of the calendar while no source is configured: one sentence on what the app does
  (read-only overview, data stays on this machine) and one primary button, **Add your first source**. A secondary
  link opens the sample-data option if it exists in the build, so people can look around before signing in.
- **Guided add-source**: the existing Add source dialog ([storage-and-sync.md](storage-and-sync.md), source
  schema) gains a short "what you need" line per provider, shown above the fields, for example "Todoist: an API token
  from Settings > Integrations > Developer" or "GitHub: a personal access token with read access". Text lives in Core
  next to the factory schema, so the dialog stays generic.
- **Checklist** on the welcome state until the first successful sync: sources added, first sync done, optional
  "turn on reminders", optional "start at login". Items tick themselves; the checklist hides for good once all
  mandatory items are done or when the user dismisses it.
- **Empty states per view** use the wording already defined in [views.md](views.md) and [inbox-view.md](inbox-view.md);
  this doc only adds the "no source yet" and "first sync running" variants.
- **Read-only promise** is stated once on the welcome state, because it is the reason a read-only token is enough.

## Core changes

`IOnboardingState` in Core (no Avalonia): computes the checklist from the source list, the last sync status and
settings. The `dismissed` flag is stored in settings ([settings.md](settings.md)). View models only bind to it.

## Testing

Checklist states for: no sources, one source syncing, one source failed, one source synced, dismissed. The
per-provider help text exists for every registered kind (a test fails when a new provider forgets it).

## Implementation notes

- `OnboardingState.Compute(sources, statuses, settings)` in Core is a static function rather than an interface; the
  view model (`OnboardingViewModel`) only binds to its result. The sample source does not count as "source added".
- Sample data is no longer added automatically on first run. The welcome state offers "Look around with sample data",
  which adds the sample source through the registry and the normal sync path. Remove it from Sources at any time;
  removing the last source shows the welcome state again.
- Every `ISourceFactory` must supply `SourceConfigSchema.WhatYouNeed` (required parameter). Tests per provider check
  the text is present, so a new provider cannot forget it.
- Setting: `OnboardingDismissed` (default off).

## Open questions

- Offer sample data in release builds? Proposal: yes, clearly marked and removable in one click.
- Should the welcome state suggest providers in a fixed order or by what the OS account suggests (no)? Proposal:
  fixed order, calendars and tasks grouped.
