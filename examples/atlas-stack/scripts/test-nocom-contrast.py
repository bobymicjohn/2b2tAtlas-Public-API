"""Check contrast rendering, unchanged coverage, cache guards and real pyramids."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np
from PIL import Image
from nocom_contrast_tiles import Candidate, PALETTE, PROFILE, recolor, recolor_reference, prepare_color_lut, sha256

spec = importlib.util.spec_from_file_location('nocom_generator', Path(__file__).with_name('generate-nocom-heatmaps.py'))
generator = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = generator
spec.loader.exec_module(generator)


class ContrastTests(unittest.TestCase):
    def test_batch_lookup_matches_approved_renderer_exactly(self):
        prepare_color_lut()
        random = np.random.default_rng(20260908)
        rgba = random.integers(0, 256, size=(1024, 1024, 4), dtype=np.uint8)
        np.testing.assert_array_equal(np.array(recolor(Image.fromarray(rgba))), np.array(recolor_reference(Image.fromarray(rgba))))

    def test_empty_cells_stay_empty_and_weak_hits_get_stronger(self):
        counts = [1, 10, 100, 1000, 10000, 100000]
        rgba = np.array([[generator.color_for_count(n) for n in counts] + [(255, 255, 255, 0)]], dtype=np.uint8)
        result = np.array(recolor(Image.fromarray(rgba)))
        np.testing.assert_array_equal(result[:, :, 3] > 0, rgba[:, :, 3] > 0)
        self.assertGreaterEqual(int(result[0, 0, 3]), 1.8 * int(rgba[0, 0, 3]))
        self.assertTrue(np.all(np.diff(result[0, :-1, 3].astype(int)) > 0))
        np.testing.assert_array_equal(result[0, -2, :3], PALETTE[-1][1])
        np.testing.assert_array_equal(result[0, -1], [0, 0, 0, 0])

    def test_tile_edges_are_identical_to_rendering_a_mosaic(self):
        random = np.random.default_rng(42522657)
        rgba = random.integers(0, 256, size=(256, 512, 4), dtype=np.uint8)
        rgba[::3, ::7, 3] = 0
        whole = np.array(recolor(Image.fromarray(rgba)))
        split = np.concatenate([np.array(recolor(Image.fromarray(part))) for part in np.split(rgba, 2, axis=1)], axis=1)
        np.testing.assert_array_equal(whole, split)
        np.testing.assert_array_equal(whole[:, :, 3] > 0, rgba[:, :, 3] > 0)

    def test_originals_and_candidate_generations_are_protected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root/'original'
            source.mkdir()
            (source/'manifest.json').write_text('{}')
            tile = source/'total/end/-10/-1/0.png'
            tile.parent.mkdir(parents=True)
            Image.new('RGBA', (256, 256), generator.color_for_count(1)).save(tile)
            original_hash = sha256(tile)
            for output in (source, source/'candidate', root):
                with self.assertRaises(ValueError): Candidate(source, output)
            candidate = Candidate(source, root/'candidate')
            for bad in ('../manifest.json', 'total/end/0/0/../../manifest.json', '/total/end/0/0/0.png'):
                with self.assertRaises(ValueError): candidate.tile(bad)
            relative = tile.relative_to(source).as_posix()
            with ThreadPoolExecutor(max_workers=4) as executor:
                outputs = list(executor.map(candidate.tile, [relative]*12))
            self.assertEqual(len(set(outputs)), 1)
            self.assertEqual(sha256(tile), original_hash)
            self.assertFalse(list(candidate.output.glob('**/*.tmp')))
            self.assertIsNone(candidate.tile('total/end/4/10000/10000.png'))
            (candidate.output/'manifest.json').write_text('{}')
            with self.assertRaises(ValueError): Candidate(source, candidate.output)


def inspect_source(source, output):
    """One edge tile and, if present, a spawn tile at every layer's native LOD."""
    candidate = Candidate(source, output)
    manifest_hash = sha256(source/'manifest.json')
    results = []
    for layer in candidate.manifest['totals'] + candidate.manifest['frames']:
        relative_root = layer['urlTemplate'].split('/{z}')[0]
        offset = {'overworld': 8000, 'nether': 1344, 'end': 1312}[layer['dimension']]
        for zoom in sorted(map(int, layer['tilesPerZoom'])):
            level = source/relative_root/str(zoom)
            first = next(level.glob('*/*.png'), None)
            if first is None:
                raise AssertionError(f'Missing advertised pyramid level: {level}')
            spawn = offset // (256 * 2**(layer['maxNativeUrlZoom']-zoom))
            central = level/str(spawn)/f'{spawn}.png'
            paths = {first}
            if central.is_file(): paths.add(central)
            for path in paths:
                relative = path.relative_to(source).as_posix()
                before = sha256(path)
                rendered = candidate.tile(relative)
                with Image.open(path) as image: old = np.array(image.convert('RGBA'))
                with Image.open(rendered) as image: new = np.array(image.convert('RGBA'))
                assert old.shape == new.shape == (256, 256, 4), relative
                assert np.array_equal(old[:, :, 3] > 0, new[:, :, 3] > 0), relative
                assert np.any(new[:, :, 3]), relative
                assert sha256(path) == before, relative
                results.append({'tile': relative, 'sourceSha256': before, 'candidateSha256': sha256(rendered), 'observedPixels': int(np.count_nonzero(new[:, :, 3]))})
    assert sha256(source/'manifest.json') == manifest_hash
    report = {'profile': PROFILE, 'layers': len(candidate.manifest['totals'] + candidate.manifest['frames']), 'levels': sum(len(layer['tilesPerZoom']) for layer in candidate.manifest['totals'] + candidate.manifest['frames']), 'tilesChecked': len(results), 'sourceManifestSha256': manifest_hash, 'unchangedSourceAndCoverage': True, 'results': results}
    (output/'raster-validation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({key: value for key, value in report.items() if key != 'results'}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ContrastTests))
    if not result.wasSuccessful(): raise SystemExit(1)
    if args.source:
        if not args.output: parser.error('--output required with --source')
        inspect_source(args.source, args.output)
