# Daily automatic prerelease

Issue: [#127](https://github.com/jkruza/allmytasks/issues/127).

## Goal

Testers get a fresh alpha every morning when something changed, without the maintainer tagging by hand.

## Behaviour

Every day at 08:00 local time (CET on the maintainer's machine) `scripts/nightly-prerelease.sh` runs:

1. Fetch `main` and the `v*` tags.
2. Find the latest tag by semver (a prerelease sorts below the same stable version).
3. If `main` has no commits after that tag, do nothing. Since `main` only takes squash-merged PRs, a commit is a merged PR.
4. Otherwise bump the minor version, reset patch to 0 and always add `-alpha`:
   `v0.8.0-alpha` becomes `v0.9.0-alpha`, `v0.7.1-alpha` becomes `v0.8.0-alpha`, `v1.2.0` becomes `v1.3.0-alpha`.
5. Create a signed annotated tag on the tip of `main` and push it. That starts `release.yml`.

It stays manual on purpose: the `release` environment still needs the maintainer's approval, and the workflow creates a
**draft** that the maintainer publishes as a pre-release. The tag is the only thing automated, so no new token or workflow
permission is added; the tag is pushed with the maintainer's own git credentials (only admins may create `v*` tags,
see `protect-release-tags`).

## Running it

A systemd user timer (`scripts/systemd/`) with `Persistent=true`, so a missed 08:00 runs at the next boot. User lingering must
be on so it runs while logged out. Output goes to the journal. `--dry-run` prints what would happen without tagging.
`scripts/test-nightly-prerelease.sh` tests the version logic against throwaway repositories. A failed run (for example a
signing key that needs a passphrase the agent doesn't hold) pushes nothing and shows as a failed unit.
