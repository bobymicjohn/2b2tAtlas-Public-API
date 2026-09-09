"""Combine a private saved WDL with its continuation, without replacing either input.

Anvil files are merged by chunk slot, never by whole region filename. New chunk
payloads win overlaps; old-only chunks (and external payloads) remain byte exact.
The collector still runs its independent footprint/coverage audit afterwards.
"""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import struct
import uuid
import zipfile

REGION = re.compile(r'^(?:(DIM-1/|DIM1/))?(region|entities|poi)/r\.(-?\d+)\.(-?\d+)\.mca$')


def members(archive, *, allow_partial=False):
    result, roots = {}, set()
    for entry in archive.infolist():
        if entry.is_dir():
            continue
        path = PurePosixPath(entry.filename)
        if ('\\' in entry.filename or path.is_absolute() or ':' in entry.filename or
                '..' in path.parts or len(path.parts) < 2):
            raise ValueError('Unsafe capture ZIP member')
        roots.add(path.parts[0])
        relative = '/'.join(path.parts[1:])
        if relative in result:
            raise ValueError('Duplicate capture ZIP member')
        result[relative] = entry
    if len(roots) != 1 or (not allow_partial and 'level.dat' not in result) or 'wdl/download.jsonl' not in result:
        raise ValueError('Capture ZIP must contain one world and its WDL report')
    return roots.pop(), result


def slots(data):
    if len(data) < 8192 or len(data) % 4096:
        raise ValueError('Invalid region size')
    result, used = {}, {0, 1}
    for slot in range(1024):
        location = struct.unpack_from('>I', data, slot * 4)[0]
        if not location:
            continue
        offset, count = location >> 8, location & 255
        extent = set(range(offset, offset + count))
        if offset < 2 or not count or (offset + count) * 4096 > len(data) or used & extent:
            raise ValueError('Invalid/overlapping Anvil sectors')
        used.update(extent)
        position = offset * 4096
        length = struct.unpack_from('>I', data, position)[0]
        if length < 1 or length + 4 > count * 4096:
            raise ValueError('Invalid chunk length')
        payload = data[position:position + length + 4]
        if payload[4] & 127 not in (1, 2, 3):
            raise ValueError('Unsupported chunk compression')
        result[slot] = (payload, data[4096 + slot * 4:4100 + slot * 4])
    return result


def pack(chunks):
    result = bytearray(8192)
    for slot, (payload, timestamp) in sorted(chunks.items()):
        sector = len(result) // 4096
        count = (len(payload) + 4095) // 4096
        if count > 255 or sector >= 1 << 24:
            raise ValueError('Region cannot represent merged payload')
        struct.pack_into('>I', result, slot * 4, (sector << 8) | count)
        result[4096 + slot * 4:4100 + slot * 4] = timestamp
        result.extend(payload)
        result.extend(bytes((-len(result)) % 4096))
    return result


def merge(previous, current, output, root, *, allow_partial_current=False):
    previous, current, output = map(Path, (previous, current, output))
    if output.exists() or output.resolve() in (previous.resolve(), current.resolve()):
        raise ValueError('Output must be new; originals are immutable')
    if not re.fullmatch(r'archive-[A-Za-z0-9._-]+', root) or '..' in root:
        raise ValueError('Unsafe output world name')
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + '.' + uuid.uuid4().hex + '.partial')
    metrics = dict(retainedChunks=0, newChunks=0, replacedChunks=0, totalChunks=0)
    try:
        with zipfile.ZipFile(previous) as old, zipfile.ZipFile(current) as new:
            # A private recovered parent may predate the downloader's metadata
            # flush. The completed continuation must provide real metadata.
            _, older = members(old, allow_partial=True)
            _, newer = members(new, allow_partial=allow_partial_current)
            chosen_files = {name: (old, entry) for name, entry in older.items()}
            chosen_files.update({name: (new, entry) for name, entry in newer.items()})
            region_names = [name for name in chosen_files if REGION.fullmatch(name)]
            chosen_files.pop('wdl/atlas-resume.json', None)
            with zipfile.ZipFile(temporary, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as target:
                for name in sorted(region_names):
                    match = REGION.fullmatch(name)
                    if not match:
                        continue
                    before = slots(old.read(older[name])) if name in older else {}
                    after = slots(new.read(newer[name])) if name in newer else {}
                    combined = before | after
                    if match[2] == 'region':
                        metrics['retainedChunks'] += len(before.keys() - after.keys())
                        metrics['newChunks'] += len(after.keys() - before.keys())
                        metrics['replacedChunks'] += len(after.keys() & before.keys())
                        metrics['totalChunks'] += len(combined)
                    # Retain the external payload associated with the selected slot,
                    # even if the other archive contains a same-named stale .mcc.
                    folder = name.rsplit('/', 1)[0]
                    for slot, (payload, _) in combined.items():
                        if payload[4] & 128:
                            x, z = int(match[3]) * 32 + slot % 32, int(match[4]) * 32 + slot // 32
                            external = f'{folder}/c.{x}.{z}.mcc'
                            source, index = (new, newer) if slot in after else (old, older)
                            if external not in index:
                                raise ValueError('Missing external chunk payload')
                            chosen_files[external] = source, index[external]
                    target.writestr(root + '/' + name, pack(combined))
                for name, (source, entry) in sorted(chosen_files.items()):
                    if not REGION.fullmatch(name):
                        with source.open(entry) as src, target.open(root + '/' + name, 'w', force_zip64=True) as dst:
                            shutil.copyfileobj(src, dst, 1024 * 1024)
                if not metrics['totalChunks']:
                    raise ValueError('Merged capture contains no terrain')
                target.writestr(root + '/wdl/atlas-resume.json', json.dumps(dict(schema=1, **metrics)))
        with temporary.open('r+b') as durable:
            os.fsync(durable.fileno())
        # Windows rename fails if a concurrent writer has created the destination.
        temporary.rename(output)
        return metrics
    finally:
        if temporary.exists():
            temporary.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous', required=True)
    parser.add_argument('--current', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--root', required=True)
    args = parser.parse_args()
    print(json.dumps(merge(args.previous, args.current, args.output, args.root)))
