"""Adapter tests use a mock executable; real Surelog integration is separate."""
import pathlib
import sys
import tempfile
import unittest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))
from velosim.surelog_frontend import elaborate, SurelogError

class SurelogFrontendTests(unittest.TestCase):
    def mock(self, directory, code):
        exe = pathlib.Path(directory) / "surelog-mock"
        exe.write_text("#!/usr/bin/env python3\n" + code)
        exe.chmod(0o755)
        return exe

    def test_uhdm_artifact_and_options(self):
        with tempfile.TemporaryDirectory() as td:
            exe = self.mock(td, """import pathlib,sys
a=sys.argv
assert '-parse' in a and '-elabuhdm' in a
assert a[a.index('-top')+1]=='counter'
assert a[a.index('-mt')+1]=='8'
assert '+define+WIDTH=8' in a
out=pathlib.Path(a[a.index('-odir')+1]);out.mkdir(exist_ok=True,parents=True)
(out/'surelog.uhdm').write_bytes(b'uhdm')
""")
            r=elaborate(['counter.sv'], top='counter', threads=8,
                        defines=['WIDTH=8'], output_dir=pathlib.Path(td)/'build',
                        executable=exe)
            self.assertEqual(r.uhdm.read_bytes(),b'uhdm')

    def test_error_propagation(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.mock(td,"import sys\nprint('syntax error',file=sys.stderr)\nsys.exit(2)\n")
            with self.assertRaisesRegex(SurelogError,'syntax error'):
                elaborate(['bad.sv'],output_dir=pathlib.Path(td)/'out',executable=exe)

    def test_missing_uhdm_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.mock(td,"pass\n")
            with self.assertRaisesRegex(SurelogError,'Expected one'):
                elaborate(['a.sv'],output_dir=pathlib.Path(td)/'out',executable=exe)

    def test_validation(self):
        with self.assertRaises(ValueError):
            elaborate([],output_dir='/tmp/unused',executable='surelog')
        with self.assertRaises(ValueError):
            elaborate(['a.sv'],threads=0,output_dir='/tmp/unused',executable='surelog')

    def test_no_shell_interpolation(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.mock(td, """import pathlib,sys
a=sys.argv
assert a[-1].endswith('evil;touch NOT_EXECUTED.sv')
out=pathlib.Path(a[a.index('-odir')+1]);out.mkdir(parents=True,exist_ok=True)
(out/'surelog.uhdm').write_bytes(b'ok')
""")
            elaborate([pathlib.Path(td)/'evil;touch NOT_EXECUTED.sv'],
                      output_dir=pathlib.Path(td)/'out',executable=exe)
            self.assertFalse((pathlib.Path(td)/'NOT_EXECUTED.sv').exists())

if __name__ == '__main__':
    unittest.main()
