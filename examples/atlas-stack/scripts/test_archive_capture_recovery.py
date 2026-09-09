import json
from pathlib import Path
import tempfile
import unittest
import zipfile
import archive_capture_recovery as recovery
from test_archive_capture_resume import archive, chunk
from archive_capture_resume import merge, members, slots, pack


class RecoveryTests(unittest.TestCase):
    def test_open_region_padding_keeps_payloads_and_rejects_truncation(self):
        chunks={0:chunk('persisted')}
        complete=pack(chunks)
        end=8192+len(chunks[0][0])
        unpadded=complete[:end]
        self.assertEqual(recovery.normalize_open_region(unpadded),complete)
        self.assertEqual(slots(recovery.normalize_open_region(unpadded)),chunks)
        with self.assertRaises(ValueError): recovery.normalize_open_region(unpadded[:-1])
        with self.assertRaises(ValueError): recovery.normalize_open_region(unpadded[:8191])
    def test_interrupted_zip_uses_working_save_and_parent_without_losing_overlap(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);name='archive-interrupted'
            old=root/'parent.zip';working=root/(name+'.zip')
            archive(old,{0:chunk('old'),1:chunk('stale')})
            archive(working,{1:chunk('fresh'),2:chunk('new')},root=name)
            with zipfile.ZipFile(working) as z: z.extractall(root)
            working.write_bytes(working.read_bytes()[:40])
            before=[recovery.digest(p) for p in (old,working)]
            request=dict(preservedRoot=str(root),destination=str(root/'recovered'),captureName=name,
                         dimension='Overworld',seeds=[dict(path=str(old),sha256=recovery.digest(old))])
            result=recovery.recover(request)
            self.assertEqual(result['savedChunks'],3)
            self.assertFalse((root/'recovered'/'union-0.zip').exists())
            with zipfile.ZipFile(result['zipPath']) as z:
                _,index=members(z)
                self.assertEqual(slots(z.read(index['region/r.-1.0.mca'])),{0:chunk('old'),1:chunk('fresh'),2:chunk('new')})
            self.assertEqual(before,[recovery.digest(p) for p in (old,working)])
            # A second interrupted continuation includes its previous union.
            request['destination']=str(root/'again')
            request['seeds']=[dict(path=result['zipPath'],sha256=result['zipSha256'])]
            self.assertEqual(recovery.recover(request)['savedChunks'],3)

    def test_sparse_plan_preserves_full_expected_set_through_merge(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);old=root/'old.zip';new=root/'new.zip';out=root/'out.zip'
            archive(old,{0:chunk('a'),2:chunk('c')});archive(new,{1:chunk('b')})
            expected=root/'expected.csv';expected.write_text('-32,0\n-31,0\n-30,0\n')
            request=dict(sourcePath=str(old),dimension='Overworld',liveDimension='minecraft:overworld',
                         bounds=[-512,0,0,16],expectedChunksPath=str(expected),radius=8,planPath=str(root/'plan.json'))
            original=expected.read_bytes()
            plan=recovery.repair_plan(request)
            self.assertEqual((plan['expected'],plan['present'],plan['missing']),(3,2,1))
            self.assertEqual(json.loads((root/'plan.json').read_text())['chunks'],[dict(x=-31,z=0)])
            merge(old,new,out,'archive-completed');request['sourcePath']=str(out)
            self.assertEqual(recovery.repair_plan(request)['missing'],0)
            self.assertEqual(expected.read_bytes(),original)
            expected.write_text('-32,0\n-32,0\n')
            with self.assertRaises(ValueError): recovery.repair_plan(request)
            expected.write_text('0,0\n')
            with self.assertRaises(ValueError): recovery.repair_plan(request)

    def test_invalid_seed_or_dimension_never_discarded(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);seed=root/'seed.zip';archive(seed,{0:chunk('a')})
            request=dict(preservedRoot=str(root),destination=str(root/'bad'),captureName='archive-test',
                         dimension='Overworld',seeds=[dict(path=str(seed),sha256='0'*64)])
            original=seed.read_bytes()
            with self.assertRaises(ValueError): recovery.recover(request)
            with self.assertRaises(ValueError): recovery.inventory(seed,'Nether')
            self.assertEqual(seed.read_bytes(),original)

    def test_working_save_without_report_does_not_forge_completion(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);source=root/'source.zip';name='archive-working'
            archive(source,{0:chunk('a')},root=name)
            with zipfile.ZipFile(source) as z: z.extractall(root)
            (root/name/'wdl/download.jsonl').unlink()
            result=recovery.recover(dict(preservedRoot=str(root),destination=str(root/'result'),captureName=name,dimension='Overworld'))
            self.assertFalse((root/'result'/'working-save.zip').exists())
            with zipfile.ZipFile(result['zipPath']) as z:
                report=json.loads(z.read(name+'/wdl/download.jsonl'))
                self.assertEqual(report['status'],'interrupted')

    def test_seed_only_recovery_keeps_external_seed(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);seed=root/'seed.zip';archive(seed,{0:chunk('a')})
            before=seed.read_bytes()
            result=recovery.recover(dict(preservedRoot=str(root),destination=str(root/'result'),
                captureName='archive-test',dimension='Overworld',seeds=[dict(path=str(seed),sha256=recovery.digest(seed))]))
            self.assertEqual(seed.read_bytes(),before)
            self.assertEqual(Path(result['zipPath']).read_bytes(),before)

    def test_regions_before_metadata_flush_resume_then_require_complete_continuation(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);name='archive-no-metadata';source=root/'source.zip'
            archive(source,{0:chunk('persisted')},root=name)
            with zipfile.ZipFile(source) as z: z.extractall(root)
            (root/name/'level.dat').unlink();(root/name/'wdl/download.jsonl').unlink()
            terrain=root/name/'region/r.-1.0.mca';before=terrain.read_bytes()
            # Simulate a writer stopped before its final sector padding flush.
            before=before[:8192+len(chunk('persisted')[0])];terrain.write_bytes(before)
            result=recovery.recover(dict(preservedRoot=str(root),destination=str(root/'result'),
                captureName=name,dimension='Overworld'))
            self.assertEqual(result['savedChunks'],1)
            self.assertEqual(result['normalizedRegions'],1)
            self.assertEqual(terrain.read_bytes(),before)
            self.assertFalse((root/name/'level.dat').exists())
            with zipfile.ZipFile(result['zipPath']) as z:
                with self.assertRaises(ValueError): members(z)
                _,index=members(z,allow_partial=True)
                report=json.loads(z.read(index['wdl/download.jsonl']))
                self.assertEqual(report['status'],'interrupted');self.assertTrue(report['missingLevelDat'])
            # Still a private partial if another disconnect happens before metadata.
            again=recovery.recover(dict(preservedRoot=str(root),destination=str(root/'again'),
                captureName=name,dimension='Overworld',seeds=[dict(path=result['zipPath'],sha256=result['zipSha256'])]))
            self.assertEqual(again['savedChunks'],1)
            continuation=root/'complete.zip';archive(continuation,{1:chunk('new')},root='archive-complete')
            output=root/'union.zip';merge(result['zipPath'],continuation,output,'archive-complete')
            with zipfile.ZipFile(output) as z:
                _,index=members(z)
                self.assertEqual(slots(z.read(index['region/r.-1.0.mca'])),{0:chunk('persisted'),1:chunk('new')})
            with self.assertRaises(ValueError):
                merge(continuation,result['zipPath'],root/'invalid.zip','archive-incomplete')


if __name__ == '__main__': unittest.main()
