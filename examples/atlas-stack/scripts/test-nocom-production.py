"""Exercise full materialization/copy gates on a tiny isolated dataset."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from PIL import Image
from nocom_contrast_tiles import Candidate, sha256

SCRIPTS = Path(__file__).resolve().parent


class ProductionTests(unittest.TestCase):
    def test_complete_copy_and_corrupt_source_rejection(self):
        with tempfile.TemporaryDirectory(prefix='nocom-release-test-') as temporary:
            root = Path(temporary)
            source, output = root/'v1', root/'candidate'
            source.mkdir()
            layers = []
            for dimension in ('overworld', 'nether', 'end'):
                layers.append(dict(dimension=dimension, maxNativeUrlZoom=0, tilesPerZoom={'0': 1, '-1': 1}, urlTemplate=f'total/{dimension}/{{z}}/{{y}}/{{x}}.png'))
                for zoom in (0, -1):
                    path = source/f'total/{dimension}/{zoom}/-1/0.png'
                    path.parent.mkdir(parents=True)
                    tile = Image.new('RGBA', (256, 256))
                    tile.putpixel((255, 0), (34, 211, 238, 83))
                    tile.save(path)
            (source/'manifest.json').write_text(json.dumps(dict(totals=layers, frames=[])), encoding='utf-8')
            before = {str(p): sha256(p) for p in source.rglob('*') if p.is_file()}
            Candidate(source, output)
            command = [sys.executable, str(SCRIPTS/'materialize-nocom-production.py'), '--source', str(source), '--output', str(output), '--processes', '2']
            materialized = subprocess.run(command, capture_output=True, text=True, timeout=60)
            self.assertEqual(materialized.returncode, 0, materialized.stderr)
            complete = json.loads((output/'production-complete.json').read_text())
            self.assertEqual(complete['tiles'], 6)
            self.assertEqual(complete['levels'], 6)
            for path, digest in before.items(): self.assertEqual(sha256(path), digest)
            copy_command = [sys.executable, str(SCRIPTS/'stage-nocom-production.py'), '--source', str(output), '--target', str(root/'staged')]
            staged = subprocess.run(copy_command, capture_output=True, text=True, timeout=60)
            self.assertEqual(staged.returncode, 0, staged.stderr)
            report = json.loads((root/'staged/staging-complete.json').read_text())
            self.assertTrue(report['everyStagedFileHashVerified'])
            self.assertEqual(report['inventorySha256'], complete['inventorySha256'])
            for line in (output/'production-files.jsonl').read_text().splitlines():
                path, digest, length = json.loads(line)
                self.assertEqual(sha256(root/'staged'/path), digest)
                self.assertEqual((root/'staged'/path).stat().st_size, length)
            (output/'total/end/0/-1/0.png').write_bytes(b'corrupt')
            corrupt = subprocess.run(copy_command[:-1]+[str(root/'rejected')], capture_output=True, text=True, timeout=60)
            self.assertNotEqual(corrupt.returncode, 0)
            self.assertIn('Source hash mismatch', corrupt.stderr)
            self.assertFalse((root/'rejected/staging-complete.json').exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
