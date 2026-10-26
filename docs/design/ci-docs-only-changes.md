# CI: skip build and test for docs-only changes

Tracked in [#35](https://github.com/jkruza/allmytasks/issues/35).

## Goal

A change that only touches documentation should not spend runner minutes building and
testing on Windows, Linux and macOS, and should still be mergeable as soon as it is
approved. Any change that touches code still builds and tests on all three platforms.

## Constraint: required status checks

`main-protection` requires `build (windows-latest)`, `build (ubuntu-latest)` and
`build (macos-latest)`. A required check that never reports stays pending forever and
blocks the merge. That rules out two obvious options:

- **Workflow-level `paths-ignore`** (or `paths`): the workflow doesn't start, so the
  checks never report.
- **A job-level `if:` on the matrix job**: a skipped matrix job reports under the
  unexpanded name `build (${{ matrix.os }})`, not the three required names.

## Design

`.github/workflows/ci.yml` gets a `changes` job. The `build` matrix job `needs` it and
always runs, so the three required checks always report. Each real step (checkout,
setup-dotnet, restore, format check, build, test) has
`if: needs.changes.outputs.code == 'true'`. For a docs-only change those steps are
skipped, a runner starts and finishes in seconds, and the checks pass.

### What counts as docs-only

Every changed file matches one of:

- `docs/**`
- `*.md` anywhere (README, CONTRIBUTING, CLAUDE.md, ...)
- `LICENSE`
- `.github/ISSUE_TEMPLATE/**`

Anything else, including `.github/workflows/**`, `.github/pull_request_template.md`,
`.claude/**` and `.editorconfig`, counts as code and runs the full matrix.

### How it is detected

A plain `git diff --name-only` in the `changes` job, so no third-party action to pin:

- `pull_request`: `base...HEAD` (changes the PR introduces).
- `push` to `main`: `before..HEAD`.
- `workflow_dispatch`, a new branch, a force-push or any failure to compute the diff:
  treated as a code change. Detection fails safe toward building.

## Not changed

- The release workflow runs only on `v*` tags and always builds everything.
- The ruleset and its required check names stay as they are.
- The build matrix is unchanged: all three platforms for every code change.
