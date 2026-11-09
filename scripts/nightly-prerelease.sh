#!/usr/bin/env bash
# Daily prerelease: if main has commits (merged PRs) after the latest v* tag, push the next
# prerelease tag, which starts the Release workflow (.github/workflows/release.yml).
#
#   latest tag v0.8.0-alpha -> v0.9.0-alpha     (minor + 1, patch reset to 0)
#   latest tag v0.7.1-alpha -> v0.8.0-alpha
#   latest tag v1.2.0       -> v1.3.0-alpha     (the -alpha suffix is always added)
#
# Run from a systemd user timer (scripts/systemd/). The tag is signed with the configured git
# signing key and pushed over the existing `origin` with git's own credentials. The workflow
# still waits for the maintainer to approve the `release` environment and leaves a draft, which
# the maintainer publishes as a pre-release (docs/MAINTAINING.md).
#
# Usage: nightly-prerelease.sh [--dry-run]
# Env:   AMT_REPO   clone to tag from (default: the repo containing this script)
#        AMT_REMOTE remote name (default: origin)
set -euo pipefail

dry_run=0
case "${1:-}" in
  "") ;;
  --dry-run) dry_run=1 ;;
  *) echo "usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

repo="${AMT_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
remote="${AMT_REMOTE:-origin}"
git() { command git -C "$repo" "$@"; }
log() { echo "nightly-prerelease: $*"; }

# One run at a time (the timer's catch-up run could overlap a manual one).
exec 9>"${XDG_RUNTIME_DIR:-/tmp}/amt-nightly-prerelease.lock"
flock -n 9 || { log "another run is in progress, exiting"; exit 0; }

# Fetch main and the release tags; never touches the working tree or checked-out branch.
git fetch --quiet "$remote" "+refs/heads/main:refs/remotes/$remote/main" "+refs/tags/v*:refs/tags/v*"

# Latest tag by semver: x.y.z, with a prerelease (-alpha) sorting below the same stable version.
latest=$(git tag --list 'v*' | sed -nE 's/^v([0-9]+)\.([0-9]+)\.([0-9]+)(-.+)?$/\1 \2 \3 \4 &/p' \
  | awk '{ rank = ($4 ~ /^v/) ? 1 : 0; tag = ($4 ~ /^v/) ? $4 : $5; print $1, $2, $3, rank, tag }' \
  | sort -k1,1n -k2,2n -k3,3n -k4,4n | tail -n 1 | awk '{ print $5 }')
if [[ -z "$latest" ]]; then
  log "no v* tag found; create the first release by hand"
  exit 1
fi

main_sha=$(git rev-parse "refs/remotes/$remote/main")
count=$(git rev-list --count "refs/tags/$latest^{commit}..$main_sha")
if [[ "$count" -eq 0 ]]; then
  log "nothing merged to main since $latest; no prerelease"
  exit 0
fi

IFS=. read -r major minor _ <<< "${latest#v}"
next="v${major}.$((minor + 1)).0-alpha"
if git rev-parse -q --verify "refs/tags/$next" > /dev/null; then
  log "tag $next already exists; refusing to continue"
  exit 1
fi

log "$count commit(s) on main since $latest:"
git log --format='  %h %s' "refs/tags/$latest^{commit}..$main_sha"

if [[ "$dry_run" -eq 1 ]]; then
  log "dry run: would tag $next at ${main_sha:0:7} and push it to $remote"
  exit 0
fi

git tag -s "$next" -m "$next" "$main_sha"
if ! git push "$remote" "refs/tags/$next"; then
  git tag -d "$next" > /dev/null
  log "push failed; local tag removed"
  exit 1
fi
log "pushed $next; approve the 'release' environment in the Actions run, then publish the draft as a pre-release"
