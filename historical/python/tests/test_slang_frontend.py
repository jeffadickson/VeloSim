import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from velosim.slang_frontend import SlangError, compile_ast, summarize

class SlangFrontendTests(unittest.TestCase):
    def fake_slang(self, root, code):
        path = Path(root) / 'fake-slang'
        path.write_text('#!/usr/bin/env python3\n' + code)
        path.chmod(0o755)
        return path

    def test_valid_ast_and_top(self):
        with tempfile.TemporaryDirectory() as td:
            exe = self.fake_slang(td, "import json,sys\nfrom pathlib import Path\na=sys.argv\nassert '--top' in a and a[a.index('--top')+1]=='dut'\nPath(a[a.index('--ast-json')+1]).write_text(json.dumps({'design':{'kind':'Root','members':[{'kind':'Instance','name':'dut'}]}}))\n")
            result=compile_ast([Path(td)/'dut.sv'], top='dut', slang=exe)
            self.assertEqual(summarize(result)['top_instances'], ['dut'])

    def test_nonzero_diagnostics_propagated(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.fake_slang(td,"import sys\nprint('syntax error',file=sys.stderr)\nsys.exit(2)\n")
            with self.assertRaisesRegex(SlangError,'syntax error'):
                compile_ast(['broken.sv'],slang=exe)

    def test_invalid_json_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.fake_slang(td,"import sys\nfrom pathlib import Path\na=sys.argv\nPath(a[a.index('--ast-json')+1]).write_text('{invalid')\n")
            with self.assertRaisesRegex(SlangError,'invalid slang AST'):
                compile_ast(['dut.sv'],slang=exe)

    def test_missing_executable(self):
        with self.assertRaisesRegex(SlangError,'invocation failed'):
            compile_ast(['dut.sv'],slang='/nonexistent/velosim-slang')

    def test_empty_sources(self):
        with self.assertRaises(ValueError):
            compile_ast([],slang='slang')

    def test_no_shell_interpolation(self):
        with tempfile.TemporaryDirectory() as td:
            exe=self.fake_slang(td,"import sys,json\nfrom pathlib import Path\na=sys.argv\nassert a[-1].endswith('name;echo BAD.sv')\nPath(a[a.index('--ast-json')+1]).write_text(json.dumps({'design':{}}))\n")
            compile_ast([Path(td)/'name;echo BAD.sv'],slang=exe)

if __name__=='__main__':unittest.main()
