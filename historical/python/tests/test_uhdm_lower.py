"""Normalized UHDM counter lowering integration tests (fixture, not Surelog output)."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'src'))
from velosim.uhdm_lower import lower,UnsupportedUHDM
from velosim.native_ir import Simulation,build_native_library
class UHDMLowerTests(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.tmp=tempfile.TemporaryDirectory()
  cls.lib=build_native_library(ROOT/'native/ir/engine.cpp',Path(cls.tmp.name)/'libir.so')
  cls.fixture=json.loads((ROOT/'examples/counter.normalized.json').read_text())
 @classmethod
 def tearDownClass(cls):cls.tmp.cleanup()
 def test_counter_1_to_16_threads(self):
  ir=lower(self.fixture)
  for workers in (1,2,4,8,16):
   with Simulation(ir,self.lib) as sim:
    sim.drive({'clk':(0,0,0),'rst_n':(0,0,0)},workers)
    sim.drive({'clk':(1,0,0)},workers)
    self.assertEqual(sim.read('count'),(8,0,0,0))
    sim.drive({'clk':(0,0,0),'rst_n':(1,0,0)},workers)
    for i in range(1,300):
     sim.drive({'clk':(1,0,0)},workers)
     self.assertEqual(sim.read('count'),(8,i%256,0,0))
     sim.drive({'clk':(0,0,0)},workers)
 def test_unknown_reset_uses_sv_if(self):
  ir=lower(self.fixture)
  with Simulation(ir,self.lib) as sim:
   sim.drive({'clk':(0,0,0),'rst_n':(0,1,0)})
   sim.drive({'clk':(1,0,0)})
   self.assertEqual(sim.read('count'),(8,1,0,0))
 def test_reject_blocking(self):
  d=copy.deepcopy(self.fixture);d['processes'][0]['body']['else']['kind']='blocking'
  with self.assertRaises(UnsupportedUHDM):lower(d)
 def test_reject_nonincrement(self):
  d=copy.deepcopy(self.fixture);d['processes'][0]['body']['else']['value']['rhs']['value']=2
  with self.assertRaises(UnsupportedUHDM):lower(d)
 def test_reject_initial(self):
  d=copy.deepcopy(self.fixture);d['processes'][0]['kind']='initial'
  with self.assertRaises(UnsupportedUHDM):lower(d)
 def test_reject_multiple_writers(self):
  d=copy.deepcopy(self.fixture);d['processes'].append(copy.deepcopy(d['processes'][0]))
  with self.assertRaises(UnsupportedUHDM):lower(d)
 def test_reject_extra_fields(self):
  d=copy.deepcopy(self.fixture);d['unexpected']='field'
  with self.assertRaises(UnsupportedUHDM):lower(d)
 def test_reject_wide_clock(self):
  d=copy.deepcopy(self.fixture);d['signals'][0]['width']=8
  with self.assertRaises(UnsupportedUHDM):lower(d)
if __name__=='__main__':unittest.main()
