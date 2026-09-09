"""Restyle existing World Pulse rasters without changing their observed-cell mask.

This is a display transform, not a new aggregation of the underlying observations.
Read v1 only; write the candidate to a separate, initially empty directory.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
from itertools import islice
import json
import os
from pathlib import Path
import re
import threading
import time
import uuid

import numpy as np
from PIL import Image

PROFILE = 'nocom-contrast-preview-v2'
PALETTE = ((0.0, (120, 109, 255)), (.32, (161, 82, 255)),
           (.64, (255, 59, 212)), (1.0, (255, 243, 251)))
TILE = re.compile(r'^(?:total/(?:overworld|nether|end)|monthly/\d{4}-\d{2}-\d{2}/(?:overworld|nether|end))/-?\d+/-?\d+/-?\d+\.png$')


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def recolor_reference(image):
    """Project legacy colors onto its two palette segments; keep cell support exact.

    Lower pyramid levels already mix colors. This preserves that qualitative
    density order; it deliberately does not infer new observation counts.
    """
    pixels = np.array(image.convert('RGBA'))
    rgb = pixels[:, :, :3].astype(np.float32)
    left = np.array((34, 211, 238), dtype=np.float32)
    middle = np.array((250, 204, 21), dtype=np.float32)
    right = np.array((239, 68, 68), dtype=np.float32)
    def project(start, finish):
        delta = finish-start
        t = np.clip(((rgb-start)*delta).sum(axis=2)/(delta*delta).sum(), 0, 1)
        distance = ((rgb-(start+t[:, :, None]*delta))**2).sum(axis=2)
        return t, distance
    first, d1 = project(left, middle)
    second, d2 = project(middle, right)
    intensity = np.where(d1 <= d2, first*.55, .55+second*.45)
    stops = [p[0] for p in PALETTE]
    for channel in range(3):
        pixels[:, :, channel] = np.rint(np.interp(intensity, stops, [p[1][channel] for p in PALETTE])).astype(np.uint8)
    alpha = pixels[:, :, 3].astype(np.int16)
    # Parent tiles saturate alpha as sparse children are reduced. Basing display
    # opacity on their color instead avoids an opaque blanket at overview zooms.
    pixels[:, :, 3] = np.where(alpha > 0, np.rint(145+105*intensity**.65), 0).astype(np.uint8)
    pixels[alpha == 0] = 0
    return Image.fromarray(pixels)


_color_lut = None


def prepare_color_lut():
    """64 MiB exact RGB lookup, shared by batch threads; same approved pixels."""
    global _color_lut
    if _color_lut is not None:
        return
    table = np.empty((1 << 24, 4), dtype=np.uint8)
    for red in range(256):
        keys = np.arange(red << 16, (red + 1) << 16, dtype=np.uint32)
        rgba = np.empty((256, 256, 4), dtype=np.uint8)
        rgba[:, :, 0] = (keys >> 16).reshape(256, 256)
        rgba[:, :, 1] = ((keys >> 8) & 255).reshape(256, 256)
        rgba[:, :, 2] = (keys & 255).reshape(256, 256)
        rgba[:, :, 3] = 255
        table[red << 16:(red + 1) << 16] = np.array(recolor_reference(Image.fromarray(rgba))).reshape(-1, 4)
    table.flags.writeable = False
    _color_lut = table


def recolor(image):
    if _color_lut is None:
        return recolor_reference(image)
    original = np.array(image.convert('RGBA'))
    rgb = original[:, :, :3].astype(np.uint32)
    keys = (rgb[:, :, 0] << 16) | (rgb[:, :, 1] << 8) | rgb[:, :, 2]
    result = _color_lut[keys]
    result[original[:, :, 3] == 0] = 0
    return Image.fromarray(result)


class Candidate:
    def __init__(self, source, output, workers=4):
        self.source = Path(source).resolve(strict=True)
        self.output = Path(output).resolve()
        if self.source == self.output or self.source in self.output.parents or self.output in self.source.parents:
            raise ValueError('Candidate must be outside the original tile tree')
        self.manifest_hash = sha256(self.source/'manifest.json')
        manifest = json.loads((self.source/'manifest.json').read_text(encoding='utf-8-sig'))
        self.tile_locks = [threading.Lock() for _ in range(64)]
        self.budget = threading.BoundedSemaphore(workers)
        self.output.mkdir(parents=True, exist_ok=True)
        candidate_manifest = self.output/'manifest.json'
        if candidate_manifest.exists():
            prior = json.loads(candidate_manifest.read_text(encoding='utf-8-sig'))
            if prior.get('displayProfile') != PROFILE or prior.get('originalManifestSha256') != self.manifest_hash:
                raise ValueError('Refusing to overwrite a different candidate generation')
        elif any(self.output.iterdir()):
            raise ValueError('Unidentified existing output directory')
        else:
            manifest.update(version=PROFILE, displayProfile=PROFILE, originalManifestSha256=self.manifest_hash,
                            derivedFrom='Nocom/v1', reviewOnly=True, materialization='on-demand',
                            displayNotes=['Same observed-cell alpha mask and native tile coordinates as v1.',
                                          'Recolored qualitative density; no new count aggregation.',
                                          'Crisp overzoom; screen-space glow below map zoom -2 is a visual aid, not additional observed area.'])
            manifest['legend'] = [dict(position=position,color='#%02x%02x%02x'%color) for position,color in PALETTE]
            candidate_manifest.write_text(json.dumps(manifest, indent=2),encoding='utf-8')
        self.manifest = json.loads(candidate_manifest.read_text(encoding='utf-8'))

    def tile(self, relative):
        if not TILE.fullmatch(relative): raise ValueError('Invalid tile path')
        source = self.source/relative
        if not source.is_file(): return None
        output = self.output/relative
        with self.tile_locks[hash(relative) % len(self.tile_locks)]:
            if output.exists(): return output
            with self.budget:
                with Image.open(source) as image: rendered = recolor(image)
                output.parent.mkdir(parents=True,exist_ok=True)
                temporary = output.with_name(output.name+'.'+uuid.uuid4().hex+'.tmp')
                rendered.save(temporary,format='PNG',compress_level=3)
                os.replace(temporary,output)
        return output


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--all',action='store_true',help='Materialize all 42 complete pyramids; otherwise only initialize the candidate')
    parser.add_argument('--workers', type=int, choices=range(1, 17), default=4)
    args=parser.parse_args();candidate=Candidate(args.source,args.output,args.workers)
    if args.all:
        prepare_color_lut()
        started = time.monotonic()
        def render(path): return candidate.tile(path.relative_to(candidate.source).as_posix())
        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            paths = candidate.source.glob('**/*.png')
            index = 0
            while batch := list(islice(paths, 64)):
                for _ in executor.map(render, batch):
                    index += 1
                    if index%10000==0: print(f'Rendered {index:,} candidate tiles; {index/(time.monotonic()-started):.1f} tiles/sec',flush=True)
    print(candidate.output)


if __name__=='__main__': main()
