# Download section on the project site

Tracked in [#80](https://github.com/jkruza/allmytasks/issues/80). Builds on [project-site.md](project-site.md).

## Goal

The site links to the newest GitHub release and, only when it is newer than that release, the newest
prerelease. A prerelease older than the current release is never shown. If either is missing, the card says
"Not available now" and links to all releases on GitHub. The section updates by itself whenever a release is
published, edited or deleted; nobody edits `site/index.html` for a release.

## Design

- **Rendered at build time.** The page stays static, with no JavaScript and no request from the visitor's
  browser to GitHub (see project-site.md). The `Pages` workflow reads
  `GET /repos/{repo}/releases` with `gh api --paginate --slurp` and runs
  `.github/pages/render_downloads.py`, which replaces everything between `<!-- downloads:start -->` and
  `<!-- downloads:end -->` in `site/index.html` before the site is uploaded. The committed file keeps the
  "Not available now" fallback between the markers, so it is valid on its own.
- **Selection.** Drafts are ignored. The release is the newest non-prerelease by `published_at`; the prerelease
  is the newest prerelease by `published_at`, shown only if it was published after the release (or if no
  release exists). A newer release therefore hides older prereleases on its own.
- **Output.** Each card has the release name, date, a button to the GitHub release page ("Download from the GitHub
  release" or "...prerelease") and the list of its files. All text is HTML-escaped and links must be
  `https://github.com/...`, otherwise they fall back to the releases page.
- **Triggers.** Pages deploys on `release` (published, edited, deleted, unpublished), on pushes to `main` that touch
  `site/**` or `.github/pages/**`, on manual dispatch, and on a daily schedule as a safety net.
- **Why a `redeploy` job.** A `release` event runs on the release's tag, and the `github-pages` environment normally
  only accepts deployments from `main`. So on a release event the workflow does not deploy; a small job runs
  `gh workflow run pages.yml --ref main` (permission `actions: write`; the event is exempt from the rule that
  `GITHUB_TOKEN` events do not start workflows) and the dispatched run deploys from `main`.
- **Release events and drafts.** `release.yml` creates a *draft*. Drafts fire no event; publishing it by hand does,
  and that is what updates the site. Releases created by a workflow using `GITHUB_TOKEN` would not start the
  Pages workflow, which does not apply here because a person publishes the draft.

## Testing

`.github/pages/test_render_downloads.py` (standard-library `unittest`) covers the selection rules, the empty
fallback, escaping and link validation. The Pages workflow runs it before every deploy.

## Not included

- Per-OS "best download" detection or direct links by platform; the file list on each card is enough for now.
- Release notes on the site.
