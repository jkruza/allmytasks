# Meeting links and attendees

Tracked in [#57](https://github.com/jkruza/allmytasks/issues/57). **Implemented**; Google and Microsoft accounts have
been tried, the points in [Unverified](#unverified) are not individually confirmed.

Add a join link and the people on an event to the model, fill them from the providers that have them, and show them
in the [details pane](details-pane.md). The meeting link was accepted as a small model change in the Google Calendar
design ([open question 7](google-calendar-provider.md)); attendees were left out of v1 on purpose
([domain-model.md](domain-model.md)) and the details pane says so. This brings both in as **display only**.

## Goals

- A **Join** button for events that have an online meeting (Google Meet, Teams, Zoom, Webex and similar).
- The organiser and attendees with their response status in the details pane.
- A small video marker on agenda rows of events with a meeting link.
- Same fields from every provider that can supply them.

## Non-goals

- Responding to invitations, inviting people, free/busy lookup (still a non-goal in [README.md](README.md)).
- Creating meetings.
- Resolving attendee names from contacts, avatars.
- Meeting links on tasks.

## Model

```csharp
public sealed record CalendarEvent(
    /* existing members */
    Uri? MeetingUrl,
    Attendee? Organizer,
    IReadOnlyList<Attendee> Attendees);

public sealed record Attendee(
    string? Name, string? Email, AttendeeRole Role, ResponseStatus Response, bool IsSelf);

public enum AttendeeRole { Required, Optional, Resource }
public enum ResponseStatus { NeedsAction, Accepted, Tentative, Declined }
```

- `MeetingUrl` is always an absolute `https` URL. Anything else is dropped at mapping time (see [Security](#security)).
- The service name ("Google Meet", "Microsoft Teams", "Zoom") is derived from the host when shown, not stored, so
  the list of known hosts can change without a migration.
- `IsSelf` marks the account owner's own entry, so the UI can show "You" and the existing "declined means cancelled"
  mapping keeps a single source of truth.
- Attendee lists are capped at 200 entries on mapping; the real count is kept so the pane can say "and 340 more".
- Defaults for providers without the data: `MeetingUrl = null`, `Organizer = null`, `Attendees = []`.

## Provider mapping

| Provider | Meeting link | Organiser and attendees |
| --- | --- | --- |
| Google Calendar | `conferenceData.entryPoints` with `entryPointType = video`, else `hangoutLink` | `organizer`, `attendees[]` (`displayName`, `email`, `optional`, `resource`, `responseStatus`, `self`) |
| Outlook | `onlineMeeting.joinUrl` (Graph `onlineMeeting` property; fall back to `onlineMeetingUrl`) | `organizer`, `attendees[]` (`emailAddress`, `type`, `status.response`) |
| iCalendar / CalDAV | `CONFERENCE` (RFC 7986) with `FEATURE=VIDEO`, then `X-GOOGLE-CONFERENCE`, `X-MICROSOFT-SKYPETEAMSMEETINGURL`, `X-MICROSOFT-ONLINEMEETINGEXTERNALLINK`; failing those, the first URL on a known meeting host in `LOCATION`, then `DESCRIPTION` | `ORGANIZER`, `ATTENDEE` (`CN`, `PARTSTAT`, `ROLE`, `CUTYPE`); a `mailto:` value is the e-mail |

Only the iCalendar fallback scans free text, and only for hosts on a short built-in list (`meet.google.com`,
`teams.microsoft.com`, `*.zoom.us`, `*.webex.com` and similar); an arbitrary link in a description is not a meeting link.

The Outlook and Google `Properties`/`$select` lists in the sync requests gain the new fields. Whether Graph returns
`onlineMeeting` on `calendarView/delta` or needs an expand or a second request is **not verified** (see below).

## As built

Differences from the plan above, all small:

- `CalendarEvent` also has `AttendeeCount`, the number the provider reported before the 200 cap, for "and 340 more".
- Join and Copy link use Avalonia's launcher and clipboard rather than `Process.Start`, like the existing Open link button.
  They are click-only, take only the validated `https` URL and never run from sync.
- Outlook: Graph does not say which attendee is the account owner, so `IsSelf` is set for the organiser only
  (from `isOrganizer`). The Outlook `Url` is now the event's `webLink` alone; the join link moved to `MeetingUrl`.
- iCalendar: `ATTENDEE` values that are not `mailto:` addresses keep the name and have no e-mail. `IsSelf` is never set.
- Google marks the organiser's answer as accepted when the organiser is not in `attendees`; when they are, the
  details pane shows the answer from the attendee entry.
- The pane lists the organiser first and does not repeat them among the attendees (matched by e-mail).
- Core holds the formatting (`EntryDetailsBuilder`, `PersonLine`, `MeetingLinks`), so it is tested without a UI.
  There is no app test project, so the Show all state of the view model is not covered by an automated test.

## Storage and migration

The new fields are stored in the event's existing `json` column; the query columns do not change (migration 004 only resets sync state). Rows cached before the migration have none of the
data, and incremental sync would not bring it back. So the migration clears the sync state of the iCalendar, Google
and Outlook sources once, which makes the next sync a full one. Items are replaced by the normal snapshot path.

## UI

- **Details pane:** a Join button below the title when `MeetingUrl` is set, showing the service name and host
  ("Join Google Meet, meet.google.com"). An **Attendees** section: organiser first, then required, then optional,
  with a response glyph and name (falling back to the e-mail). Collapsed after 5 with **Show all**. A copy-link action
  next to Join.
- **Agenda rows:** a small video icon when `MeetingUrl` is set. No attendees on rows.
- Where the details pane today says attendees are not shown ([details-pane.md](details-pane.md)), that note goes.
- Later, with [reminders](reminders.md): a **Join** action on the notification.

## Security

Event data is third-party input.

- Only `https` URLs are accepted; `javascript:`, `file:`, custom schemes and URLs with credentials are dropped.
- Join opens the user's default browser (`Process.Start` with `UseShellExecute` on the validated URL) only when the
  user clicks. It never opens automatically and never from a notification without a click.
- The pane shows the host next to the button so a lookalike link is visible before clicking.
- Names and e-mails are plain text, never interpreted as markup, and never logged. They are personal data of other
  people, stored in the local database like the rest of the cache; nothing leaves the machine.

## Tests

- Fixtures for each provider with and without meeting data; a Teams-in-description case; several conferencing
  entries (pick the video one).
- URL sanitiser: schemes, credentials, overlong values, IDN hosts; host-to-service-name mapping.
- iCalendar attendee parsing: parameters, `mailto:`, missing `CN`, resources, 200+ attendees.
- Migration: old rows read as no meeting data; sync state reset happens once.
- View model: Join command, collapsed/expanded attendee list, ordering.

## Docs to update with the implementation

[domain-model.md](domain-model.md) (the "left out of v1" list and the event type), [details-pane.md](details-pane.md),
[google-calendar-provider.md](google-calendar-provider.md), [outlook-calendar-provider.md](outlook-calendar-provider.md),
[ical-caldav-provider.md](ical-caldav-provider.md), [storage-and-sync.md](storage-and-sync.md), the README and the
[roadmap](roadmap.md).

## Unverified

- Which Graph property carries the Teams link on `calendarView/delta` for personal accounts (personal accounts may
  not populate `onlineMeeting`).
- Whether Google returns `conferenceData` without a `conferenceDataVersion` request parameter on reads.

## Open questions

| # | Question | Proposal |
| --- | --- | --- |
| 1 | Show attendee e-mails or names only? | Name, with the e-mail as tooltip and as fallback |
| 2 | Keep attendees of cancelled or declined events? | Yes, same mapping as other events |
| 3 | Scan `DESCRIPTION` for links on Google and Outlook too? | No; they have structured fields, and a wrong guess is worse than no button |
