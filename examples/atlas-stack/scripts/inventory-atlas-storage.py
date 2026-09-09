"""Read-only streaming inventory of explicit Atlas roots; never follows reparse points."""
import concurrent.futures, datetime, json, os, pathlib, stat, sys

ROOTS = [r'C:\AtlasExample\Api', r'C:\AtlasExample\Ingest', r'C:\AtlasExample\Seo',
         r'D:\AtlasExample\Ingest', r'E:\2b2t', r'E:\AtlasExample', r'F:\AtlasExample', r'F:\AtlasIngest',
         r'X:\AtlasExample', r'X:\AtlasExample\Backups', r'I:\AtlasBackups', r'B:\AtlasExample\Backups']

def measure(root):
    result = dict(path=root, bytes=0, files=0, directories=0, skippedReparsePoints=0, errors=[])
    pending = [root]
    while pending:
        current = pending.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    try:
                        info = entry.stat(follow_symlinks=False)
                        if info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                            result['skippedReparsePoints'] += 1
                        elif entry.is_dir(follow_symlinks=False):
                            pending.append(entry.path)
                            result['directories'] += 1
                        else:
                            result['bytes'] += info.st_size
                            result['files'] += 1
                    except OSError as exc:
                        result['errors'].append(str(exc))
        except OSError as exc:
            result['errors'].append(str(exc))
    result['completedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    return result

if __name__ == '__main__':
    output = pathlib.Path(sys.argv[1])
    paths, loose = [], []
    for root in ROOTS:
        try:
            with os.scandir(root) as entries:
                total = dict(path=root + '\\[root files]', bytes=0, files=0, errors=[])
                for entry in entries:
                    if entry.is_dir(follow_symlinks=False):
                        if not entry.stat(follow_symlinks=False).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                            paths.append(entry.path)
                    else:
                        total['bytes'] += entry.stat(follow_symlinks=False).st_size
                        total['files'] += 1
                loose.append(total)
        except OSError as exc:
            loose.append(dict(path=root, errors=[str(exc)]))
    with output.open('w', encoding='utf-8') as report:
        for result in loose:
            report.write(json.dumps(result) + '\n')
        report.flush()
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            for task in concurrent.futures.as_completed([pool.submit(measure, path) for path in paths]):
                result = task.result()
                report.write(json.dumps(result) + '\n')
                report.flush()
                print(f"{result['path']}: {result['bytes'] / 2**30:.3f} GiB, {result['files']} files, {len(result['errors'])} errors", flush=True)
