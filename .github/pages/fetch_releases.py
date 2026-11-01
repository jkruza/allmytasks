#!/usr/bin/env python3
"""Fetches the releases list with `gh api` and, after a release event, waits until the list reflects it.

Usage: fetch_releases.py REPO OUT_JSON [TAG ACTION]

ACTION is the release event type (published, edited, deleted, unpublished). Right after such an event the API
can still return the old list, so the page would be rendered without the change. This polls until the list
agrees with the event, up to WAIT_SECONDS, then continues with a warning (the daily run corrects the site).
See docs/design/site-downloads.md.
"""
import json
import subprocess
import sys
import time

WAIT_SECONDS = 360
POLL_SECONDS = 15


def flatten(data):
    if data and isinstance(data[0], list):  # `--paginate --slurp` gives a list of pages
        return [r for page in data for r in page]
    return data


def satisfied(releases, tag, action):
    """True when the list already reflects the event; unknown or missing input counts as satisfied."""
    if not tag or action not in ("published", "edited", "deleted", "unpublished"):
        return True
    found = next((r for r in releases if r.get("tag_name") == tag), None)
    live = found is not None and not found.get("draft") and bool(found.get("published_at"))
    if action in ("published", "edited"):
        return live
    return not live  # deleted / unpublished


def fetch(repo):
    out = subprocess.run(["gh", "api", f"repos/{repo}/releases?per_page=100", "--paginate", "--slurp"],
                         check=True, capture_output=True, text=True).stdout
    return flatten(json.loads(out))


def main(repo, out_path, tag=None, action=None, sleep=time.sleep, fetcher=fetch):
    waited = 0
    while True:
        releases = fetcher(repo)
        if satisfied(releases, tag, action):
            break
        if waited >= WAIT_SECONDS:
            print(f"::warning title=Release not visible yet::{action} {tag}: the releases list did not catch up "
                  f"after {waited}s; rendering what the API returns. The daily run will correct the site.")
            break
        print(f"Release {tag} ({action}) not reflected in the releases list yet; waiting {POLL_SECONDS}s")
        sleep(POLL_SECONDS)
        waited += POLL_SECONDS
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(releases, f)


if __name__ == "__main__":
    if len(sys.argv) not in (3, 5):
        sys.exit(__doc__)
    main(*sys.argv[1:])
