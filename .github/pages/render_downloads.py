#!/usr/bin/env python3
"""Fills the download section of site/index.html from the GitHub releases list.

Usage: render_downloads.py RELEASES_JSON INDEX_HTML

RELEASES_JSON is the output of `gh api repos/OWNER/REPO/releases --paginate --slurp`
(or a plain list). Everything between the downloads:start and downloads:end markers in
INDEX_HTML is replaced. See docs/design/site-downloads.md.
"""
import html
import json
import re
import sys

ALL_RELEASES = "https://github.com/jkruza/allmytasks/releases"
START, END = "<!-- downloads:start -->", "<!-- downloads:end -->"
TEXT = "font-size: 14px; line-height: 22px; color: var(--mut)"
CARD = ("flex: 1 1 320px; display: flex; flex-direction: column; gap: 8px; padding: 20px; "
        "background: var(--s000); border: 1px solid var(--bd); border-radius: 6px")
BUTTON = ("display: inline-block; align-self: flex-start; padding: 10px 16px; border-radius: 6px; "
          "background: var(--ink); color: var(--s100); text-decoration: none; font-weight: 600; "
          "font-size: 14px; line-height: 20px")


def flatten(data):
    if data and isinstance(data[0], list):  # `gh api --paginate --slurp` gives a list of pages
        return [r for page in data for r in page]
    return data


def pick(releases):
    """Returns (release, prerelease); prerelease is None unless it is newer than release."""
    published = [r for r in releases if not r.get("draft") and r.get("published_at")]
    published.sort(key=lambda r: r["published_at"], reverse=True)  # ISO 8601 UTC sorts as text
    release = next((r for r in published if not r.get("prerelease")), None)
    prerelease = next((r for r in published if r.get("prerelease")), None)
    if prerelease and release and prerelease["published_at"] <= release["published_at"]:
        prerelease = None
    return release, prerelease


PACKAGE_SUFFIXES = (".msi", ".appimage", ".deb", ".rpm", ".dmg", ".pkg")
CHECKSUMS = "sha256sums.txt"


def is_listed(name):
    """Installable packages and the checksums file; archives and build leftovers stay on the release page."""
    lower = name.lower()
    return lower.endswith(PACKAGE_SUFFIXES) or lower == CHECKSUMS


def sort_key(name):
    return (name.lower() == CHECKSUMS, name.lower())  # checksums last


def safe_url(url):
    return url if re.fullmatch(r"https://github\.com/[^\s\"'<>]+", url or "") else ALL_RELEASES


def card(title, release, lead):
    e = html.escape
    if release is None:
        body = (f'<p style="margin: 0; {TEXT}">Not available now. '
                f'<a href="{ALL_RELEASES}">See all releases on GitHub</a>.</p>')
    else:
        name = release.get("name") or release["tag_name"]
        date = release["published_at"][:10]
        assets = sorted((a for a in release.get("assets", [])
                         if a.get("browser_download_url") and is_listed(a["name"])),
                        key=lambda a: sort_key(a["name"]))
        links = "".join(f'<li><a href="{e(safe_url(a["browser_download_url"]))}">{e(a["name"])}</a></li>'
                        for a in assets)
        body = (f'<p style="margin: 0; {TEXT}">{e(name)}, published {e(date)}.</p>'
                f'<a href="{e(safe_url(release["html_url"]))}" style="{BUTTON}">{e(lead)}</a>')
        if links:
            body += (f'<ul style="margin: 0; padding: 0; list-style: none; font-size: 13px; line-height: 24px; '
                     f'color: var(--mut)">{links}</ul>')
    return (f'<div style="{CARD}"><h3 style="margin: 0; font-size: 18px; line-height: 24px; '
            f'font-weight: 600">{e(title)}</h3>{body}</div>')


def render(releases):
    release, prerelease = pick(releases)
    cards = [card("Latest release", release, "Download from the GitHub release")]
    if prerelease is not None or release is None:
        cards.append(card("Latest prerelease", prerelease, "Download from the GitHub prerelease"))
    else:
        cards.append(
            f'<div style="{CARD}"><h3 style="margin: 0; font-size: 18px; line-height: 24px; font-weight: 600">'
            f'Latest prerelease</h3><p style="margin: 0; {TEXT}">Not available now; the latest release is '
            f'the newest build. <a href="{ALL_RELEASES}">See all releases on GitHub</a>.</p></div>')
    return '<div style="display: flex; flex-wrap: wrap; gap: 16px">' + "".join(cards) + "</div>"


def main(releases_path, index_path):
    with open(releases_path, encoding="utf-8") as f:
        releases = flatten(json.load(f))
    with open(index_path, encoding="utf-8") as f:
        page = f.read()
    if page.count(START) != 1 or page.count(END) != 1:
        sys.exit(f"{index_path}: expected exactly one {START} / {END} pair")
    head, rest = page.split(START)
    _, tail = rest.split(END)
    with open(index_path, "w", encoding="utf-8") as f:
        f.write(head + START + "\n" + render(releases) + "\n" + END + tail)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
