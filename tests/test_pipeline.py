"""Mock Surelog/UHDM integration tests; native C++ execution is real."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
from velosim.pipeline import compile_design,PipelineError
from velosim.native_ir import build_native_library,Simulation

class PipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory()
        cls.lib=build_native_library(ROOT/"native/ir/engine.cpp",Path(cls.temp.name)/"libir.so")
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def setup_tools(self,root,invalid=False):
        root=Path(root)
        surelog=root/"mock-surelog"
        surelog.write_text("""#!/usr/bin/env python3
import pathlib,sys
a=sys.argv
assert '-elabuhdm' in a and '-top' in a
out=pathlib.Path(a[a.index('-odir')+1]);out.mkdir(parents=True,exist_ok=True)
(out/'surelog.uhdm').write_bytes(b'mock UHDM bytes')
""")
        normalizer=root/"mock-normalizer"
        fixture=ROOT/"examples/counter.normalized.json"
        content=fixture.read_text() if not invalid else '{"format":"bad"}'
        normalizer.write_text("""#!/usr/bin/env python3
import pathlib,sys
assert pathlib.Path(sys.argv[1]).read_bytes()==b'mock UHDM bytes'
assert sys.argv[3]=='counter'
pathlib.Path(sys.argv[2]).write_text("""+repr(content)+""")
""")
        surelog.chmod(0o755);normalizer.chmod(0o755)
        return surelog,normalizer
    def test_pipeline_native_counter(self):
        with tempfile.TemporaryDirectory() as td:
            surelog,normalizer=self.setup_tools(td)
            path=compile_design([ROOT/"examples/counter.sv"],top="counter",
                 output_dir=Path(td)/"out",surelog=surelog,normalizer=normalizer,threads=8)
            ir=json.loads(path.read_text())
            self.assertEqual(ir["processes"][0]["operation"],"sv_if_reset_increment_strict")
            with Simulation(ir,self.lib) as sim:
                sim.drive({"clk":(0,0,0),"rst_n":(0,0,0)},8)
                sim.drive({"clk":(1,0,0)},8)
                self.assertEqual(sim.read("count"),(8,0,0,0))
                sim.drive({"clk":(0,0,0),"rst_n":(1,0,0)},8)
                for i in range(1,270):
                    sim.drive({"clk":(1,0,0)},8)
                    self.assertEqual(sim.read("count"),(8,i%256,0,0))
                    sim.drive({"clk":(0,0,0)},8)
    def test_reject_invalid_normalized_data(self):
        with tempfile.TemporaryDirectory() as td:
            surelog,normalizer=self.setup_tools(td,invalid=True)
            with self.assertRaisesRegex(PipelineError,"rejected"):
                compile_design(["counter.sv"],top="counter",output_dir=Path(td)/"out",
                               surelog=surelog,normalizer=normalizer)
    def test_missing_surelog(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaisesRegex(PipelineError,"failed to start"):
                compile_design(["counter.sv"],top="counter",output_dir=Path(td)/"out",
                               surelog="/nonexistent/surelog")
    def test_missing_normalizer(self):
        with tempfile.TemporaryDirectory() as td:
            surelog,_=self.setup_tools(td)
            with self.assertRaisesRegex(PipelineError,"normalizer failed to start"):
                compile_design(["counter.sv"],top="counter",output_dir=Path(td)/"out",
                               surelog=surelog,normalizer="/nonexistent/normalizer")
if __name__=="__main__":unittest.main()
