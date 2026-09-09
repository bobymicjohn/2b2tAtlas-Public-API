"""Check the public v2 manifest and sampled PNGs against the approved inventory."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen

BASE = 'https://tiles.atlas.example/AtlasTiles/Nocom/'


def fetch(url):
    request = Request(url, headers={'Origin': 'https://atlas.example', 'User-Agent': 'Atlas-release-validation/2'})
    with urlopen(request, timeout=45) as response:
        return response.read(), dict(response.headers.items())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--pending-package', type=Path, default=Path('C:/AtlasSeo/pending-upload.json'))
    parser.add_argument('--report', type=Path, default=Path('C:/AtlasRecovery/nocom-production-public-verification.json'))
    args = parser.parse_args()
    raw, _ = fetch(BASE+'v2/manifest.json?v=20260908-contrast-v2')
    manifest = json.loads(raw)
    complete = json.loads((args.candidate/'production-complete.json').read_text())
    assert manifest['version'] == 'nocom-world-pulse-v2'
    assert manifest['reviewOnly'] is False and manifest['materialization'] == 'complete'
    assert manifest['release']['tiles'] == complete['tiles']
    assert manifest['release']['inventorySha256'].lower() == complete['inventorySha256'].lower()
    assert manifest['displayDefaults']['style'] == 'contrast' and manifest['displayDefaults']['opacity'] == .85
    originals, _ = fetch(BASE+'v1/manifest.json')
    assert hashlib.sha256(originals).hexdigest() == complete['originalManifestSha256']
    source_manifest = json.loads(originals)
    assert manifest['frames'] == source_manifest['frames'] and manifest['totals'] == source_manifest['totals']
    samples = json.loads((args.candidate/'raster-validation.json').read_text())['results']
    selected = []
    for layer in manifest['totals']+manifest['frames']:
        root = layer['urlTemplate'].split('/{z}')[0]
        for zoom in sorted({min(map(int, layer['tilesPerZoom'])), 0, layer['maxNativeUrlZoom']}):
            selected.append(next(item for item in samples if item['tile'].startswith(root+f'/{zoom}/')))

    def check(item):
        data, headers = fetch(BASE+'v2/'+item['tile'])
        headers = {key.lower(): value for key, value in headers.items()}
        assert headers.get('content-type', '').startswith('image/png'), item['tile']
        assert headers.get('access-control-allow-origin') in ('*', 'https://atlas.example'), item['tile']
        digest = hashlib.sha256(data).hexdigest()
        assert digest == item['candidateSha256'], item['tile']
        return dict(tile=item['tile'], sha256=digest, bytes=len(data), passed=True)

    with ThreadPoolExecutor(max_workers=6) as executor:
        results = list(executor.map(check, selected))
    for dim in ('overworld', 'nether', 'end'):
        sample = next(item for item in selected if item['tile'].startswith(f'total/{dim}/'))
        data, _ = fetch(BASE+'v1/'+sample['tile'])
        assert hashlib.sha256(data).hexdigest() == sample['sourceSha256']
    pending = json.loads(args.pending_package.read_text(encoding='utf-8-sig'))
    frontend_error = None
    try:
        live_release, _ = fetch('https://atlas.example/seo-release.json')
        live = json.loads(live_release)
    except (URLError, ValueError) as error:
        live = {}
        frontend_error = str(error)
    report = dict(checkedAtUtc=datetime.now(timezone.utc).isoformat(), tilesPublished=True,
                  totalTiles=complete['tiles'], publicTileChecks=len(results), publicTileChecksPassed=len(results),
                  originalManifestAndSamplesUnchanged=True, layers=len(manifest['totals']+manifest['frames']),
                  expectedFrontendFingerprint=pending['fingerprint'], observedFrontendFingerprint=live.get('seoFingerprint'),
                  frontendActive=live.get('seoFingerprint') == pending['fingerprint'], frontendMetadataError=frontend_error, results=results)
    args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({key: value for key, value in report.items() if key != 'results'}, indent=2))


if __name__ == '__main__':
    main()
