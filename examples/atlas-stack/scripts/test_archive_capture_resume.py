import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import archive_capture_resume as m


def chunk(value, external=False):
    data = value.encode()
    return struct.pack('>I', 1 if external else len(data) + 1) + bytes([130 if external else 3]) + (b'' if external else data), bytes(4)


def archive(path, chunks, external=None, root='archive-source'):
    with zipfile.ZipFile(path, 'w') as z:
        z.writestr(root + '/level.dat', b'level')
        z.writestr(root + '/wdl/download.jsonl', json.dumps(dict(status='complete', dimensionName='overworld')))
        z.writestr(root + '/region/r.-1.0.mca', m.pack(chunks))
        z.writestr(root + '/entities/r.-1.0.mca', m.pack(chunks))
        for name, data in (external or {}).items():
            z.writestr(root + '/' + name, data)


class ResumeTests(unittest.TestCase):
    def test_chunk_union_overlap_external_and_repeated_handoff(self):
        with tempfile.TemporaryDirectory() as folder:
            old, new, out, again = [Path(folder) / n for n in ('old.zip','new.zip','out.zip','again.zip')]
            before = {0:chunk('old'), 1:chunk('refresh'), 2:chunk('external', True)}
            after = {1:chunk('newer'), 3:chunk('new')}
            external = {'region/c.-30.0.mcc':b'old-external', 'entities/c.-30.0.mcc':b'old-entities'}
            archive(old, before, external)
            archive(new, after, {name:b'stale-unreferenced' for name in external}, root='archive-next')
            hashes = [hashlib.sha256(p.read_bytes()).hexdigest() for p in (old,new)]
            metrics = m.merge(old,new,out,'archive-merged')
            self.assertEqual(metrics, dict(retainedChunks=2,newChunks=1,replacedChunks=1,totalChunks=4))
            for source in (out, again):
                if source == again: m.merge(out,new,again,'archive-merged')
                with zipfile.ZipFile(source) as z:
                    self.assertEqual(len(z.namelist()),len(set(z.namelist())))
                    for kind in ('region','entities'):
                        self.assertEqual(m.slots(z.read(f'archive-merged/{kind}/r.-1.0.mca')),before | after)
                    for name,data in external.items(): self.assertEqual(z.read('archive-merged/'+name),data)
            self.assertEqual(hashes,[hashlib.sha256(p.read_bytes()).hexdigest() for p in (old,new)])

    def test_completed_empty_continuation_retains_full_parent(self):
        with tempfile.TemporaryDirectory() as folder:
            old,new,out = [Path(folder)/n for n in ('old.zip','empty.zip','out.zip')]
            archive(old,{0:chunk('retained')})
            with zipfile.ZipFile(new,'w') as z:
                z.writestr('archive-empty/level.dat',b'level')
                z.writestr('archive-empty/wdl/download.jsonl',json.dumps(dict(status='complete',chunks=0,dimensionName='overworld')))
            self.assertEqual(m.merge(old,new,out,'archive-merged'),dict(retainedChunks=1,newChunks=0,replacedChunks=0,totalChunks=1))
            with zipfile.ZipFile(out) as z:
                self.assertEqual(m.slots(z.read('archive-merged/region/r.-1.0.mca')),{0:chunk('retained')})

    def test_interruption_keeps_inputs_and_never_commits_partial_output(self):
        with tempfile.TemporaryDirectory() as folder:
            old,new,out = [Path(folder)/n for n in ('old.zip','new.zip','out.zip')]
            archive(old,{0:chunk('old')});archive(new,{1:chunk('new')})
            hashes = [p.read_bytes() for p in (old,new)]
            with patch.object(m.shutil,'copyfileobj',side_effect=OSError('simulated write failure')):
                with self.assertRaises(OSError): m.merge(old,new,out,'archive-merged')
            self.assertFalse(out.exists())
            self.assertEqual(hashes,[p.read_bytes() for p in (old,new)])
            self.assertFalse(list(Path(folder).glob('*.partial')))
            m.merge(old,new,out,'archive-merged')
            with self.assertRaises(ValueError): m.merge(old,new,out,'archive-merged')

    def test_missing_external_and_corrupt_headers_fail_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            old,new,out = [Path(folder)/n for n in ('old.zip','new.zip','out.zip')]
            archive(old,{0:chunk('external',True)});archive(new,{1:chunk('new')})
            with self.assertRaises(ValueError): m.merge(old,new,out,'archive-merged')
            self.assertFalse(out.exists())
            region=bytearray(m.pack({0:chunk('a'),1:chunk('b')}));region[4:8]=region[:4]
            with self.assertRaises(ValueError): m.slots(region)
            with self.assertRaises(ValueError): m.slots(bytes(8191))

    def test_unsafe_member_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/'bad.zip'
            archive(p,{0:chunk('x')})
            with zipfile.ZipFile(p,'a') as z: z.writestr('archive-source/../escape',b'x')
            with zipfile.ZipFile(p) as z:
                with self.assertRaises(ValueError): m.members(z)


if __name__ == '__main__': unittest.main()
