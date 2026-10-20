"""Native engine stress tests with independent reference calculations."""
import random
import sys
import tempfile
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
from velosim.native_ir import Simulation,build_native_library

def design(width,operation="increment",edge="posedge"):
 return {"format":"velosim.ir.v1","signals":[
 {"name":"clk","width":1,"value":0},
 {"name":"rst","width":1,"value":1},
 {"name":"src","width":width,"value":0},
 {"name":"dst","width":width,"value":0}],
 "processes":[{"clock":"clk","destination":"dst","source":"src","reset":"rst","edge":edge,"operation":operation}]}

class NativeStress(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.tmp=tempfile.TemporaryDirectory()
  cls.lib=build_native_library(ROOT/"native/ir/engine.cpp",Path(cls.tmp.name)/"libir.so")
 @classmethod
 def tearDownClass(cls):cls.tmp.cleanup()
 def test_exhaustive_8bit_increment(self):
  for workers in (1,2,4,8,16):
   with Simulation(design(8),self.lib) as sim:
    for v in range(256):
     sim.drive({"clk":(0,0,0),"src":(v,0,0)},workers)
     sim.drive({"clk":(1,0,0)},workers)
     self.assertEqual(sim.read("dst"),(8,(v+1)&255,0,0))
 def test_64bit_boundary(self):
  with Simulation(design(64),self.lib) as sim:
   for v in (0,(1<<32)-1,(1<<63)-1,(1<<64)-1):
    sim.drive({"clk":(0,0,0),"src":(v,0,0)})
    sim.drive({"clk":(1,0,0)})
    self.assertEqual(sim.read("dst"),(64,(v+1)&((1<<64)-1),0,0))
 def test_random_four_state_oracle(self):
  rng=random.Random(20261012)
  with Simulation(design(8),self.lib) as sim:
   for _ in range(300):
    digits=[rng.randrange(4) for _ in range(8)]
    v=sum(1<<i for i,d in enumerate(digits) if d==1)
    x=sum(1<<i for i,d in enumerate(digits) if d==2)
    z=sum(1<<i for i,d in enumerate(digits) if d==3)
    sim.drive({"clk":(0,0,0),"src":(v,x,z)})
    sim.drive({"clk":(1,0,0)})
    unknown=x|z
    candidates=[((v|r)+1)&255 for r in range(256) if not r&~unknown]
    ones=255;zeros=255
    for c in candidates:
     ones &= c;zeros &= (~c)&255
    self.assertEqual(sim.read("dst"),(8,ones,255&~(ones|zeros),0))
 def test_unknown_reset_procedural_else(self):
  with Simulation(design(8,"sv_if_reset_increment"),self.lib) as sim:
   sim.drive({"clk":(0,0,0),"rst":(0,1,0),"src":(5,0,0)})
   sim.drive({"clk":(1,0,0)})
   self.assertEqual(sim.read("dst"),(8,6,0,0))
 def test_no_edge_no_update(self):
  with Simulation(design(8,"copy"),self.lib) as sim:
   sim.drive({"clk":(0,0,0),"src":(7,0,0)})
   sim.drive({"clk":(1,0,0)})
   sim.drive({"src":(99,0,0)})
   self.assertEqual(sim.read("dst"),(8,7,0,0))
 def test_negedge_unknown_transitions(self):
  with Simulation(design(8,"copy","negedge"),self.lib) as sim:
   sim.drive({"clk":(1,0,0),"src":(17,0,0)})
   sim.drive({"clk":(0,1,0)})
   self.assertEqual(sim.read("dst")[1],17)
   sim.drive({"clk":(0,0,0),"src":(29,0,0)})
   self.assertEqual(sim.read("dst")[1],29)
if __name__=="__main__":unittest.main()
