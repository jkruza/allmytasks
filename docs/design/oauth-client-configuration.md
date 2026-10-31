# OAuth client configuration (Microsoft and Google)

Tracking issue: [#65](https://github.com/jkruza/allmytasks/issues/65).

Follow-ups left over from the [Outlook](outlook-calendar-provider.md) and [Google](google-calendar-provider.md)
providers. Microsoft and Google only issue tokens to a registered OAuth client, and the repository ships none. This doc
settles how a client id (and for Google, a secret) gets into a build, what the app does when a build has none, and which
leftovers are done now and which are deliberately not.

## Goals

1. **Release builds embed the project's client ids**, supplied by the GitHub Actions release workflow from repository
   settings. Nothing is committed.
2. **A release without those settings still builds.** The workflow notices what is missing, says so, and ships a build that
   asks the user for their own client.
3. **A build without embedded ids lets the user enter their own** at provider-settings level (the source form), with the
   form saying so up front rather than failing at the first sync.
4. **Detailed setup instructions** for Azure (Microsoft Entra) and Google Cloud, for users bringing their own client and
   for the maintainer creating the project client: [docs/oauth-setup.md](../oauth-setup.md).

## Where each value lives

| Value | Public? | GitHub setting | Build property | Reaches the app as |
| --- | --- | --- | --- | --- |
| Microsoft application (client) ID | yes, sent in every sign-in URL | repository **variable** `AMT_MICROSOFT_CLIENT_ID` | `AMT_MICROSOFT_CLIENT_ID` | assembly metadata `MicrosoftClientId` |
| Google OAuth client ID | yes | repository **variable** `AMT_GOOGLE_CLIENT_ID` | `AMT_GOOGLE_CLIENT_ID` | assembly metadata `GoogleClientId` |
| Google OAuth client secret | not confidential for desktop clients, but flagged by secret scanning | Actions **secret** `AMT_GOOGLE_CLIENT_SECRET` | `AMT_GOOGLE_CLIENT_SECRET` | assembly metadata `GoogleClientSecret` |

Jarek asked for all of them as GitHub secrets. Ids are public by design, so a variable would do, but a secret is simply
harder to leak into logs, so the workflow accepts **either**: it reads `secrets.X` and falls back to `vars.X`. The setup
doc recommends secrets for all three. (Secrets are masked in logs; variables are not.)

The values are compiled into the shipped binary, so they are extractable by anyone who downloads it. That is accepted:
Google documents that an installed app's secret is not confidential, and a client id is public. Keeping them out of the
repository only keeps forks from sharing the maintainer's registration (and quota, and consent screen).

## Release workflow

`release.yml` passes the three values to the **Publish** step as environment variables. MSBuild reads environment variables
as properties, and the project files already add the assembly metadata only when the property is non-empty, so an unset or
empty value simply embeds nothing.

A **Check OAuth client settings** step runs first in each build job. For each of the three names it writes a
`::notice::` annotation (and a line in the job summary) saying "embedded" or "not set: users must enter their own".
It never prints a value, never fails the build, and treats an empty string like a missing one. The Google id and secret are
a pair: if only one is set the step warns, because an id without its secret fails at the token endpoint, and the app
ignores a lone built-in id's missing secret by sending none.

Fork pull requests and the `ci.yml` workflow get no secrets and need none: tests inject fake ids. Releases run only on
`v*` tags pushed by an admin, in the `release`-protected pipeline, so secrets are not exposed to untrusted code.

`.github/` is code-owner protected, so this workflow change goes in the same PR but needs the maintainer's approval like
any other.

## The app without embedded ids

Today both factories already accept a per-source `clientId` (and Google a `clientSecret`) and the first sync fails with a
`SourceAuthException` if there is none. That works but the form always says "Only needed if this build has none built
in", which is true for the user only if they know what their build has.

Change: each factory checks for its embedded values when it builds its `ConfigSchema` and adapts the fields.

| Build | Client ID field | Client secret field (Google) |
| --- | --- | --- |
| No embedded value | **Required** (`*`), help text explains how to register and links the docs | **Required** |
| Embedded value | optional, help text: "This build includes a client. Leave blank to use it, or enter your own to use a different one." | optional, same |

`SourceRegistry.Validate` already enforces `Required`, so the user gets "Application (client) ID is required." when saving
instead of a sync failure later. On edit, a blank secret keeps the stored one, as for every secret field. An own Google
client id continues to pair only with an own secret (never the built-in secret), as implemented.

The Microsoft id is also checked to be a GUID when saved, so a pasted secret value or a tenant name fails early.

## Google token revoke on removal

Removing a Google source deletes the local refresh token but leaves the grant active in the user's Google account. New
optional Core interface `ISourceCleanup` (`Task OnRemovingAsync(CancellationToken)`) is called by
`SourceRegistry.RemoveAsync` before secrets are deleted. `GoogleSource` implements it by posting the refresh token to
`https://oauth2.googleapis.com/revoke`. Best effort: failures and timeouts are swallowed (logged at debug), removal always
proceeds, and no token is ever logged. Microsoft has no revoke endpoint for personal accounts' refresh tokens; the doc
tells the user to remove the app at account.live.com/consent/Manage.

## Not in this change

- **In-app sign-in / re-sign-in button and progress dialog.** Needs a provider-specific action in the schema-driven form
  and a view model for an in-flight browser sign-in. Re-sign-in stays "edit and save the source". Own design later.
- **Work and school Microsoft accounts.** Design-only, still as in
  [outlook-calendar-provider.md](outlook-calendar-provider.md#work-and-school-accounts-not-in-v1). The setup doc describes
  how a multi-tenant registration would differ, so the maintainer can prepare it, but the app stays on the `consumers`
  authority. Publisher verification is the blocker.
- **Creating the Entra app and Google Cloud project.** Maintainer tasks, described in the setup doc.

## Testing

- Factory schema tests: with an injected default id the fields are optional, without it required; Google secret likewise.
- `SourceRegistry.Validate` rejects an empty required client id.
- Microsoft GUID validation on save.
- Google revoke: request goes to the revoke endpoint with the stored refresh token and no client secret leaks into logs;
  a failing endpoint does not stop removal; `SourceRegistry.RemoveAsync` calls the cleanup before deleting secrets.
- Workflow: checked by running the step script locally with each combination of set, empty and unset variables.
