import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile
import zlib
spec=importlib.util.spec_from_file_location('compare',Path(__file__).parents[1]/'compare-wdl-payloads.py')
compare=importlib.util.module_from_spec(spec);spec.loader.exec_module(compare)


def fixture(path,root,payload=b'\x0a\x00\x00\x00',external=False,dimension=''):
    data=bytearray(12288);data[:4]=((2<<8)|1).to_bytes(4,'big')
    encoded=zlib.compress(payload)
    body=b'' if external else encoded
    data[8192:8196]=(len(body)+1).to_bytes(4,'big');data[8196]=130 if external else 2
    data[8197:8197+len(body)]=body
    prefix='/'.join(x for x in [root,dimension,'region'] if x)
    with zipfile.ZipFile(path,'w') as z:
        z.writestr(prefix+'/r.-1.0.mca',data)
        if external:z.writestr(prefix+'/c.-32.0.mcc',encoded)

class PayloadTests(unittest.TestCase):
    def test_external_inline_and_save_folder_independent(self):
        with tempfile.TemporaryDirectory() as folder:
            a,b=Path(folder)/'a.zip',Path(folder)/'b.zip'
            fixture(a,'old');fixture(b,'new',external=True)
            self.assertTrue(compare.compare(a,b)['allAnvilPayloadsIdentical'])
    def test_same_bounds_different_payload_never_reused(self):
        with tempfile.TemporaryDirectory() as folder:
            a,b=Path(folder)/'a.zip',Path(folder)/'b.zip'
            fixture(a,'old');fixture(b,'old',b'changed')
            self.assertFalse(compare.compare(a,b)['allAnvilPayloadsIdentical'])
    def test_root_dimension_not_confused_with_overworld(self):
        with tempfile.TemporaryDirectory() as folder:
            a,b=Path(folder)/'a.zip',Path(folder)/'b.zip'
            fixture(a,'',dimension='DIM-1');fixture(b,'')
            result=compare.compare(a,b)
            self.assertFalse(result['allAnvilPayloadsIdentical'])
            self.assertEqual(result['byKind']['region']['common'],0)

if __name__=='__main__': unittest.main()
