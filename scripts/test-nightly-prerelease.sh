#!/usr/bin/env bash
# Tests nightly-prerelease.sh against throwaway repositories (no network, no signing key needed).
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
script="$here/nightly-prerelease.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
export XDG_RUNTIME_DIR="$tmp"
fail=0
check() { if [[ "$2" == *"$3"* ]]; then echo "ok   $1"; else echo "FAIL $1: expected '$3' in: $2"; fail=1; fi; }

git init -q --bare "$tmp/origin.git"
git clone -q "$tmp/origin.git" "$tmp/work" 2> /dev/null
cd "$tmp/work"
git checkout -q -b main
c() { git commit -q --allow-empty -m "$1"; }
c one; git tag v0.0.1; git push -q origin main --tags
c two; git tag v0.7.1-alpha; c three; git tag v0.8.0-alpha; git push -q origin main --tags

out=$(AMT_REPO="$tmp/work" "$script" --dry-run); check "no change" "$out" "nothing merged"
c four; git push -q origin main
out=$(AMT_REPO="$tmp/work" "$script" --dry-run); check "bumps minor" "$out" "would tag v0.9.0-alpha"
check "lists commits" "$out" "four"
out=$(AMT_REPO="$tmp/work" "$script" --dry-run); check "dry run is repeatable" "$out" "would tag v0.9.0-alpha"
git tag -l | grep -q v0.9.0-alpha && { echo "FAIL dry run created a tag"; fail=1; }

# Real run without a signing key: unsigned tags are not allowed, so it must fail and leave no tag behind.
if AMT_REPO="$tmp/work" "$script" > /dev/null 2>&1; then echo "FAIL unsigned run succeeded"; fail=1; else echo "ok   fails without signing key"; fi
git ls-remote --tags origin | grep -q v0.9.0-alpha && { echo "FAIL tag pushed"; fail=1; }

# Stable latest -> next minor with -alpha; stable sorts above the same prerelease.
git tag v0.8.0; git push -q origin --tags
out=$(AMT_REPO="$tmp/work" "$script" --dry-run); check "stable latest" "$out" "nothing merged"
c five; git push -q origin main
out=$(AMT_REPO="$tmp/work" "$script" --dry-run); check "stable then alpha suffix" "$out" "would tag v0.9.0-alpha"
exit "$fail"
