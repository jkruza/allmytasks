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
- Repository admins can bypass **through a pull request** only. GitHub doesn't let you
  approve your own PR, so this is how you merge your own work once checks pass.
  Contributors have no bypass.
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
   linux-x64 and linux-arm64, then waits.
4. Approve the `release` environment deployment in the Actions run.
5. Windows and macOS binaries are **not** built automatically. When you want them, run
   Actions → "Desktop binaries (on demand)" → Run workflow, enter the tag (for example
   `v0.1.0`) and choose windows, macos or both. Approve the `release` deployment again;
   the files are uploaded to the existing draft release.
6. Review the draft release, then publish it.

Binaries are not code-signed yet, so Windows SmartScreen and macOS Gatekeeper will warn.
macOS builds are plain executables, not `.app` bundles.

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
