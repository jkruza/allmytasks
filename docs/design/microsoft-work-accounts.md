# Microsoft work and school accounts

Tracking issue: [#118](https://github.com/jkruza/allmytasks/issues/118). Research item, not scheduled for a version (moved from version 1 by the maintainer, 2026-11-05).

Status: design only, a research item (label `research`, no milestone) that is not scheduled for any version. Nothing here is implemented, and none of it has been tried against a work tenant.

The Microsoft source (kind `microsoft`: Outlook calendar and Microsoft To Do) signs in **personal accounts only**
today. This doc says what it takes to also sign in **work or school accounts** (Microsoft Entra ID, Microsoft 365):
the sign-in change, the app registration, what tenant administrators will see and have to do, new failure modes, and
how we test. It extends, and does not replace,
[outlook-calendar-provider.md](outlook-calendar-provider.md#microsoft-identity-and-graph-client-shared-with-to-do)
(sign-in, tokens, Graph client) and [microsoft-todo-provider.md](microsoft-todo-provider.md). It supersedes the
section "Work and school accounts (not in v1)" in the first of those; that section is rewritten to point here.

## Summary

- The Graph sync code is **the same** for both account types. The same endpoints, scopes and delta queries work.
- What changes: the sign-in authority (`consumers` becomes configurable), the app registration, error mapping, a
  small amount of UI, and documentation for people who are not administrators of their tenant.
- **The main risk is admin consent.** Microsoft's default user-consent policy does not allow users to consent to
  `Calendars.Read` or `Tasks.Read`. In most tenants an administrator has to approve the app once before any employee
  can use it, **even if the app is publisher verified**. We cannot fix this in code. We make it survivable: a clear
  error, a ready-made admin-consent link, a short guide to forward to IT, and two fallbacks.
- Everything stays read-only and local. No new scopes, no backend, no project-owned server.

## Scope

If and when this is built:

- Work or school accounts in the existing `microsoft` source, for Outlook calendar and To Do, read-only.
- Personal accounts keep working, with no migration. One shared project app registration serves both.
- Several accounts at once, mixed freely (one source each, as today).
- The same scopes as today: `User.Read`, `Calendars.Read`, `Tasks.Read`, `offline_access`. Least privilege is kept;
  see [Scopes](#scopes-and-why-we-do-not-ask-for-less).
- A self-registered app: someone whose organization will not approve the shared app can register their own
  (single-tenant) app in their tenant and enter its id and tenant in the source.

Not in this feature (decisions, not oversights):

| Not included | Why / where it goes |
| --- | --- |
| Microsoft Planner tasks | Different Graph API and permissions (Planner needs group access that is more sensitive than `Tasks.Read`). Likely to matter to work users; candidate for version 2 |
| Shared, delegate, room and Microsoft 365 group calendars | Already out of v1 ([outlook-calendar-provider.md](outlook-calendar-provider.md#scope)); `Calendars.Read.Shared` is a bigger ask for admins |
| Windows single sign-on broker (WAM) | Would fix some conditional-access cases ([below](#conditional-access-and-device-policies)); optional later |
| National clouds (US Government, China) | Different authority and Graph host; unchanged from the personal design |
| Exchange on-premises mailboxes | Graph serves Exchange Online only. Shown as a clear message |
| Writing | Version 2, [write-support.md](write-support.md) |
| In-app "Sign in" button and progress view | Version 2, [#116](https://github.com/jkruza/allmytasks/issues/116); see [Known limits](#known-limits) |

## Sign-in changes

### Authority

Today `MicrosoftAuth` hard-codes `https://login.microsoftonline.com/consumers/...`. It becomes a per-source value,
the **authority segment**:

| Value | Who can sign in | Needs the app registered as |
| --- | --- | --- |
| `consumers` | Personal accounts only | Personal only, or any directory + personal |
| `organizations` | Work or school accounts only | Any directory (+ personal also works) |
| `common` | Both; Microsoft decides from the account entered | Any directory **and** personal |
| a tenant GUID or verified domain | One organization only | Single-tenant app in that tenant, or any multi-tenant app |

The source form gets one new choice, **Account type**, and one advanced text field, **Directory (tenant)**:

| Account type (form) | Authority used | Notes |
| --- | --- | --- |
| Any Microsoft account (default for new sources) | `common` | One sign-in page for personal and work. Works with the shared project app |
| Personal account | `consumers` | For self-registered apps that were created as "personal only" |
| Work or school account | `organizations` | Hides the personal option |
| Directory (tenant) filled in | that tenant | For a single-tenant self-registered app. Overrides the choice above. Validated as a GUID or a domain name; anything else is rejected before any request |

`FieldKind` has no "choice" kind today. Either add one (small, generic change, useful elsewhere) or use a text field
that accepts `common`, `consumers`, `organizations`, a GUID or a domain. The first is preferable; the implementation
PR decides.

**Existing sources need no migration.** A source saved before this feature has no account-type value; a missing value
means `consumers`. Keeping `consumers` for old sources means their stored refresh tokens are redeemed exactly as before, so no existing
user is asked to sign in again. (Whether a `consumers` token could also be redeemed at `common` is not needed to know
and is not relied on.)
A user who wants a work account adds a new source.

The authority is part of the stored source config (not a secret). The token endpoint and the authorize endpoint
are both derived from it. Host pinning stays: only `login.microsoftonline.com` is ever contacted for sign-in and
only `graph.microsoft.com` for data.

Redirect URI, PKCE, the loopback listener, state check and the "browser opens at most once per source instance" rule
are **unchanged**.

### Prompts

Add `prompt=select_account` to the authorize request when the account type is `common` or `organizations`. Without it
the browser silently signs in with whichever account it last used, which is wrong for people who keep a work and a
personal account in the same browser. For `consumers` nothing changes.

### Account label

With several work accounts, the Sources list needs more than the display name the user typed. After the first
successful sign-in the source reads `GET /me?$select=userPrincipalName,mail` and stores a short, non-secret
**account label** (the sign-in name) in the source's config so the list can show "Work - Contoso" style text.

- The label is personal data, so it is **never logged**, same rule as the account name today.
- It is stored with the source, deleted with the source, and shown only in the Sources list and the source editor.
- This resolves the "account name is not shown" known limit for all Microsoft sources, not only work ones.

Whether to also show the tenant name (`GET /organization`) is not needed in a first version; the sign-in name's domain is enough.

### Scopes (and why we do not ask for less)

| Scope | Why | Admin consent |
| --- | --- | --- |
| `User.Read` | Account label | Not needed (low impact, user-consentable in every default policy) |
| `Calendars.Read` | Events | Not "admin only" in the registration, but **blocked for user consent by Microsoft's default policy** |
| `Tasks.Read` | To Do | Same |
| `offline_access` | Refresh token | Not needed |

Alternatives considered:

- **`Calendars.ReadBasic`** drops the event body and attachments, but it is on the same default block list, so it
  gives the admin no easier decision and costs us the notes field. Not worth it.
- **`.Shared` variants** are broader, so no.
- **Asking for only the scopes of the enabled features** is already the design (a calendar-only source requests no
  `Tasks.Read`). That matters for work: an administrator can approve calendar only, and a tenant that blocks tasks
  does not stop calendar users.

## Tenant administrator consent (the main risk)

### What happens in a tenant, by policy

Facts from Microsoft's documentation, checked 2026-11-05:

- Microsoft's default consent setting ("Let Microsoft manage your consent settings") lets users consent to most
  delegated permissions **except** a list that includes `Calendars.Read`, `Calendars.ReadBasic`, `Calendars.Read.Shared`,
  `Tasks.Read`, `Tasks.Read.Shared` and the mail, files and chat read permissions. Source:
  [Manage app consent policies](https://learn.microsoft.com/entra/identity/enterprise-apps/manage-app-consent-policies#microsoft-recommended-current-settings).
- The older option "Allow user consent for apps from verified publishers, for selected permissions" works only for
  permissions an administrator has **classified as low impact**. Calendar and task read are not classified by default.
- A tenant that allows "all apps" (the legacy setting) lets users consent unaided, but that setting is discouraged
  and is not the default for new tenants.
- A tenant that disables user consent entirely needs an administrator for every new app.

So the realistic cases for an employee who tries to add a work account:

| Tenant setting | What the employee sees | What fixes it |
| --- | --- | --- |
| Default (Microsoft-managed) | "Need admin approval" (the sign-in ends with `AADSTS90094`) | An admin approves the app once for the whole tenant |
| Admin consent workflow enabled | "Request approval"; the request goes to reviewers (`AADSTS90095` interrupt) | A reviewer approves it, then the employee signs in again |
| Verified publisher + admin classified the two scopes as low | Consent screen with a blue verified badge; user consents | Publisher verification (below) |
| Legacy "all apps" | Consent screen; user consents | Nothing |
| User assignment required on the app's enterprise object, or the app blocked by policy | `AADSTS50105` or a block page | Admin assigns the user or allows the app. A policy-level block means: use the fallbacks |

Even when users can consent, an **unverified multi-tenant app** shows a prominent "unverified" warning, and where
risk-based step-up consent is enabled, users cannot consent to it at all (see
[Publisher verification](https://learn.microsoft.com/entra/identity-platform/publisher-verification-overview)).

**Conclusion:** admin approval will be needed in most corporate tenants. The product has to make that a five-minute
task for the employee and the admin, not a dead end.

### What the project does about it

1. **A clear message, not an error code.** On `AADSTS90094`, `AADSTS90095`, `AADSTS65001` or `consent_required`
   the source shows "Your organization needs to approve this app before you can use it." with a **Copy admin
   link** action and a link to the IT-admin guide. We map by the error **code** only and never display or log the
   server's error text, as for all Microsoft errors today.
2. **An admin-consent link.** The app builds, for the user's tenant, the standard admin consent URL
   (`https://login.microsoftonline.com/{tenant}/v2.0/adminconsent?client_id=...&scope=...&redirect_uri=...`)
   with only the scopes of the enabled features. The employee forwards it to their administrator. The admin signs in,
   sees the permission list and the publisher, and approves for the tenant. The `{tenant}` comes from the tenant
   that the failed sign-in reported, or `organizations` when it is unknown. The redirect target is
   `http://localhost` (already registered); the admin sees a harmless "can't reach this page" after approving, which
   the guide explains.
3. **An IT-admin guide**, `docs/work-accounts-admin.md`, written for someone who has never heard of the project:
   what the app is, that it is local with no server of ours, exactly which permissions are requested and what
   each allows (read-only: your own calendars and tasks), what the admin consent link does, how to review or
   revoke it later in **Enterprise applications**, how to restrict it to a group via "Assignment required", and how
   to register an organization's own copy of the app. It also lists the app's publisher and the verification state.
   Short, factual, no marketing.
4. **Publisher verification** of the shared app (see [App registration](#app-registration-maintainer-tasks)). It
   removes the "unverified" warning, and it is the only way to be consentable at all in tenants using the
   verified-publisher policy. It does **not** by itself remove the need for admin approval in default tenants, and the
   docs must not claim it does.
5. **Fallback A: the organization's own app registration.** A tenant admin (or a developer in the tenant) registers a
   single-tenant app, grants consent, and gives employees the application id and the tenant id. They enter both in the
   source ([Authority](#authority)). Apps registered in the tenant itself are allowed by the verified-publisher
   policy, and an admin can approve their own. No project involvement, nothing unverified. This also works for
   organizations that refuse any third-party app by policy. The IT-admin guide has the steps.
6. **Fallback B: the published calendar link (`.ics`).** Outlook on the web can publish a calendar as an `.ics`
   link, added with the `ical` provider. No app, no consent, read-only snapshot with lag and no tasks. Many tenants
   disable publishing; when they do, this is not available. The guide mentions it as a calendar-only workaround.

### What we do not do

- **No stealth workarounds.** We do not reuse a Microsoft first-party client id to avoid consent
  (the personal design already rejects this; Microsoft blocks it with `AADSTS65002` and it would be impersonation).
- **No device-code flow to dodge browser policy.** It would not avoid consent and some tenants block it.
- **No attempt to create an app in the user's tenant automatically.** Needs high privileges and is intrusive.

## App registration (maintainer tasks)

These are manual steps for the project owner; none of it is code. The shared registration already exists
(multi-tenant, owned by Jarek). Needed for work accounts:

1. **Supported account types** must be *Accounts in any organizational directory and personal Microsoft accounts*
   (`AzureADandPersonalMicrosoftAccount`). If the current registration is "any directory" only, personal sign-in
   needs this change as well; verify first, the portal's wording differs between versions.
2. **Redirect URI** `http://localhost` as *Mobile and desktop applications* (unchanged). Needed by the admin-consent
   link too.
3. **API permissions** declared in the registration: `User.Read`, `Calendars.Read`, `Tasks.Read`, `offline_access`.
   Declaring them is what lets the admin consent page list them; the app still requests per-feature at sign-in.
4. **Branding:** name, logo, a privacy statement URL and terms URL (the project site can host them), and a
   **publisher domain**. A verified custom domain is needed for the next step; `allmytasks.jkruza.com` or the parent
   `jkruza.com` is the candidate. Verifying the domain means serving
   `/.well-known/microsoft-identity-association.json` from it; the Pages site must publish that dotfolder
   (Jekyll-style processing skips dot-directories, so check how `site/` is built).
5. **Publisher verification** (optional but strongly recommended). Requirements from Microsoft:
   a verified **Microsoft AI Cloud Partner Program (Partner Center) account** for an organization, whose primary
   contact email domain matches the app's publisher domain; the app registered in a **work or school (Entra) tenant**,
   not under a personal Microsoft account; that tenant associated with the partner account; and the person doing it
   holding the right roles in both. See
   [Mark your app as publisher verified](https://learn.microsoft.com/entra/identity-platform/mark-app-as-publisher-verified).
   **Open concern:** Partner Center verifies a *legal business*. Whether an individual open-source maintainer
   without a company can complete it is not established here and must be checked by Jarek (see [Decisions](#decisions-for-the-maintainer)).
   If it cannot be done, the project ships unverified: the consent page shows the warning, more tenants will refuse
   it, and Fallback A becomes the main route for managed organizations.
6. Keep the client id out of the repository as today: it is injected at release build
   (`AMT_MICROSOFT_CLIENT_ID`, [oauth-client-configuration.md](oauth-client-configuration.md)). **No `release.yml`
   change is needed**, because personal and work accounts use the same app id.

Rotation or loss of the registration affects all users at once; the personal design already accepts this.

## New failure modes and messages

All are `SourceAuthException` ("needs attention") unless noted. Mapping is by error code or HTTP status, never by
the server's text, and messages never contain tokens, codes or account names.

| Signal | Meaning | Message to the user |
| --- | --- | --- |
| `AADSTS90094`, `AADSTS65001`, `AADSTS90095`, `consent_required` | Admin approval needed | "Your organization needs to approve this app." + admin link + guide ([above](#what-the-project-does-about-it)) |
| `AADSTS65004` / `access_denied` at sign-in | User declined | "Sign-in was cancelled." (unchanged) |
| `AADSTS50105` | App needs user assignment and this user has none | "Your organization has not given you access to this app. Ask your IT administrator." |
| `AADSTS53003`, `AADSTS530035` | Blocked by conditional access or security defaults | "Your organization's policy blocks this sign-in." No retry loop |
| `AADSTS53000`, `AADSTS53001`, `AADSTS53002` | Compliant, domain-joined or approved-app device required | "Your organization only allows sign-in from managed devices or approved apps." See below |
| `AADSTS50076`, `AADSTS50079`, `AADSTS50078` | Multifactor required or expired | Re-sign-in prompt: "Sign in to Microsoft again." (the interactive flow completes MFA) |
| `AADSTS700016`, `AADSTS50020`, `AADSTS70002` | App not found in tenant, account not allowed by the app's audience, or personal-only app used with a work account | "This app registration does not accept this kind of account. Check the account type or use your own app." Shown at sign-in, helps people with a self-registered personal-only app |
| Graph 404 `MailboxNotEnabledForRESTAPI`, `MailboxNotSupportedForRESTAPI` | Mailbox on-premises, or no Exchange Online licence | `SourceException`: "This account has no Exchange Online mailbox." (already mapped; tenant-specific wording added) |
| Graph 403 on `/me/todo/...` while calendar works | To Do not available for this user | Per-feature failure, see below |
| `invalid_grant` on refresh | Password change, revoked sessions, sign-in frequency, 90-day idle expiry | "Sign in to Microsoft again: edit this source and save it." (unchanged) |

### One feature failing must not fail the other

A work account may have a calendar but no usable To Do, or the reverse (licence, policy, an admin who approved only
one scope). Today a failure in one half fails the source. For work accounts the Microsoft source reports
**per-feature health**: if calendar sync succeeds and To Do answers 403/404 for a licence or policy reason, the
calendar data stays, the source shows a warning for "To Do", and the next sync tries To Do again at the normal
interval. The warning text names the feature, not the account. This follows the existing warning mechanism
(for example a calendar answering 404 keeps its cache and adds a warning).

### Conditional access and device policies

Conditional access can require a compliant or joined device, an approved client app, MFA at a fixed interval, or a
location. A sign-in in the **system browser** satisfies MFA. Device-based rules are different: they need the
browser to present the device identity. On a managed Windows machine this works when the default browser carries the
Microsoft single sign-on integration (Edge does; other browsers need Microsoft's extension). On unmanaged, Linux or
macOS machines it often cannot be satisfied, and the user sees `AADSTS53000`-family blocks.

We report these clearly and stop; we do not try to bypass them. The broker (WAM) on Windows would improve the
managed-device case. It remains the optional later item in
[outlook-calendar-provider.md](outlook-calendar-provider.md#open-questions) and is not part of this feature.

### Token lifetime

Refresh tokens for work accounts are typically valid for 90 days of inactivity, but a conditional-access
**sign-in frequency** policy can force a new interactive sign-in every few hours or days. Each such expiry turns the
source into "needs attention" until the user edits and saves it. See [Known limits](#known-limits).

## Data from work calendars and tasks

- **Sensitivity.** Events marked Private/Confidential carry `sensitivity`. For the signed-in user's own calendar Graph
  returns the full event, so the app shows what the user can already see in Outlook. No special handling at first;
  the model has no sensitivity field, so nothing is hidden or highlighted.
- **Teams meetings.** `onlineMeeting.joinUrl` and `onlineMeetingProvider` are already mapped to `MeetingUrl`
  ([meeting-url-and-attendees.md](meeting-url-and-attendees.md)). Work calendars are where this matters most; the
  mapping needs no change but needs a live check.
- **Free/busy-only entries.** Calendars shared into the user's mailbox that expose only free/busy are shared calendars,
  out of scope.
- **Employer data on a personal machine.** Events and tasks are cached unencrypted in the local database like any
  other source ([storage-and-sync.md](storage-and-sync.md)). Removing the source deletes its cache and its refresh
  token (existing behaviour). The IT guide states plainly where data lives, so an organization can judge its own data
  policy. The app does not apply or check any such policy.
- **Revocation.** Removing a source does not revoke the grant at Microsoft, as for personal accounts. Employees see
  and remove it at *My Apps* (`myaccount.microsoft.com`); administrators in **Enterprise applications**. Both are in
  the guide.

## Testing

The personal provider was verified on real accounts. Work accounts **have not been tried at all** and cannot be
without a tenant that has an Exchange Online mailbox. An Entra tenant alone is not enough: calendars and To Do need a
mailbox licence.

Options for a test tenant (maintainer to pick):

- A **Microsoft 365 Developer Program** sandbox, if the account is eligible; the programme's eligibility rules have
  changed over time, so verify before relying on it.
- A trial of a Microsoft 365 Business plan (time-limited), enough for a few test users.
- Any real work account that the maintainer is allowed to use, for a read-only check.

Code tests (no network, fixtures, in `AllMyTasks.Core.Tests` or the Microsoft provider tests as for the existing code):

- Authority selection from config, including a missing value on old sources (`consumers`), tenant GUID and domain
  validation, and rejection of anything else (no request is made).
- Authorize URL: `prompt=select_account` only for `common` and `organizations`; host pinned.
- Error mapping table above: each `AADSTS` code lands on the right exception and message; no server text, token or
  account name appears in messages or logs.
- Admin-consent link: correct tenant, only the enabled features' scopes, URL-encoded, `https` and host pinned.
- Per-feature health: calendar OK + To Do 403 keeps calendar data and sets one warning; the reverse; both failing.
- Account label stored and never logged.

**Unverified until a work tenant is available** (documented as such in the README and the source form while true):

1. Real consent behaviour in a default tenant, with and without the admin consent link and the consent workflow.
2. `calendarView/delta` and `todo/lists/{id}/tasks/delta` against Exchange Online (the personal service and
   Exchange Online are not guaranteed to behave identically; compare the open points in the personal design).
3. Time zone handling for Exchange Online events with Windows time zone names in `originalStartTimeZone`.
4. Throttling behaviour (`429`) on a busy tenant.
5. The flagged-email list and shared To Do lists for work accounts.
6. Conditional-access flows on a managed Windows device.

## Implementation plan

Follow-up work, after this design is accepted. Small, independent steps; each ships with its docs.

1. **Authority and form.** Per-source authority, account-type choice (new `FieldKind` or text), tenant field,
   validation, `prompt=select_account`; old sources keep `consumers`. Tests as above.
2. **Errors, per-feature health, account label.** Mapping table, admin-consent link builder, warning text, `/me`
   label. Tests as above.
3. **Docs.** Update `outlook-calendar-provider.md` (replace the "not in v1" section and the "personal accounts"
   wording), `microsoft-todo-provider.md`, `oauth-setup.md` (registration with *any directory and personal*,
   publisher verification, tenant field, rewrite the "Work and school accounts (not supported yet)" section),
   `product-scope.md`, `roadmap.md`, `README.md`, `docs/design/README.md`, and add `docs/work-accounts-admin.md`.
   Steps 1 and 2 update the docs they touch; the admin guide may land with step 2.
4. **Maintainer tasks** (parallel, no code): change supported account types, publisher domain, decide on
   publisher verification, find a test tenant.
5. **Live check** in a work tenant, then remove the "not tried on a work account" wording.

No new package, no new project, no `.github/` change.

## Known limits

- **No in-app sign-in button** (version 2, [#116](https://github.com/jkruza/allmytasks/issues/116)). For personal accounts
  re-signing in by editing the source is tolerable. For work accounts with short sign-in frequency it will be
  annoying, and the "needs attention" state and the message are the only guidance. Raising the priority of #116 is
  worth considering if live testing shows frequent expiry.
- **Admin approval is outside our control.** Some organizations will never approve a third-party app. Those users can
  use their own tenant app (Fallback A), or the `.ics` fallback for calendars.
- **No tenant-wide view.** The app shows one signed-in user's data, never other users' or any directory data.
- **Single-user device assumption** is unchanged: the refresh token lives in the OS secret store of the user.

## Decisions for the maintainer

| # | Question | Recommendation |
| --- | --- | --- |
| 1 | Pursue **publisher verification**? Needs a verified organization in Partner Center and a verified domain | Yes if Jarek can get a Partner Center organization account (a legal entity may be required); otherwise ship unverified and say so honestly in the guide |
| 2 | Which domain is the **publisher domain**? | `allmytasks.jkruza.com` or `jkruza.com`, whichever the Partner Center contact email can match |
| 3 | Default **Account type** for new sources | *Any Microsoft account* (`common`); old and self-registered personal-only apps pick *Personal* |
| 4 | Test tenant source | Developer sandbox if eligible, else a short Business trial |
| 5 | Is **Planner** wanted in version 2? | Raise as a separate item once work accounts are live; do not widen scopes now |
| 6 | Show the account label for personal sources too? | Yes; same mechanism, same privacy rules |

## Sources

- [Overview of user and admin consent](https://learn.microsoft.com/entra/identity/enterprise-apps/user-admin-consent-overview)
- [Manage app consent policies](https://learn.microsoft.com/entra/identity/enterprise-apps/manage-app-consent-policies)
- [Configure how users consent to applications](https://learn.microsoft.com/entra/identity/enterprise-apps/configure-user-consent)
- [Publisher verification](https://learn.microsoft.com/entra/identity-platform/publisher-verification-overview)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/graph/permissions-reference)
- [Microsoft Entra authentication and authorization error codes](https://learn.microsoft.com/entra/identity-platform/reference-error-codes)
