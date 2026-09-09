"""Copy completed candidate PNGs to release staging, verifying every SHA-256."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import time
import uuid
from nocom_contrast_tiles import TILE, sha256


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--target', type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    target = args.target.resolve()
    if source == target or source in target.parents or target in source.parents:
        raise ValueError('Release staging must be separate from its source')
    target.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((source/'manifest.json').read_text(encoding='utf-8-sig'))
    marker = target/'staging-source.json'
    identity = dict(source=str(source), profile=manifest['displayProfile'], originalManifestSha256=manifest['originalManifestSha256'])
    if marker.exists():
        if json.loads(marker.read_text()) != identity: raise ValueError('Different release already staged here')
    elif any(target.iterdir()): raise ValueError('Unidentified nonempty release staging')
    else: marker.write_text(json.dumps(identity), encoding='utf-8')

    def copy(record):
        relative, digest, size = record
        if not TILE.fullmatch(relative): raise ValueError('Invalid receipt path')
        destination = target/relative
        if destination.is_file() and destination.stat().st_size == size and sha256(destination) == digest:
            return size
        data = (source/relative).read_bytes()
        if len(data) != size or hashlib.sha256(data).hexdigest() != digest: raise ValueError(f'Source hash mismatch: {relative}')
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_name(destination.name+'.'+uuid.uuid4().hex+'.tmp')
        temporary.write_bytes(data)
        if sha256(temporary) != digest: raise ValueError(f'Copy hash mismatch: {relative}')
        os.replace(temporary, destination)
        return size

    receipts = source/'production-files.jsonl'
    completion = source/'production-complete.json'
    count = 0
    size = 0
    started = time.monotonic()
    with receipts.open(encoding='utf-8') as stream, ThreadPoolExecutor(max_workers=8) as executor:
        batch = []
        while True:
            position = stream.tell()
            line = stream.readline()
            if line.endswith('\n'):
                batch.append(json.loads(line))
            else:
                stream.seek(position)
            if len(batch) >= 128 or (not line.endswith('\n') and batch):
                size += sum(executor.map(copy, batch))
                previous = count
                count += len(batch)
                batch = []
                if count//10000 != previous//10000: print(f'Verified {count:,} staged PNGs; {count/(time.monotonic()-started):.1f}/sec', flush=True)
            if not line.endswith('\n'):
                if completion.exists():
                    report = json.loads(completion.read_text())
                    if report.get('complete') and report['inventorySha256'] == sha256(receipts):
                        if count != report['tiles'] or size != report['bytes']: raise ValueError('Incomplete staged generation')
                        break
                time.sleep(1)
    report['everyStagedFileHashVerified'] = True
    report['stagedAtUtc'] = datetime.now(timezone.utc).isoformat()
    (target/'staging-complete.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report), flush=True)


if __name__ == '__main__':
    main()
