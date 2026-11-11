# Update check

Tracked in [#139](https://github.com/jkruza/allmytasks/issues/139). **Status: implemented, version 1.**

## Goal

Tell the user when a newer release exists. The app is a fast-moving alpha with unsigned installers, so people on
old builds are common. The app does **not** download or install anything; it points to the release page.

## Constraints

- No telemetry and no backend of our own ([product-scope.md](product-scope.md#what-does-not-change)). The check is one
  anonymous HTTPS request to the public GitHub releases API for this repository, with a generic `User-Agent`
  and no identifier of the user, machine or installation.
- **Opt out** in Settings, "Check for new versions", on by default. When off, the app makes no such request.
- Never blocks start-up, never shows an error dialog; a failed check is silent and retried later.

## Design

- Check at start and then at most once every 24 hours, skipping when the last check was recent (time kept in
  settings). Use conditional requests (`ETag`) so most checks are free.
- Compare the running version with the newest release. Prereleases count only if the running build is itself a
  prerelease (every release so far is `-alpha`); a stable build ignores prereleases.
- If newer: a quiet banner "Version 0.10.0-alpha is available" with **View release** (opens the release page in the
  browser) and **Skip this version**. The tray menu shows the same item. Nothing is shown again for a skipped
  version.
- The release notes are not rendered in the app.

## Core changes

`IReleaseChecker` and a `ReleaseVersion` comparer (SemVer with prerelease labels) in Core, with an
`HttpClient`-based implementation behind an interface so tests use a fake. The ETag and skipped version are
settings.

## Testing

Version ordering with prerelease labels, prerelease-vs-stable policy, skip, 304, rate limit (403) and offline
(silent), opt-out makes zero requests, 24-hour throttle with a fake `TimeProvider`.

## Open questions

- Linux packages come from the same release page, so the link works for all OSes. Package-manager users may prefer no
  prompt; the opt-out covers that.
- Once installers are signed ([#115](https://github.com/jkruza/allmytasks/issues/115)), is a self-updater wanted?
  Proposal: separate decision, not part of this.

## Implementation notes

- Core, `Updates/`: `ReleaseVersion` (SemVer with prerelease labels, build metadata ignored), `IReleaseChecker` and
  `GitHubReleaseChecker` (one anonymous `GET` of the repository's release list; no token, cookies or redirects,
  `User-Agent: AllMyTasks`, 1 MB body cap, 15 s timeout, drafts ignored, only release pages inside this repository are
  accepted as links), and `UpdateCheckService` (opt-out, 24-hour throttle, `ETag`, skipped version, silent failure).
- The state is one JSON value under the settings key `update.check`: time of the last check, the `ETag`, the release
  list from the last answer (so a `304` still shows the banner) and the skipped version. Skipping a version also hides
  every older one.
- The opt-out is `AppSettings.CheckForUpdates` (`checkForUpdates`, default on), under **Updates** in Settings. Turning
  it off hides the banner and makes no request; turning it on checks at once.
- App: `UpdateBannerViewModel` and a banner above the view with **View release** and **Skip this version**; the tray
  menu gets a matching "Version X is available: view release" item while an update is offered. The running version
  is the assembly's informational version without build metadata (`AppVersion`).
- The check runs once at start-up in the background and again whenever settings change; the 24-hour throttle makes the
  second call free. A running app that stays open for days checks again at the next start-up or settings change; a
  timer is not added because the banner is a convenience, not a guarantee.
- Tests: `UpdateCheckTests` (version ordering, parsing, prerelease policy, skip, throttle, `304`, `403`, `429`, offline,
  opt-out makes zero requests, stored state survives a restart, request headers have no credentials).
- Development builds report `0.1.0`, so they are offered every release unless the check is switched off.
