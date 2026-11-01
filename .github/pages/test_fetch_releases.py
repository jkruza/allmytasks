import json
import os
import tempfile
import unittest

import fetch_releases as fr


def rel(tag, draft=False, published=True):
    return {"tag_name": tag, "draft": draft, "published_at": "2026-11-01T00:00:00Z" if published else None}


class SatisfiedTests(unittest.TestCase):
    def test_published_needs_the_tag_live(self):
        self.assertFalse(fr.satisfied([rel("v1")], "v2", "published"))
        self.assertFalse(fr.satisfied([rel("v2", draft=True, published=False)], "v2", "published"))
        self.assertTrue(fr.satisfied([rel("v1"), rel("v2")], "v2", "published"))

    def test_deleted_and_unpublished_need_the_tag_gone(self):
        self.assertFalse(fr.satisfied([rel("v2")], "v2", "deleted"))
        self.assertTrue(fr.satisfied([rel("v1")], "v2", "deleted"))
        self.assertTrue(fr.satisfied([rel("v2", draft=True, published=False)], "v2", "unpublished"))

    def test_no_event_means_no_waiting(self):
        self.assertTrue(fr.satisfied([], None, None))
        self.assertTrue(fr.satisfied([], "v2", "created"))


class MainTests(unittest.TestCase):
    def run_main(self, lists, tag="v2", action="published"):
        calls, sleeps = [], []

        def fetcher(repo):
            calls.append(repo)
            return lists[min(len(calls) - 1, len(lists) - 1)]

        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "r.json")
            fr.main("o/r", path, tag, action, sleep=sleeps.append, fetcher=fetcher)
            with open(path, encoding="utf-8") as f:
                return json.load(f), len(calls), len(sleeps)

    def test_waits_until_the_release_shows_up(self):
        result, calls, sleeps = self.run_main([[rel("v1")], [rel("v1")], [rel("v1"), rel("v2")]])
        self.assertEqual([r["tag_name"] for r in result], ["v1", "v2"])
        self.assertEqual((calls, sleeps), (3, 2))

    def test_gives_up_after_the_limit_and_still_writes(self):
        result, calls, sleeps = self.run_main([[rel("v1")]])
        self.assertEqual(len(result), 1)
        self.assertEqual(sleeps, fr.WAIT_SECONDS // fr.POLL_SECONDS)

    def test_no_wait_without_an_event(self):
        _, calls, sleeps = self.run_main([[rel("v1")]], tag=None, action=None)
        self.assertEqual((calls, sleeps), (1, 0))


if __name__ == "__main__":
    unittest.main()
