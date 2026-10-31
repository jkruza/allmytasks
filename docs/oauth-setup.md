# Setting up Microsoft and Google sign-in

Microsoft and Google only let an app read an account after the app has been **registered** with them. The repository does
not contain anyone's registration, so a build of All My Tasks either has the project's client built in, or you create your
own (free, about ten minutes) and give it to the app. This page covers both:

- [Using your own client in the app](#using-your-own-client-in-the-app): for users whose build has none built in.
- [Microsoft: register an app in Entra](#microsoft-register-an-app-in-microsoft-entra)
- [Google: create an OAuth client](#google-create-an-oauth-client-in-google-cloud)
- [Maintainers: build the clients into releases](#maintainers-build-the-clients-into-releases)

Nothing here is a secret of yours that the app uploads anywhere: the app talks only to Microsoft and Google directly, and
keeps only a refresh token, in your system keyring.

## Using your own client in the app

1. Create the client below (Microsoft, Google, or both).
2. In the app open **Sources → Add source** and choose *Microsoft* or *Google*.
3. If your build has no built-in client, the **Application (client) ID** (Microsoft) or **OAuth client ID** and **OAuth
   client secret** (Google) fields are marked required, and saving without them is refused. If the build has a client, the
   fields are optional: leave them blank to use the built-in one, or fill them in to use yours instead.
4. Save. A browser window opens for you to sign in and approve read-only access. Close it when it says you can.
5. To sign in again (the sign-in expired or was revoked), edit the source and save it.

Removing a Google source also revokes its access at Google. For Microsoft, remove the app yourself at
<https://account.live.com/consent/Manage> if you want the grant gone.

## Microsoft: register an app in Microsoft Entra

For **personal** Microsoft accounts (Outlook.com, Hotmail, Live). Work and school accounts are not supported yet. No client
secret and no publisher verification are needed.

1. Sign in to the [Microsoft Entra admin center](https://entra.microsoft.com) with a Microsoft account. If it has no
   directory yet, the portal offers to create a free one (an Azure sign-up without a subscription is enough).
2. Go to **Entra ID → App registrations → New registration**.
3. **Name:** anything, for example `All My Tasks (mine)`. It is shown on the consent screen.
4. **Supported account types:** choose **Personal Microsoft accounts only**. (If your portal only offers *Accounts in any
   organizational directory and personal Microsoft accounts*, that also works: the app always signs in against the
   `consumers` authority.)
5. **Redirect URI:** platform **Public client/native (mobile & desktop)**, value `http://localhost`. Do not add a port: for
   `localhost` Microsoft accepts any port, and the app picks a free one.
6. Click **Register**.
7. On the app's **Overview** page copy the **Application (client) ID**. It is a GUID like
   `11111111-2222-3333-4444-555555555555`. Do not copy the *Object ID* or the *Directory (tenant) ID*; they look the same
   but do not work.
8. **Authentication:** under *Advanced settings* make sure **Allow public client flows** is **Yes**, then save.
9. **API permissions → Add a permission → Microsoft Graph → Delegated permissions**, add:
   `User.Read`, `Calendars.Read`, `Tasks.Read`, `offline_access`. (`openid` and `profile` come with the defaults.) Personal
   accounts consent for themselves, so there is no *Grant admin consent* step.
10. Paste the client ID into the app, or give it to the build ([below](#maintainers-build-the-clients-into-releases)).

Check: adding the source opens a Microsoft sign-in page that names your app and lists "Read your calendars", "Read your
tasks" and "Maintain access to data you have given it access to". If the page says *unauthorized_client* or *the
application is not configured for personal accounts*, step 4 is wrong. *AADSTS50011* (reply URL mismatch) means step 5 is
wrong.

## Google: create an OAuth client in Google Cloud

1. Open the [Google Cloud console](https://console.cloud.google.com) and create a project (**Select a project → New
   project**), for example `All My Tasks`. No billing is needed.
2. **APIs & Services → Library:** enable the **Google Calendar API** and the **Google Tasks API**.
3. **Google Auth Platform** (older consoles: *APIs & Services → OAuth consent screen*). Click **Get started** and fill in:
   - **App name** and **User support email**.
   - **Audience:** *External* (unless the project is inside a Google Workspace organization and only its members will use
     it, then *Internal* needs no further steps and no 7-day limit).
   - **Contact information:** your email.
4. **Data Access → Add or remove scopes**, add these three and save:
   - `https://www.googleapis.com/auth/calendar.calendarlist.readonly`
   - `https://www.googleapis.com/auth/calendar.events.readonly`
   - `https://www.googleapis.com/auth/tasks.readonly`

   (If you only want one service you can add only its scopes; the source's Calendar and Tasks switches choose what is
   asked for.)
5. **Audience → Test users → Add users:** add every Google account that will sign in, **including your own**. While the app
   is in *Testing* only these accounts can sign in, up to 100.
6. **Clients → Create client:** Application type **Desktop app**, any name. Do not choose *Web application*: the app signs in
   through a loopback address `http://127.0.0.1:<port>`, which only Desktop clients accept without registering a redirect.
7. Copy the **Client ID** (ends in `.apps.googleusercontent.com`) and the **Client secret**. For a desktop app Google says
   the secret is not confidential, but the app still keeps it in the keyring and you should not commit it.
8. Paste both into the app, or give them to the build ([below](#maintainers-build-the-clients-into-releases)).

Things you will meet:

- **"Google hasn't verified this app"** is expected while the app is in Testing. Click *Advanced → Go to <app name>
  (unsafe)*; it is your own app.
- **Sign-in stops working after 7 days.** In Testing, Google expires refresh tokens after a week. Edit the source and save
  to sign in again, or publish the app (**Audience → Publish app**). A published app that uses these scopes is *unverified*:
  it shows the same warning and is limited to 100 users until Google verifies it (privacy policy, home page on a verified
  domain, demo video). That is a maintainer task, not needed for personal use, and it removes the 7-day limit only for
  users who accept the warning.
- **`access_denied` / "app not verified for this user"**: the account is not in the test users list (step 5).
- **`invalid_client`**: the client ID or secret was pasted wrong or the client is not a Desktop app.
- **`redirect_uri_mismatch`**: the client type is not *Desktop app*.

## Maintainers: build the clients into releases

A release build can carry the project's own clients, so users need to enter nothing. Create the Microsoft app and the Google
client as above, using the project's accounts, then store them as **repository secrets**:

1. GitHub: **Settings → Secrets and variables → Actions → New repository secret**, or from a terminal:

   ```sh
   gh secret set AMT_MICROSOFT_CLIENT_ID    --repo jkruza/allmytasks
   gh secret set AMT_GOOGLE_CLIENT_ID       --repo jkruza/allmytasks
   gh secret set AMT_GOOGLE_CLIENT_SECRET   --repo jkruza/allmytasks
   ```

   (`gh` prompts for the value, so it does not end up in shell history.)
2. Push a `v*` tag as usual. `release.yml` passes the three values to the publish step, and the project files embed them
   in the assembly. Each build job summary lists which are *embedded* and which are *not set*.

Rules the workflow follows:

- **Missing values never fail a release.** An unset or empty value embeds nothing, a notice is shown, and that build asks
  users for their own client (the form marks the fields required, see above).
- The Google ID and secret are a pair: set both or neither. The workflow warns if only one is set.
- A repository **variable** of the same name works as a fallback for the two IDs (they are public). Secrets win when both
  exist. The Google secret is read from secrets only.
- The values live only in the release job's environment. Pull request builds and forks never receive them, and they are
  never written to the repository or to logs.
- Anyone can extract the values from a shipped binary. That is accepted: a client ID is public, and Google documents that an
  installed app's secret is not confidential. Rotate them in the Entra or Google console if they are ever abused, update the
  GitHub secret, and tag a new release.

For Google, the project's consent screen should be moved to *In production* and verified before announcing a public
release; see
[google-calendar-provider.md](design/google-calendar-provider.md#oauth-client-and-verification-a-maintainer-task-not-code).

### Work and school accounts (not supported yet)

Supporting them would need a second registration setting, not different code: **Supported account types** *Accounts in any
organizational directory and personal Microsoft accounts*, and **publisher verification** through a Microsoft Partner Center
(MPN) account so that organizations do not show "needs admin approval" for an unverified publisher. The app also needs its
sign-in authority changed from `consumers` to `common`. This is design only; see
[outlook-calendar-provider.md](design/outlook-calendar-provider.md#work-and-school-accounts-not-in-v1).

### Building locally with your own client

```sh
AMT_MICROSOFT_CLIENT_ID=<guid> \
AMT_GOOGLE_CLIENT_ID=<id> AMT_GOOGLE_CLIENT_SECRET=<secret> \
  dotnet run --project src/AllMyTasks
```

Leave out any you do not have. Or skip this and enter the values in the source form.
