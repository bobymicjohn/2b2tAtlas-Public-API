"""Compare saved Anvil payloads before proposing reuse across exhibits.
Equal bounds, block counts, names or landing coordinates are not sufficient.
Reads ZIPs only; never merges captures, changes dates, or publishes anything.
"""
import argparse
import hashlib
import json
import re
import zlib
import zipfile
from pathlib import Path

REGION=re.compile(r'^(.*?)(region|entities|poi)/r\.(-?\d+)\.(-?\d+)\.mca$')


def payload_hashes(path):
    hashes={}
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            match=REGION.fullmatch(entry.filename)
            if not match: continue
            prefix,kind,rx,rz=match.groups();rx=int(rx);rz=int(rz)
            # Normalize only the top-level save folder. Keep dimensions distinct.
            pieces=prefix.strip('/').split('/')
            dimension='/'.join(pieces if pieces[0] in ['DIM-1','DIM1','dimensions'] else pieces[1:])
            if entry.file_size<8192 or entry.file_size>512*1024**2: raise ValueError('Invalid region size')
            data=archive.read(entry)
            for slot in range(1024):
                loc=int.from_bytes(data[slot*4:slot*4+4],'big')
                if not loc: continue
                offset=(loc>>8)*4096; sectors=loc&255
                if offset<8192 or sectors==0 or offset+sectors*4096>len(data): raise ValueError('Invalid slot')
                size=int.from_bytes(data[offset:offset+4],'big');encoding=data[offset+4]
                if size<1 or size+4>sectors*4096: raise ValueError('Invalid payload length')
                x,z=rx*32+slot%32,rz*32+slot//32
                payload=archive.read(f'{prefix}{kind}/c.{x}.{z}.mcc') if encoding&128 else data[offset+5:offset+4+size]
                code=encoding&127
                if code in [1,2]:
                    decoder=zlib.decompressobj(31 if code==1 else 15)
                    payload=decoder.decompress(payload,32*1024**2+1)
                    if not decoder.eof or len(payload)>32*1024**2: raise ValueError('Invalid or oversized NBT payload')
                elif code!=3: raise ValueError('Unsupported compression')
                if len(payload)>32*1024**2: raise ValueError('Oversized NBT payload')
                key=(dimension,kind,x,z)
                if key in hashes: raise ValueError('Duplicate dimension/kind/chunk')
                hashes[key]=hashlib.sha256(payload).hexdigest()
    if not hashes: raise ValueError('No Anvil payloads')
    return hashes


def compare(left,right):
    a,b=payload_hashes(left),payload_hashes(right)
    result={}
    for kind in ['region','entities','poi']:
        ka={k for k in a if k[1]==kind};kb={k for k in b if k[1]==kind};both=ka&kb
        same=sum(a[k]==b[k] for k in both)
        result[kind]={'left':len(ka),'right':len(kb),'common':len(both),'identicalNbtPayloads':same,
                      'differentNbtPayloads':len(both)-same,'leftOnly':len(ka-kb),'rightOnly':len(kb-ka)}
    return {'allAnvilPayloadsIdentical':a==b,'reuseRequiresReviewedSourceIdentity':True,'byKind':result}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('left',type=Path);parser.add_argument('right',type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    report=compare(args.left,args.right)
    with args.output.open('x',encoding='utf-8') as f: json.dump(report,f,indent=2)
    print(json.dumps(report))
