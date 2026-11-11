# About dialog

A small **About** dialog that says what is running: the version, where the project lives, and
whether this build carries built-in OAuth clients. Version 1, read-only, no network access.

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

- Core, `About/`: `BuildInfo` record (`Version`, `Commit`, `Runtime`, `Clients`), `ClientStatus`
  enum (`Included`, `Incomplete`, `NotIncluded`), and a pure `ClientStatus.From(id, secret, requiresSecret)`
  with tests (blank and whitespace values count as absent). `AboutText.Format(BuildInfo)` builds the
  copy-to-clipboard text, tested to contain no client values by construction, because it never receives them.
- Providers: `GoogleFactory` and `MicrosoftFactory` already read the assembly metadata. Each gets a
  public `ClientStatus BuiltInClientStatus` property computed from what they already hold. A small Core
  interface `IBuiltInClientInfo` (`string Provider`, `ClientStatus Status`) lets the app list them without
  knowing the provider kinds. Nothing but the status leaves the factory.
- App: `AboutViewModel` (version, links, client rows, `CopyDetailsCommand`) and `AboutWindow`
  (`x:DataType`, design-system classes). Links open through `TopLevel.Launcher`, as the details pane does.
  URLs are constants in one place, shared with the update banner's fallback link.
- Version: `AppVersion.Current`. When the informational version is absent the dialog shows "unknown".

## Where it lives

Entry points: a **About All My Tasks** item in the tray menu, and a button in Settings. The Settings page
is being redesigned in the "Redesign the long Settings page" work; if that lands tabs or sections, About
becomes the last tab or section and the dialog is the same view hosted there. This doc is final on the
content and logic; the entry point is confirmed against the merged Settings layout before implementation,
and implementation starts only after that PR is merged to avoid conflicts in the Settings view.

## Security and privacy

- No client ID, secret, token, path or account name is ever rendered or copied.
- The dialog makes no network request. Links are opened only on a click, in the system browser.
- Copy details contains version, commit, runtime, OS and the three-state client status. Nothing else.

## Tests

- `ClientStatus.From`: both present, id only, secret only, blank, whitespace, Microsoft (no secret needed).
- `AboutText.Format`: contains version and status lines, never a value passed elsewhere (property test with a sentinel client ID set on the factory).
- Factory status: `GoogleFactory` and `MicrosoftFactory` constructed with default client args report the expected status.
- View model: link commands use the constant URLs; copy text matches `AboutText`.
- Manual: screenshots on Linux, light and dark.

## Docs to update with the implementation

README features list, `docs/oauth-setup.md` ("check the About dialog to see whether your build has a client"),
[settings.md](settings.md), the site's feature list, and the roadmap row.
