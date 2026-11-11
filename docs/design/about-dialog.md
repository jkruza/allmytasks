# About dialog

An **About** page in Settings that says what is running: the version, where the project lives, and
whether this build carries built-in OAuth clients. Version 1, read-only, no network access. Implemented.

## Goals

- Show the **current version** (the same string the [update check](update-check.md) compares).
- Link to the **project site** (<https://allmytasks.jkruza.com>) and the **GitHub project**
  (<https://github.com/jkruza/allmytasks>), opened in the system browser.
- Show whether the build **contains client IDs**, per provider, so a user knows whether adding a
  Google or Microsoft source will work out of the box or needs their own client
  ([oauth-setup.md](../oauth-setup.md)).
- Be copyable: a **Copy details** button puts version, OS and client status on the clipboard for bug reports.

## Non-goals

- Showing any client ID or secret, or a hash or prefix of one. Only present / not present.
- A licence browser or third-party notices. A one-line "MIT licence" with a link to the repo's `LICENSE` is enough.
- Checking for updates from here (the banner and tray entry own that).

## Content

| Row | Source |
| --- | --- |
| App name and icon | Existing window icon and the three-lines logo |
| Version | `AppVersion.Current` (informational version without build metadata, so `0.9.0-alpha`) |
| Commit | Short commit hash from the informational version's `+` part, when present |
| Runtime | `RuntimeInformation.FrameworkDescription` and OS description |
| Website | Link to the project site |
| GitHub | Link to the repository (and an "Issues" link next to it) |
| Licence | "MIT, Jarosław Kruza and contributors" |
| Built-in clients | One line per provider, below |

### Built-in clients

| Provider | "Included" means |
| --- | --- |
| Microsoft (Outlook, To Do) | `MicrosoftClientId` assembly metadata is non-empty |
| Google (Calendar, Tasks) | `GoogleClientId` **and** `GoogleClientSecret` are non-empty |

Each line shows **Included** or **Not included**, with a short consequence: "Included: adding a Google
source just opens the browser sign-in" or "Not included: enter your own client when adding a source
(see oauth-setup.md)". A Google build with only one of id and secret shows **Incomplete** (release.yml
already warns about this case), since sign-in would fail.

Release builds inject the IDs from Actions secrets ([oauth-client-configuration.md](oauth-client-configuration.md));
local and CI builds usually have none, so "Not included" is the normal state for a developer.

## Design

Logic in Core, thin view model, as per the project conventions.

- Core, `About/`: `ClientStatus` enum (`Included`, `Incomplete`, `NotIncluded`) with the pure
  `ClientStatusRules.From(id, secret, requiresSecret)` (blank and whitespace count as absent);
  `AboutInfo` (version, short commit, runtime, OS, one `ClientLine` per provider) with `ToText()` for the
  clipboard. `AboutInfo` never receives a client value, so it cannot leak one. `ProjectLinks` holds the URLs.
- Providers: `GoogleFactory` and `MicrosoftFactory` already read the assembly metadata. Each implements the
  small Core interface `IBuiltInClientInfo` (`ClientProvider`, `BuiltInClientStatus`), so the app lists them
  without knowing provider kinds. Nothing but the status leaves the factory.
- App: `SettingsViewModel.About` (an `AboutInfo`), the About `TabItem` in `SettingsWindow.axaml`, and
  click handlers in the code-behind for the three links and Copy details. `AppVersion` also exposes the
  short commit from the informational version.
- When the informational version is absent the page shows "unknown".

## Where it lives

An **About** page, the fifth and last page of the Settings side navigation
([settings-layout.md](settings-layout.md)). It is a page, not a separate window: Settings already has
the navigation, scrolling and Close button, and the content is short. Links open in the system browser
through `TopLevel.Launcher`; **Copy details** uses the clipboard.

## Security and privacy

- No client ID, secret, token, path or account name is ever rendered or copied.
- The dialog makes no network request. Links are opened only on a click, in the system browser.
- Copy details contains version, commit, runtime, OS and the three-state client status. Nothing else.

## Tests

- `ClientStatus.From`: both present, id only, secret only, blank, whitespace, Microsoft (no secret needed).
- `AboutInfo.ToText`: version, commit and status lines in provider order; unknown version; split of the informational version.
- Factory status: `GoogleFactory` (both, id only, secret only) and `MicrosoftFactory` report the expected status.
- Manual (not done from the cloud): open Settings > About in light and dark.
