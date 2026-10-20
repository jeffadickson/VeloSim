"""Tests matrix orchestration without claiming real Surelog integration."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("surelog_matrix", ROOT / "tools/surelog_matrix.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

class SurelogMatrixTests(unittest.TestCase):
    def test_three_cases_two_thread_counts(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)
            for source, _ in mod.CASES.values():
                path=root/source
                path.parent.mkdir(parents=True,exist_ok=True)
                path.write_text("module test; endmodule\n")
            mock=root/"mock-surelog"
            mock.write_text("""#!/usr/bin/env python3
import pathlib,sys
a=sys.argv
assert '-parse' in a and '-elabuhdm' in a
assert '-top' in a and '-mt' in a
out=pathlib.Path(a[a.index('-odir')+1]);out.mkdir(parents=True,exist_ok=True)
(out/'surelog.uhdm').write_bytes(b'fixture')
""")
            mock.chmod(0o755)
            records=mod.run_matrix(root,root/"results",threads=(1,4),executable=mock)
            self.assertEqual(len(records),6)
            self.assertTrue(all(x["status"]=="pass" for x in records))
            self.assertEqual(len(json.loads((root/"results/matrix.json").read_text())),6)
    def test_failed_frontend_is_reported(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)
            records=mod.run_matrix(root,root/"results",threads=(1,),
                                   executable="/nonexistent/surelog")
            self.assertEqual(len(records),3)
            self.assertTrue(all(x["status"]=="fail" for x in records))
            self.assertTrue((root/"results/matrix.json").exists())

if __name__ == "__main__":
    unittest.main()
