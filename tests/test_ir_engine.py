"""Native multibit process kernel: exhaustive four-state and parallel regressions."""
import ctypes as C
from pathlib import Path
import random
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
class EngineTests(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.tmp=tempfile.TemporaryDirectory()
  path=Path(cls.tmp.name)/'libir.so'
  subprocess.run(['c++','-std=c++20','-O2','-shared','-fPIC','-pthread',str(ROOT/'native/ir/engine.cpp'),'-o',str(path)],check=True)
  cls.lib=C.CDLL(str(path));L=cls.lib
  L.vs_ir_new.restype=C.c_void_p
  L.vs_ir_free.argtypes=[C.c_void_p]
  L.vs_ir_signal.argtypes=[C.c_void_p,C.c_uint32,C.c_uint64,C.c_uint64,C.c_uint64];L.vs_ir_signal.restype=C.c_int
  L.vs_ir_process.argtypes=[C.c_void_p,C.c_uint32,C.c_uint32,C.c_uint32,C.c_uint32,C.c_uint8,C.c_uint8];L.vs_ir_process.restype=C.c_int
  L.vs_ir_read.argtypes=[C.c_void_p,C.c_uint32,C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64)];L.vs_ir_read.restype=C.c_int
  L.vs_ir_drive.argtypes=[C.c_void_p,C.POINTER(C.c_uint32),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.c_uint32,C.c_uint32];L.vs_ir_drive.restype=C.c_int
 @classmethod
 def tearDownClass(cls):cls.tmp.cleanup()
 def new(self):
  p=self.lib.vs_ir_new();self.assertTrue(p);self.addCleanup(self.lib.vs_ir_free,p);return p
 def signal(self,p,w,v=0,x=0,z=0):return self.lib.vs_ir_signal(p,w,v,x,z)
 def proc(self,p,clk,dst,a,reset,edge,op):return self.lib.vs_ir_process(p,clk,dst,a,reset,edge,op)
 def drive(self,p,changes,threads=1):
  ids=(C.c_uint32*len(changes))(*changes.keys());vals=(C.c_uint64*len(changes))(*(v[0] for v in changes.values()))
  xs=(C.c_uint64*len(changes))(*(v[1] for v in changes.values()));zs=(C.c_uint64*len(changes))(*(v[2] for v in changes.values()))
  return self.lib.vs_ir_drive(p,ids,vals,xs,zs,len(changes),threads)
 def read(self,p,id):
  v,x,z=C.c_uint64(),C.c_uint64(),C.c_uint64()
  w=self.lib.vs_ir_read(p,id,C.byref(v),C.byref(x),C.byref(z))
  return w,v.value,x.value,z.value
 def test_counter_wrap_all_workers(self):
  for threads in (1,2,4,8,16):
   p=self.new();clk=self.signal(p,1);rst=self.signal(p,1,1);cnt=self.signal(p,8,254)
   self.assertEqual(self.proc(p,clk,cnt,cnt,rst,0,2),0)
   for expected in (255,0,1):
    self.drive(p,{clk:(0,0,0)},threads);self.assertEqual(self.drive(p,{clk:(1,0,0)},threads),1)
    self.assertEqual(self.read(p,cnt),(8,expected,0,0))
 def test_synchronous_reset(self):
  p=self.new();clk=self.signal(p,1);rst=self.signal(p,1,0);cnt=self.signal(p,16,999)
  self.proc(p,clk,cnt,cnt,rst,0,2);self.drive(p,{clk:(0,0,0)})
  self.drive(p,{clk:(1,0,0)});self.assertEqual(self.read(p,cnt),(16,0,0,0))
  self.drive(p,{clk:(0,0,0),rst:(1,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,cnt),(16,1,0,0))
 def test_nba_uses_precommit_values(self):
  for threads in (1,2,4,8,16):
   p=self.new();clk=self.signal(p,1);dummy=self.signal(p,1);a=self.signal(p,8,7);b=self.signal(p,8,99)
   self.proc(p,clk,a,b,dummy,0,0);self.proc(p,clk,b,a,dummy,0,0)
   self.drive(p,{clk:(0,0,0)},threads);self.drive(p,{clk:(1,0,0)},threads)
   self.assertEqual(self.read(p,a)[1],99);self.assertEqual(self.read(p,b)[1],7)
 def test_four_state_copy(self):
  p=self.new();clk=self.signal(p,1);dummy=self.signal(p,1);a=self.signal(p,8,0b1010,0b0100,0b0001);b=self.signal(p,8)
  self.proc(p,clk,b,a,dummy,0,0);self.drive(p,{clk:(0,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,b),(8,0b1010,0b0100,0b0001))
 def test_unknown_arithmetic_and_reset(self):
  p=self.new();clk=self.signal(p,1);rst=self.signal(p,1,1);cnt=self.signal(p,8,5,4)
  self.proc(p,clk,cnt,cnt,rst,0,2);self.drive(p,{clk:(0,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,cnt),(8,2,4,0))
  self.drive(p,{clk:(0,0,0),rst:(0,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,cnt),(8,0,0,0))
 def test_partial_unknown_increment(self):
  for threads in (1,2,4,8,16):
   p=self.new();clk=self.signal(p,1);dummy=self.signal(p,1);src=self.signal(p,8,4,8);dst=self.signal(p,8)
   self.assertEqual(self.proc(p,clk,dst,src,dummy,0,1),0)
   self.drive(p,{clk:(0,0,0)},threads);self.drive(p,{clk:(1,0,0)},threads)
   self.assertEqual(self.read(p,dst),(8,5,8,0))
 def test_unknown_reset_branch_merge(self):
  p=self.new();clk=self.signal(p,1);rst=self.signal(p,1,0,1);cnt=self.signal(p,8,255)
  self.proc(p,clk,cnt,cnt,rst,0,2)
  self.drive(p,{clk:(0,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,cnt),(8,0,0,0))
 def test_unknown_carry_propagation(self):
  p=self.new();clk=self.signal(p,1);dummy=self.signal(p,1);src=self.signal(p,8,2,1);dst=self.signal(p,8)
  self.proc(p,clk,dst,src,dummy,0,1)
  self.drive(p,{clk:(0,0,0)});self.drive(p,{clk:(1,0,0)})
  self.assertEqual(self.read(p,dst),(8,0,7,0))
 def test_reject_invalid_drive_without_partial_commit(self):
  p=self.new();a=self.signal(p,8,3)
  self.assertEqual(self.drive(p,{a:(5,1,1)}),-2)
  self.assertEqual(self.read(p,a),(8,3,0,0))
 def test_randomized_equivalence(self):
  histories=[]
  for threads in (1,2,4,8,16):
   p=self.new();clk=self.signal(p,1);rst=self.signal(p,1,1);cnt=self.signal(p,32,0)
   self.proc(p,clk,cnt,cnt,rst,0,2)
   rng=random.Random(934);history=[]
   for _ in range(300):
    c=rng.randrange(2);r=rng.randrange(2)
    self.drive(p,{clk:(c,0,0),rst:(r,0,0)},threads)
    history.append(self.read(p,cnt))
   histories.append(history)
  self.assertTrue(all(h==histories[0] for h in histories))
 def test_exhaustive_four_state_increment(self):
  # Independent oracle enumerates all 2-state resolutions of X and Z.
  for workers in (1,4,16):
   p=self.new();clk=self.signal(p,1);dummy=self.signal(p,1)
   src=self.signal(p,4);dst=self.signal(p,4)
   self.assertEqual(self.proc(p,clk,dst,src,dummy,0,1),0)
   for code in range(4**4):
    v=x=z=0
    for bit in range(4):
     digit=(code>>(2*bit))&3
     if digit==1:v|=1<<bit
     elif digit==2:x|=1<<bit
     elif digit==3:z|=1<<bit
    self.drive(p,{clk:(0,0,0),src:(v,x,z)},workers)
    self.drive(p,{clk:(1,0,0)},workers)
    unknown=x|z
    results=[((v|resolution)+1)&15 for resolution in range(16) if not resolution&~unknown]
    ones=15;zeros=15
    for result in results:
     ones&=result;zeros&=(~result)&15
    self.assertEqual(self.read(p,dst),(4,ones,15&~(ones|zeros),0),
                     (workers,code,v,x,z))
if __name__=='__main__':unittest.main()
