import unittest

from render_downloads import pick, render


def rel(tag, when, pre=False, draft=False):
    return {"tag_name": tag, "name": tag, "published_at": when, "prerelease": pre, "draft": draft,
            "html_url": f"https://github.com/jkruza/allmytasks/releases/tag/{tag}",
            "assets": [{"name": f"{tag}.zip",
                        "browser_download_url": f"https://github.com/jkruza/allmytasks/releases/download/{tag}/{tag}.zip"}]}


class PickTests(unittest.TestCase):
    def test_prerelease_older_than_release_is_hidden(self):
        r, p = pick([rel("v1.0.0", "2026-02-01T00:00:00Z"), rel("v0.9.0-beta", "2026-01-01T00:00:00Z", pre=True)])
        self.assertEqual(r["tag_name"], "v1.0.0")
        self.assertIsNone(p)

    def test_prerelease_newer_than_release_is_shown(self):
        r, p = pick([rel("v1.0.0", "2026-02-01T00:00:00Z"), rel("v1.1.0-beta", "2026-03-01T00:00:00Z", pre=True)])
        self.assertEqual(p["tag_name"], "v1.1.0-beta")

    def test_drafts_are_ignored(self):
        r, p = pick([rel("v2.0.0", "2026-05-01T00:00:00Z", draft=True)])
        self.assertIsNone(r)
        self.assertIsNone(p)

    def test_only_prerelease_exists(self):
        r, p = pick([rel("v0.1.0-alpha", "2026-01-01T00:00:00Z", pre=True)])
        self.assertIsNone(r)
        self.assertEqual(p["tag_name"], "v0.1.0-alpha")


class RenderTests(unittest.TestCase):
    def test_lists_only_packages_and_checksums_one_per_line(self):
        r = rel("v1.0.0", "2026-01-01T00:00:00Z")
        base = "https://github.com/jkruza/allmytasks/releases/download/v1.0.0/"
        r["assets"] = [{"name": n, "browser_download_url": base + n} for n in (
            "AllMyTasks-v1.0.0-win-x64.zip", "AllMyTasks-v1.0.0-windows-x64.msi", "AllMyTasks.wixpdb",
            "SHA256SUMS.txt", "AllMyTasks-v1.0.0-linux-x64.tar.gz", "AllMyTasks-v1.0.0-linux-x64.deb",
            "AllMyTasks-v1.0.0-linux-x64.AppImage")]
        out = render([r])
        self.assertEqual(out.count("<li>"), 4)
        for gone in (".zip", ".tar.gz", ".wixpdb"):
            self.assertNotIn(gone, out)
        self.assertLess(out.index("linux-x64.deb"), out.index("SHA256SUMS.txt"))

    def test_no_files_list_when_nothing_qualifies(self):
        r = rel("v1.0.0", "2026-01-01T00:00:00Z")
        r["assets"] = [{"name": "a.zip", "browser_download_url": "https://github.com/x/a.zip"}]
        self.assertNotIn("<ul", render([r]))

    def test_nothing_published_falls_back(self):
        out = render([])
        self.assertEqual(out.count("Not available now"), 2)
        self.assertIn("See all releases on GitHub", out)

    def test_escapes_and_rejects_foreign_links(self):
        r = rel("v1<b>", "2026-01-01T00:00:00Z")
        r["html_url"] = "javascript:alert(1)"
        out = render([r])
        self.assertNotIn("<b>", out)
        self.assertNotIn("javascript:", out)


if __name__ == "__main__":
    unittest.main()
