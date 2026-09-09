"""Verify a bounded Atlas archive copy by SHA-256; never mutate either tree."""
import argparse
import concurrent.futures
import stat
import hashlib
import json
import os
import itertools
from pathlib import Path
from datetime import datetime, timezone


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--destination', required=True)
    parser.add_argument('--report', required=True)
    parser.add_argument('--workers', type=int, choices=range(1, 33), default=4)
    parser.add_argument('--resume', action='store_true', help='Reuse verified immutable files whose source/destination size and timestamps are unchanged.')
    args = parser.parse_args()
    source, destination = Path(args.source).resolve(), Path(args.destination).resolve()
    if source == destination or not source.is_dir() or not destination.is_dir():
        raise ValueError('Two distinct existing archive roots are required.')
    previous = {}
    if args.resume and Path(args.report).exists():
        for line in Path(args.report).read_text(encoding='utf-8').splitlines():
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                # An interrupted verifier can leave a partial final record.
                # Unreadable cache entries are rehashed, never trusted.
                continue
            if 'path' in entry and 'error' not in entry:
                previous[entry['path']] = entry
    def verify(original):
        relative = original.relative_to(source)
        copied = destination / relative
        before = original.stat(follow_symlinks=False)
        if getattr(before, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError(f'Archive contains a file reparse point: {relative}')
        target_info = copied.stat()
        if target_info.st_size != before.st_size:
            raise ValueError(f'Size mismatch: {relative}')
        cached = previous.get(str(relative))
        if cached and cached['bytes'] == before.st_size and cached.get('sourceMtimeNs') == before.st_mtime_ns and cached.get('destinationMtimeNs') == target_info.st_mtime_ns:
            return cached
        expected, actual = digest(original), digest(copied)
        after = original.stat()
        if expected != actual or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError(f'Hash mismatch or changing source: {relative}')
        return {'path': str(relative), 'bytes': before.st_size, 'sha256': actual,
                'sourceMtimeNs': before.st_mtime_ns, 'destinationMtimeNs': copied.stat().st_mtime_ns}

    def originals():
        for directory, folders, names in os.walk(source, followlinks=False):
            for folder in folders:
                info = Path(directory, folder).stat(follow_symlinks=False)
                if getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    raise ValueError('Archive contains a directory reparse point; inspect explicitly.')
            for name in sorted(names):
                yield Path(directory, name)

    def verify_safely(original):
        try:
            return verify(original)
        except (OSError, ValueError) as error:
            return {'path': str(original.relative_to(source)), 'error': str(error)}

    files, total, errors = 0, 0, []
    with Path(args.report).open('w', encoding='utf-8') as report, concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as executor:
        def results():
            pending = iter(originals())
            while batch := list(itertools.islice(pending, args.workers * 16)):
                yield from executor.map(verify_safely, batch)

        for result in results():
            report.write(json.dumps(result) + '\n')
            if 'error' in result:
                errors.append(result)
            else:
                files += 1
                total += result['bytes']
        report.write(json.dumps({'verifiedUtc': datetime.now(timezone.utc).isoformat(),
                                 'source': str(source), 'destination': str(destination),
                                 'files': files, 'bytes': total, 'state': 'incomplete' if errors else 'verified', 'errors': errors}) + '\n')
    print(json.dumps({'source': str(source), 'files': files, 'bytes': total, 'state': 'incomplete' if errors else 'verified', 'errors': errors}), flush=True)
    if errors:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
