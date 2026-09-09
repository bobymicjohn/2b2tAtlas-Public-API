"""Build and inventory a complete separate generation; never replace live tiles."""
import argparse
from collections import Counter
from concurrent.futures import ProcessPoolExecutor, wait, FIRST_COMPLETED
from datetime import datetime, timezone
from itertools import islice
import json
from pathlib import Path
import time

from nocom_contrast_tiles import Candidate, prepare_color_lut, sha256

_candidate = None


def initialize(source, output):
    global _candidate
    prepare_color_lut()
    _candidate = Candidate(source, output, workers=1)


def render_batch(paths):
    results = []
    for relative in paths:
        output = _candidate.tile(relative)
        if output is None:
            raise FileNotFoundError(relative)
        results.append((relative, sha256(output), output.stat().st_size))
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--processes', type=int, choices=range(1, 9), default=6)
    args = parser.parse_args()
    candidate = Candidate(args.source, args.output)
    expected = {}
    for layer in candidate.manifest['totals'] + candidate.manifest['frames']:
        root = layer['urlTemplate'].split('/{z}')[0]
        for zoom, count in layer['tilesPerZoom'].items():
            expected[root+'/'+zoom] = count
    paths = (path.relative_to(candidate.source).as_posix() for path in candidate.source.glob('**/*.png'))
    actual = Counter()
    total = 0
    size = 0
    started = time.monotonic()
    receipt_path = candidate.output/'production-files.jsonl'
    # A restarted run inventories every existing output again. PNG writes are
    # atomic; a crash cannot mark an incomplete generation ready for publication.
    with receipt_path.open('w', encoding='utf-8') as receipt, ProcessPoolExecutor(
        max_workers=args.processes, initializer=initialize,
        initargs=(str(candidate.source), str(candidate.output))
    ) as executor:
        pending = set()
        exhausted = False
        while pending or not exhausted:
            while not exhausted and len(pending) < args.processes*2:
                batch = list(islice(paths, 128))
                if not batch:
                    exhausted = True
                    break
                pending.add(executor.submit(render_batch, batch))
            if not pending:
                break
            done, pending = wait(pending, return_when=FIRST_COMPLETED)
            for future in done:
                for relative, digest, length in future.result():
                    receipt.write(json.dumps([relative, digest, length], separators=(',', ':'))+'\n')
                    actual[relative.rsplit('/', 2)[0]] += 1
                    total += 1
                    size += length
                if total//10000 != (total-len(future.result()))//10000:
                    print(f'{total:,}/{sum(expected.values()):,} tiles; {total/(time.monotonic()-started):.1f}/sec', flush=True)
                    receipt.flush()
    if dict(actual) != expected:
        raise RuntimeError('Pyramid counts differ from the source manifest')
    if sha256(candidate.source/'manifest.json') != candidate.manifest_hash:
        raise RuntimeError('Source manifest changed during rendering')
    report = dict(complete=True, profile=candidate.manifest['displayProfile'],
                  completedAtUtc=datetime.now(timezone.utc).isoformat(),
                  tiles=total, bytes=size, levels=len(actual), layers=len(candidate.manifest['totals'] + candidate.manifest['frames']),
                  originalManifestSha256=candidate.manifest_hash,
                  inventorySha256=sha256(receipt_path), seconds=time.monotonic()-started)
    (candidate.output/'production-complete.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report), flush=True)


if __name__ == '__main__':
    main()
