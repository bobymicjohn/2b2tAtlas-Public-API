import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


spec = importlib.util.spec_from_file_location('sync', Path(__file__).parents[1] / 'sync-youtube-research-transcripts.py')
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class ResearchCatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.args = SimpleNamespace(output_root=str(self.root), slug='creator',
                                    channel='https://www.youtube.com/@creator/videos', catalog_only=True)

    def run_catalog(self, channel, entries):
        with patch.object(sync, 'parse_args', return_value=self.args), patch.object(sync, 'get_catalog', return_value=(channel, entries)):
            sync.main()

    def test_videos_shorts_and_streams_merge_by_id(self):
        channel = {'channel_id': 'UCcreator', 'channel': 'Creator'}
        self.run_catalog(channel, [{'id': 'long', 'title': 'Long video'}])
        self.args.channel = 'https://www.youtube.com/@creator/shorts'
        self.run_catalog(channel, [{'id': 'short', 'title': 'Short'}, {'id': 'long', 'title': 'Updated title'}])
        self.args.channel = 'https://www.youtube.com/@creator/streams'
        self.run_catalog(channel, [{'id': 'stream', 'title': 'Stream'}])
        catalog = json.loads((self.root / 'creator/catalog.json').read_text(encoding='utf-8'))
        self.assertEqual({e['id'] for e in catalog['entries']}, {'long', 'short', 'stream'})
        self.assertEqual(len(catalog['sources']), 3)
        self.assertEqual(next(e['title'] for e in catalog['entries'] if e['id'] == 'long'), 'Updated title')

    def test_wrong_channel_preserves_existing_catalog(self):
        self.run_catalog({'channel_id': 'UCone'}, [{'id': 'one'}])
        before = (self.root / 'creator/catalog.json').read_bytes()
        with self.assertRaisesRegex(ValueError, 'different channel'):
            self.run_catalog({'channel_id': 'UCtwo'}, [{'id': 'two'}])
        self.assertEqual((self.root / 'creator/catalog.json').read_bytes(), before)

    def test_native_title_selection_and_run_history(self):
        self.args.catalog_only = False
        self.args.video_id = []
        self.args.title_pattern = '(?i)2b2t'
        self.args.max_videos = 0
        self.args.refresh = False
        self.args.caption_language = ['ja-orig', 'ja']
        self.args.max_consecutive_errors = 3
        with patch.object(sync, 'parse_args', return_value=self.args), \
             patch.object(sync, 'get_catalog', return_value=({'channel_id': 'UCone'}, [{'id': 'japanese', 'title': '２ｂ２ｔの歴史'}])), \
             patch.object(sync, 'download_caption', return_value=(None, self.root / 'metadata.json')) as download:
            sync.main()
            sync.main()
            self.assertEqual(download.call_args.args[2], ['ja-orig', 'ja'])
        record = json.loads((self.root / 'creator/last-run.json').read_text(encoding='utf-8'))
        self.assertEqual(record['selectedVideos'], 1)
        self.assertEqual(record['results'][0]['status'], 'no-requested-captions')
        self.assertEqual(len(list((self.root / 'creator/runs').glob('*.json'))), 2)


if __name__ == '__main__':
    unittest.main()
