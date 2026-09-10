"""Propose an exhibit crop from saved evidence. Never changes or publishes a WDL.
Requires numpy, scipy and matplotlib. Coordinates must be the actual Archive warp
landing for this dated save, not a potentially merged Atlas location coordinate.
"""
import argparse
import csv
import gzip
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from scipy import ndimage


def propose(rows, center_x, center_z, margin=8):
    if not rows:
        raise ValueError("No saved chunks")
    xs = [r[0] for r in rows]; zs = [r[1] for r in rows]
    x0, z0 = min(xs), min(zs)
    shape = (max(zs)-z0+1, max(xs)-x0+1)
    if math.prod(shape) > 4_000_000:
        raise ValueError("Review the regional save in smaller windows (4M-cell analysis limit)")
    saved = np.zeros(shape, dtype=bool)
    built = np.zeros(shape, dtype=bool)
    for x,z,nonvoid,artificial,strong,entities in rows:
        saved[z-z0,x-x0] = True
        built[z-z0,x-x0] = entities > 0 or strong >= 2 or artificial >= 24
    # A 1-2 chunk road cannot join two dense exhibits. This is only a proposal:
    # thin sculptures, farms and separate outbuildings may need a wider crop.
    density = ndimage.convolve(built.astype(np.int16), np.ones((5,5),dtype=np.int16), mode='constant')
    cores = built & (density >= 13)
    labels, count = ndimage.label(cores, structure=np.ones((3,3)))
    cx, cz = math.floor(center_x/16), math.floor(center_z/16)
    if not (x0 <= cx < x0+shape[1] and z0 <= cz < z0+shape[0]) or not saved[cz-z0,cx-x0]:
        raise ValueError("Archive landing is not a saved chunk; resolve source/coordinate identity first")
    coords = np.argwhere(cores)
    warnings = []
    selected = np.zeros(shape,dtype=bool)
    if len(coords):
        distances = (coords[:,0]+z0-cz)**2 + (coords[:,1]+x0-cx)**2
        nearest = coords[np.argmin(distances)]
        if distances.min() <= 16**2:
            selected = labels == labels[tuple(nearest)]
            # Recover sparse building edges close to the dense core, without
            # recursively following the recovered road to another component.
            selected |= built & ndimage.binary_dilation(selected, iterations=2)
        else:
            warnings.append("No dense structure within 256 blocks of the actual landing")
    if not selected.any():
        warnings.append("No reliable density boundary: this is a landing-context candidate only")
        low_x, high_x, low_z, high_z = cx-16,cx+15,cz-16,cz+15
    else:
        zz,xx=np.where(selected)
        low_x,high_x=min(int(xx.min())+x0,cx),max(int(xx.max())+x0,cx)
        low_z,high_z=min(int(zz.min())+z0,cz),max(int(zz.max())+z0,cz)
    bounds = [max(x0,low_x-margin),max(z0,low_z-margin),
              min(x0+shape[1],high_x+margin+1),min(z0+shape[0],high_z+margin+1)]
    x1,z1,x2,z2=bounds
    included=np.zeros(shape,dtype=bool);included[z1-z0:z2-z0,x1-x0:x2-x0]=True
    nearby=np.zeros(shape,dtype=bool)
    nearby[max(0,z1-z0-32):min(shape[0],z2-z0+32),max(0,x1-x0-32):min(shape[1],x2-x0+32)]=True
    nearby_excluded=int(np.count_nonzero(nearby & built & ~included))
    if nearby_excluded >= 16:
        warnings.append(f"{nearby_excluded} construction-evidence chunks within 512 blocks lie outside the candidate; check detached buildings before accepting")
    missing=int(np.count_nonzero(included & ~saved))
    if missing: warnings.append(f"Candidate rectangle has {missing} unsaved chunks; do not call it complete")
    if (x2-x1)*(z2-z1)>16384: warnings.append("Dense component still spans a large region; inspect manually")
    result={"status":"needs-footprint-review", "method":"landing-density-v1", "marginChunks":margin,
            "landingBlocks":[center_x,center_z], "bounds":[v*16 for v in bounds],
            "originalChunks":len(rows), "candidateChunks":int(np.count_nonzero(saved & included)),
            "unsavedInsideCandidate":missing,"originalBuildChunks":int(built.sum()),
            "buildChunksOutsideCandidate":int(np.count_nonzero(built & ~included)),
            "denseComponents":int(count),"nearbyBuildChunksOutsideCandidate":nearby_excluded,"warnings":warnings,
            "approvalRequiredBeforePublication":True}
    return result,(saved,built,selected,included,x0,z0)


def sha256(path):
    digest=hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''): digest.update(block)
    return digest.hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',required=True,type=Path)
    parser.add_argument('--evidence',required=True,type=Path)
    parser.add_argument('--center-x',required=True,type=float)
    parser.add_argument('--center-z',required=True,type=float)
    parser.add_argument('--name',required=True)
    parser.add_argument('--output-dir',required=True,type=Path)
    args=parser.parse_args()
    source_hash=sha256(args.source)
    with gzip.open(args.evidence,'rt') as f:
        if f.readline().strip() != '# source-sha256='+source_hash:
            raise ValueError('Evidence does not belong to this source ZIP; export it again')
        height_line=f.readline().strip()
        if not height_line.startswith('# analysis-y='): raise ValueError('Missing analysis height metadata')
        analysis_heights=list(map(int,height_line.split('=',1)[1].split(',')))
        reader=csv.reader(f)
        if next(reader)!=['x','z','nonvoid','artificial','strong','block_entities']: raise ValueError('Unexpected evidence schema')
        rows=[tuple(map(int,row)) for row in reader]
    if len({r[:2] for r in rows})!=len(rows): raise ValueError('Duplicate chunk evidence')
    # Refuse to mix a rerun with earlier candidates.
    args.output_dir.mkdir(parents=True,exist_ok=False)
    result,grids=propose(rows,args.center_x,args.center_z)
    result.update(name=args.name,analysisYRange=analysis_heights,retainsFullChunkHeight=True,source=str(args.source.resolve()),sourceSha256=source_hash,
                  evidenceSha256=sha256(args.evidence))
    (args.output_dir/'proposal.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.patches import Rectangle
    saved,built,selected,included,x0,z0=grids
    pixels=np.zeros((*saved.shape,3)) + [0.05,0.07,0.09]
    pixels[saved]=[0.18,0.23,0.26];pixels[built]=[0.90,0.60,0.25];pixels[selected]=[0.2,0.9,0.72]
    fig,axes=plt.subplots(1,2,figsize=(14,6),layout='constrained')
    x1,z1,x2,z2=result['bounds']
    extent=[x0*16,(x0+saved.shape[1])*16,(z0+saved.shape[0])*16,z0*16]
    for ax in axes:
        ax.imshow(pixels,extent=extent,interpolation='nearest')
        ax.scatter([args.center_x],[args.center_z],c='white',marker='+',s=110,linewidths=2,label='Archive landing')
        ax.add_patch(Rectangle((x1,z1),x2-x1,z2-z1,fill=False,edgecolor='#5eead4',linewidth=2))
        ax.set_xlabel('X (blocks)');ax.set_ylabel('Z (blocks)');ax.ticklabel_format(style='plain',useOffset=False)
    axes[0].set_title(f"Preserved source: {len(rows):,} chunks")
    axes[1].set_xlim(x1-64,x2+64);axes[1].set_ylim(z2+64,z1-64)
    axes[1].set_title(f"Review candidate: {result['candidateChunks']:,} chunks")
    fig.suptitle(args.name+' — boundary proposal, not approved',fontsize=15)
    fig.supxlabel('Orange: construction evidence · Teal: selected dense structure · Gray: saved terrain · White +: actual warp')
    fig.savefig(args.output_dir/'comparison.png',dpi=140)
    plt.close(fig)
    print(json.dumps(result))

if __name__=='__main__': main()
