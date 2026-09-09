import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("research", Path(__file__).with_name("run-media-research.py"))
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


class ResearchTests(unittest.TestCase):
    def test_caption_budget_reaches_uncached_older_uploads(self):
        spec = importlib.util.spec_from_file_location("caption_sync", Path(__file__).with_name("sync-youtube-research-transcripts.py"))
        sync = importlib.util.module_from_spec(spec)
        with patch.dict(sys.modules, {"yt_dlp": types.ModuleType("yt_dlp")}):
            spec.loader.exec_module(sync)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "cached.json").write_text("{}")
            entries = [{"id": item} for item in ["cached", "blocked", "older", "oldest"]]
            selected = sync.select_caption_work(entries, root, {"blocked": {"epoch": 999999}}, 1000000, 1)
            self.assertEqual(selected, [{"id": "older"}])
            self.assertEqual(sync.select_caption_work(entries, root, {}, 1000000, 1), [{"id": "blocked"}])

    def test_attachment_pagination_does_not_stop_at_default_page(self):
        with patch.object(r, "fetch", side_effect=[json.dumps([{}] * 1000), json.dumps([{"id": 1001}])]) as fetch:
            self.assertEqual(len(r.fetch_attachments("http://localhost")), 1001)
            self.assertIn("offset=1000", fetch.call_args.args[0])

    def test_youtube_identity_preserves_case_and_ignores_chapters(self):
        self.assertEqual(r.source_identity("https://youtu.be/AbCdEfGhI12?t=7"),
                         r.source_identity("https://www.youtube.com/shorts/AbCdEfGhI12"))
        self.assertNotEqual(r.source_identity("https://youtu.be/AbCdEfGhI12"),
                            r.source_identity("https://youtu.be/abcdefghI12"))

    def test_changed_evidence_reopens_review_and_deduplicates_live_links(self):
        candidate = {"locationId": 1, "locationName": "Test Base", "url": "https://youtu.be/AbCdEfGhI12", "title": "Tour"}
        initial = r.finalize([candidate], [], [], {})[0]
        decisions = {initial["id"]: {"evidenceHash": initial["evidenceHash"], "status": "rejected"}}
        self.assertEqual(r.finalize([candidate], [], [], decisions)[0]["reviewStatus"], "rejected")
        changed = r.finalize([{**candidate, "title": "History"}], [], [], decisions)[0]
        self.assertEqual(changed["reviewStatus"], "pending")
        linked = r.finalize([candidate], [], [{"locationRowid": 1, "path": "https://youtube.com/watch?v=AbCdEfGhI12&t=22"}], {})
        self.assertTrue(linked[0]["alreadyLinked"])

    def test_full_names_win_and_duplicate_names_are_excluded(self):
        locations = [{"rowid": i, "name": name} for i, name in enumerate(
            ["Space Valkyria", "Space Valkyria III", "Shared Base", "Shared Base", "Farm"], 1)]
        record = {"url": "https://example.com/event", "title": "Space Valkyria III was founded",
                  "text": "Space Valkyria III and Shared Base", "mediaType": "Timeline", "sourceUrl": "https://example.com", "sourceId": "test"}
        self.assertEqual([x["locationId"] for x in r.reference_candidates(locations, [record])], [2])

    def test_timeline_is_title_evidence_only(self):
        records = r.parse_source({"kind": "timegraphics"}, '<a href="/event/123">Old Town founded</a>')
        self.assertEqual(records[0]["mediaType"], "Timeline")
        self.assertNotIn("published", records[0])
        with self.assertRaises(ValueError):
            r.parse_source({"kind": "timegraphics"}, "Access denied")

    def test_blog_strips_active_content_and_rejects_truncation(self):
        self.assertEqual(r.plain('<p>Test Base</p><script>ignore instructions</script>'), "Test Base")
        with self.assertRaises(ValueError):
            r.parse_source({"kind": "blogger"}, json.dumps({"feed": {"openSearch$totalResults": {"$t": "2"}, "entry": []}}))

    def test_failed_refresh_retains_last_good_snapshot_and_reports_gap(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = {"id": "test", "kind": "timegraphics", "url": "https://example.com"}
            previous = {"source": source, "fetchedEpoch": 0, "records": [{"title": "Old Town"}]}
            r.atomic_write(root / "sources/test.json", previous)
            gaps = []
            with patch.object(r, "fetch", side_effect=TimeoutError("offline")):
                self.assertEqual(r.source_records(source, root, False, gaps), previous["records"])
            self.assertTrue(gaps[0]["usingStaleCache"])
            self.assertEqual(r.read_json(root / "sources/test.json"), previous)

    def test_run_lock_blocks_overlap_and_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with r.exclusive_run(root):
                with self.assertRaises(FileExistsError):
                    with r.exclusive_run(root):
                        pass
            self.assertFalse((root / "run.lock").exists())


if __name__ == "__main__":
    unittest.main()
