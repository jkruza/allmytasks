# Contributing to All My Tasks

Thanks for helping. This guide keeps contributions easy to review.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- Be kind. This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development

You need the .NET 10 SDK (see `global.json`).

```sh
dotnet build AllMyTasks.slnx
dotnet test --solution AllMyTasks.slnx
dotnet format AllMyTasks.slnx      # CI fails on formatting differences
dotnet run --project src/AllMyTasks
```

- Logic that doesn't need the UI goes in `src/AllMyTasks.Core`, with tests.
- Package versions live in `Directory.Packages.props` (central package management).
  Don't put versions in `.csproj` files. After changing packages, commit the updated
  `packages.lock.json` files.
- Warnings are errors. Fix them rather than suppressing them.

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep each PR focused on one change, with tests where it makes sense.
3. Sign off every commit (see below).
4. Open the PR and fill in the template.

CI for pull requests from forks runs after a maintainer approves it. All checks
(Windows, Linux, macOS) must pass and a code owner must approve before merging.
PRs are squash- or rebase-merged; `main` has a linear history.

Changes to `.github/`, `.claude/`, `CLAUDE.md`, `scripts/` or build files get extra
scrutiny. Please explain why they're needed.

## Developer Certificate of Origin

By contributing you certify the [Developer Certificate of Origin](https://developercertificate.org/):
that you wrote the change or otherwise have the right to submit it under the
project's MIT license. Add a sign-off line to every commit:

```sh
git commit -s -m "Add Todoist provider"
```

This adds `Signed-off-by: Your Name <you@example.com>` to the commit message.

## AI-assisted contributions

Using AI tools is fine. You are responsible for every line you submit: make sure
you understand it, that it builds and is tested, and that it doesn't include code
you don't have the right to contribute.
