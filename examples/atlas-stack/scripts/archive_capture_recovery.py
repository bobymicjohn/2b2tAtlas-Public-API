"""Private disk recovery and missing-only repair plans. No network or publication."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import zipfile
from archive_capture_resume import REGION, members, merge, slots


def digest(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''): value.update(block)
    return value.hexdigest()


def inventory(path, dimension):
    folder = {'Overworld':'', 'Nether':'DIM-1/', 'End':'DIM1/'}[dimension]
    result = set()
    with zipfile.ZipFile(path) as archive:
        _, index = members(archive)
        for name, entry in index.items():
            match = REGION.fullmatch(name)
            if not match or match[2] != 'region': continue
            if (match[1] or '') != folder: raise ValueError('Capture has an unexpected terrain dimension')
            for slot, (payload, _) in slots(archive.read(entry)).items():
                x, z = int(match[3]) * 32 + slot % 32, int(match[4]) * 32 + slot // 32
                if payload[4] & 128:
                    external = f'{folder}region/c.{x}.{z}.mcc'
                    if external not in index: raise ValueError('Missing external chunk')
                    # Reading validates the ZIP CRC, including external chunks.
                    archive.read(index[external])
                result.add((x,z))
    if not result: raise ValueError('No persisted terrain found')
    return result


def repair_plan(request):
    present = inventory(request['sourcePath'], request['dimension'])
    bounds = request['bounds']
    if len(bounds) != 4 or bounds[0] >= bounds[2] or bounds[1] >= bounds[3]: raise ValueError('Invalid repair bounds')
    expected = set()
    for line in Path(request['expectedChunksPath']).read_text(encoding='utf-8-sig').splitlines():
        line = line.strip()
        if not line or line.startswith('#'): continue
        if not re.fullmatch(r'-?\d+,-?\d+',line): raise ValueError('Invalid expected coordinate')
        x,z = map(int,line.split(','))
        if not (bounds[0]//16 <= x <= (bounds[2]-1)//16 and bounds[1]//16 <= z <= (bounds[3]-1)//16):
            raise ValueError('Expected coordinate outside original bounds')
        if (x,z) in expected: raise ValueError('Duplicate expected coordinate')
        expected.add((x,z))
    if not expected: raise ValueError('Empty expected footprint')
    missing = sorted(expected-present)
    plan = dict(schema=1,dimension=request['liveDimension'],radius=request['radius'],chunks=[dict(x=x,z=z) for x,z in missing])
    Path(request['planPath']).write_text(json.dumps(plan),encoding='utf-8')
    return dict(expected=len(expected),present=len(expected & present),missing=len(missing),sourceSha256=digest(request['sourcePath']))


def pack_working_save(source, output, capture_name, dimension):
    source, output = Path(source), Path(output)
    if not (source/'level.dat').is_file(): raise ValueError('Interrupted save has no level.dat')
    if not re.fullmatch(r'archive-[A-Za-z0-9._-]+',capture_name) or '..' in capture_name: raise ValueError('Invalid capture name')
    with zipfile.ZipFile(output,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=1) as archive:
        for current, directories, files in os.walk(source,followlinks=False):
            for name in [Path(current)] + [Path(current)/n for n in directories+files]:
                if name.is_symlink() or getattr(name.lstat(),'st_file_attributes',0) & 0x400:
                    raise ValueError('Interrupted save contains a reparse point')
            for name in files:
                path = Path(current)/name
                if path.name == 'session.lock': continue
                archive.write(path,capture_name+'/'+path.relative_to(source).as_posix())
        if not (source/'wdl/download.jsonl').exists():
            # This is deliberately not a WDL completion claim. The future merged
            # capture must include a real completed downloader report and audit.
            archive.writestr(capture_name+'/wdl/download.jsonl',json.dumps(dict(status='interrupted',dimensionName=dimension)))
    with output.open('r+b') as stream: os.fsync(stream.fileno())
    inventory(output,dimension)


def recover(request):
    source = Path(request['preservedRoot'])
    destination = Path(request['destination'])
    name = request['captureName']
    if not re.fullmatch(r'archive-[A-Za-z0-9._-]+',name) or '..' in name: raise ValueError('Invalid capture name')
    destination.mkdir(parents=True,exist_ok=False)
    candidates = []
    for seed in request.get('seeds',[]):
        if digest(seed['path']).lower() != seed['sha256'].lower(): raise ValueError('Recovery seed hash mismatch')
        inventory(seed['path'],request['dimension']); candidates.append(Path(seed['path']))
    saved_zip = source/(name+'.zip')
    if saved_zip.exists():
        # A killed ZIP writer may not have produced a central directory. In that
        # case use its intact extracted save; never erase the unfinished ZIP.
        try:
            inventory(saved_zip,request['dimension']);candidates.append(saved_zip)
        except zipfile.BadZipFile:
            if not (source/name).is_dir(): raise
    if (source/name).is_dir():
        packed = destination/'working-save.zip'
        pack_working_save(source/name,packed,name,request['dimension']); candidates.append(packed)
    if not candidates: raise ValueError('No persisted terrain available for recovery')
    combined = candidates[0]
    for number, current in enumerate(candidates[1:]):
        output = destination/f'union-{number}.zip'
        merge(combined,current,output,name);combined=output
    final = destination/'partial-wdl.zip'
    shutil.copyfile(combined,final)
    with final.open('r+b') as stream: os.fsync(stream.fileno())
    coordinates = inventory(final,request['dimension'])
    return dict(zipPath=str(final),zipSha256=digest(final),savedChunks=len(coordinates),
                minChunkX=min(x for x,z in coordinates),maxChunkX=max(x for x,z in coordinates),
                minChunkZ=min(z for x,z in coordinates),maxChunkZ=max(z for x,z in coordinates))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation',choices=['repair','recover'])
    parser.add_argument('request')
    args=parser.parse_args()
    request=json.loads(Path(args.request).read_text(encoding='utf-8-sig'))
    print(json.dumps((repair_plan if args.operation=='repair' else recover)(request)))
