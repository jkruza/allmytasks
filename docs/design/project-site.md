# Project site on GitHub Pages

Tracked in [#41](https://github.com/jkruza/allmytasks/issues/41).

## Goal

A single public page that says what All My Tasks is, how far along it is, which providers
work, how it is built and how to build it. It is a front door for people who land on the
repository link; it is not a docs portal. The design docs stay in `docs/` on GitHub.

## Design

- **Content** lives in `site/index.html`: one hand-written static page with inline CSS and no
  JavaScript, scripts, fonts or third-party requests. It follows the OS light or dark theme.
  The page was designed in Claude Design and exported to plain HTML.
- **Publishing** is done by `.github/workflows/pages.yml`, which uploads `site/` with
  `actions/upload-pages-artifact` and deploys it with `actions/deploy-pages`. It runs on pushes
  to `main` that touch `site/**` or the workflow, and on manual dispatch.
- **Why a workflow** and not "deploy from a branch": branch publishing only offers the repository
  root or `/docs`. `/docs` holds the design docs and maintainer notes, and Pages would run them
  through Jekyll. A workflow publishes exactly `site/` and nothing else, with no `gh-pages` branch.
- The workflow uses only GitHub-owned actions pinned to commit SHAs, a read-only repository token
  plus the `pages: write` and `id-token: write` scopes the deploy action needs, and the
  `github-pages` environment. It runs only from `main`, never for pull requests, so forks cannot
  trigger a deployment.

## Keeping the page true

The page repeats facts that also live elsewhere (provider status, roadmap, build commands). A pull
request that changes any of these in the README or the [roadmap](roadmap.md) updates `site/index.html`
in the same PR. A page change goes through the normal PR and approval flow, like any other file.

## One-time setup (maintainer)

Enabling Pages is a repository setting, so it needs an admin: **Settings → Pages → Build and
deployment → Source: GitHub Actions**. After that, the first run of the Pages workflow publishes the
site (run it by hand from the Actions tab, or merge any change under `site/`). The site is served from the custom domain
`https://allmytasks.jkruza.com/` (a CNAME record for `allmytasks` pointing at `jkruza.github.io`, set under
Settings → Pages; the older `jkruza.github.io/allmytasks/` address redirects to it). See [MAINTAINING.md](../MAINTAINING.md#project-site).

## Not included

- A custom domain, analytics or a blog.
- Generated API or design docs on the site.
- A build step; the page is served as written.
