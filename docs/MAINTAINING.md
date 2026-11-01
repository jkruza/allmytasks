# Maintaining All My Tasks

Notes for the maintainer: how the repository is protected, how to release, and how
to add Claude automation safely later. `scripts/setup-github.sh` applies most of the
settings below. If a step fails, set it by hand here.

## Repository settings

| Setting | Value | Where |
| --- | --- | --- |
| Visibility | Public | Settings → General |
| Merge methods | Squash and rebase only; delete branches after merge | Settings → General |
| Require sign-off on web commits | On | Settings → General |
| Secret scanning + push protection | On | Settings → Advanced Security |
| Dependabot alerts + security updates | On | Settings → Advanced Security |
| Private vulnerability reporting | On | Settings → Advanced Security |
| Allowed actions | GitHub-owned only, SHA pinning required | Settings → Actions → General |
| Fork PR workflows | Require approval for all outside collaborators | Settings → Actions → General |
| Workflow permissions | Read repository contents; can't approve PRs | Settings → Actions → General |
| Pages source | GitHub Actions (site deployed by `pages.yml`) | Settings → Pages |
| Environment `release` | Required reviewer: you; only `v*` tags | Settings → Environments |
| Rulesets | `main-protection`, `protect-release-tags` | Settings → Rules → Rulesets |

GitHub renames settings from time to time; if a name doesn't match, look for the
closest equivalent.

### Rulesets

The JSON in `.github/rulesets/` is the source of truth. To change a ruleset, edit the
file in a PR and re-run the ruleset steps of `scripts/setup-github.sh`, or import the
file in Settings → Rules → Rulesets → New ruleset → Import.

- **main-protection**: PR required, 1 approval, code-owner review, approval of the
  latest push, resolved conversations, linear history, no force-push or deletion, and
  `build (windows-latest)`, `build (ubuntu-latest)`, `build (macos-latest)` must pass.
  If you rename the CI job, update the required checks or every PR will be blocked.
- **Docs-only changes skip build and test.** A `changes` job in `ci.yml` detects PRs and
  pushes that touch only `docs/**`, `*.md`, `LICENSE` or `.github/ISSUE_TEMPLATE/**`. The
  `build` jobs still run so the required checks report, but all real steps are skipped.
  Don't add `paths-ignore` to the workflow or an `if:` on the `build` job: either leaves
  the required checks pending and blocks merging. See
  [docs/design/ci-docs-only-changes.md](design/ci-docs-only-changes.md).
- Repository admins can bypass **through a pull request** only. GitHub doesn't let you
  approve your own PR, so this is how you merge your own work once checks pass.
  Contributors have no bypass.

#### Who approves what

Every PR needs an approval from `@jkruza` (CODEOWNERS `*` plus code-owner review), and
no one else's approval can replace it. GitHub never lets an author approve their own PR,
so PRs opened by Claude under your identity can't be approved by you. Use a separate
account for Claude's PRs:

1. Create a second GitHub account (e.g. a machine user) and give it **Write** access.
   Don't make it an admin and don't add it to CODEOWNERS.
2. Use that account's credentials for Claude's pushes and PRs, never your own.
3. Review and approve those PRs as `@jkruza`. This is a real approval, no bypass needed.

Claude's PRs are authored by `jkruza-ai-bot` (Write access, not a code owner).
Admin bypass-through-PR stays as a fallback for PRs you author yourself.
- **protect-release-tags**: only admins can create, move or delete `v*` tags.

### Your account

Use a passkey or hardware security key for your GitHub account. Everything above
depends on nobody else being able to sign in as you.

## Reviewing contributions

- CI for fork PRs doesn't run until you approve it. Read the diff first, especially
  anything under `.github/`, since workflow changes run with whatever they ask for.
- `.claude/`, `CLAUDE.md` and `scripts/` are code that runs on **your** machine. A PR
  could add a hook or loosen permissions that run the next time you open Claude Code on
  that branch. Read those diffs before checking out the branch locally.
- Never add a workflow that uses `pull_request_target` and checks out PR code.

## Releasing

1. Make sure `main` is green.
2. Tag and push: `git tag -s v0.1.0 -m "v0.1.0" && git push origin v0.1.0`
3. The Release workflow tests, publishes self-contained single-file builds for
   win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64, then waits.
4. Approve the `release` environment deployment in the Actions run.
5. Review the draft release it creates, then publish it.

Release builds can embed the project's Microsoft and Google OAuth clients. Store `AMT_MICROSOFT_CLIENT_ID`,
`AMT_GOOGLE_CLIENT_ID` and `AMT_GOOGLE_CLIENT_SECRET` as repository **Actions secrets** (a repository variable works as a
fallback for the two IDs); `release.yml` passes them to the publish step and prints which were embedded in each build's
job summary. Any that are missing are skipped without failing the release, and that build asks users for their own client.
Creating the clients and storing the secrets step by step: [oauth-setup.md](oauth-setup.md#maintainers-build-the-clients-into-releases).
The Google consent screen must be *In production* (and verified, for sensitive scopes) before a public release; until then
it caps users at the listed test users and expires refresh tokens after 7 days, so alpha testers are added as test users and
warned. See
[google-calendar-provider.md](design/google-calendar-provider.md#oauth-client-and-verification-a-maintainer-task-not-code).

Each release run builds, per platform (design: [packaging.md](design/packaging.md)):

- Windows: a per-user MSI and the portable zip, for x64 and arm64.
- Linux: AppImage, `.deb`, `.rpm` and the tarball, for x64 and arm64.
- macOS: tarballs of the plain executable (no `.app` bundle or dmg yet, issue #59).

x64 packages are installed and started once with `--version` in the build job (MSI on the Windows runner; deb and rpm in
Debian, Ubuntu and Fedora containers). The draft release also gets `SHA256SUMS.txt` and GitHub build provenance
attestations (verify with `gh attestation verify <file> --repo jkruza/allmytasks`). The same packages are built unsigned
by the `package` job on every pull request and on `main`, and kept as workflow artifacts for 7 days.

Binaries are not code-signed yet, so Windows SmartScreen and macOS Gatekeeper will warn. The Windows signing route
(SignPath Foundation, or a Certum open-source certificate) is still to be decided; the release workflow has a placeholder
step where signing goes.

## Project site

`site/index.html` is the public project page, published to GitHub Pages by the `Pages`
workflow (`.github/workflows/pages.yml`) whenever a change under `site/` lands on `main`.
Design: [docs/design/project-site.md](design/project-site.md).

1. One time: **Settings → Pages → Build and deployment → Source: GitHub Actions**. This is a
   repository setting, so it needs an admin; Claude's bot account can't do it.
2. Run the `Pages` workflow once from the Actions tab (or merge a change under `site/`).
   The first deployment may wait for you to approve the `github-pages` environment.
3. The site is at `https://allmytasks.jkruza.com/`. The custom domain is set in Settings → Pages
   (a DNS CNAME for `allmytasks` pointing at `jkruza.github.io`, with Enforce HTTPS on). A
   workflow deployment ignores a `CNAME` file, so there is none in `site/`.

The download section needs no manual update: publishing (or editing or deleting) a release redeploys the
site, and a daily run catches anything missed. If it looks stale, run the `Pages` workflow by hand. See
[docs/design/site-downloads.md](design/site-downloads.md).

When the README or roadmap changes provider status or build steps, update `site/index.html`
in the same PR.

## Dependencies

Dependabot opens weekly PRs for NuGet packages and for the SHA-pinned actions. Since
CI restores in locked mode, a NuGet update PR must include the updated
`packages.lock.json` files (Dependabot does this).

## Adding Claude automation later (optional)

Nothing in this repo uses a Claude credential, and that's the safest state. Claude
Code on your machine uses your own login and never touches the repo's secrets. If you
want Claude in GitHub Actions (`anthropics/claude-code-action`):

- Use an **Anthropic Console API key with a spend limit**, never your subscription's
  OAuth token. A capped key can be rotated and monitored, and a leak can't use your plan.
- Store it as a secret on a dedicated environment (e.g. `claude`) with you as required
  reviewer, not as a repository secret.
- Only trigger it for you: `if: github.actor == 'jkruza'`, or on a label only
  maintainers can apply. Never allow non-write users to trigger it.
- Give the job minimal `permissions:` and never let it push to `main`.
- Treat issue and PR text as untrusted input (prompt injection).
- Allowing the action requires adding it to Settings → Actions → allowed actions,
  pinned to a commit SHA.
