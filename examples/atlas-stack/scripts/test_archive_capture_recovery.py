import json
from pathlib import Path
import tempfile
import unittest
import zipfile
import archive_capture_recovery as recovery
from test_archive_capture_resume import archive, chunk
from archive_capture_resume import merge, members, slots


class RecoveryTests(unittest.TestCase):
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
            with zipfile.ZipFile(result['zipPath']) as z:
                report=json.loads(z.read(name+'/wdl/download.jsonl'))
                self.assertEqual(report['status'],'interrupted')


if __name__ == '__main__': unittest.main()
