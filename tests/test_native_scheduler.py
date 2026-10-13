import ctypes
import pathlib
import random
import subprocess
import tempfile
import unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
class SchedulerTests(unittest.TestCase):
 @classmethod
 def setUpClass(cls):
  cls.tmp=tempfile.TemporaryDirectory()
  lib=pathlib.Path(cls.tmp.name)/"libsched.so"
  subprocess.run(["c++","-std=c++20","-O2","-shared","-fPIC","-pthread",str(ROOT/"native/scheduler.cpp"),"-o",str(lib)],check=True)
  cls.api=ctypes.CDLL(str(lib))
  cls.api.vs_sched_new.argtypes=[ctypes.c_uint32];cls.api.vs_sched_new.restype=ctypes.c_void_p
  cls.api.vs_sched_free.argtypes=[ctypes.c_void_p]
  cls.api.vs_sched_add.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_uint8,ctypes.c_uint8]
  cls.api.vs_sched_get.argtypes=[ctypes.c_void_p,ctypes.c_uint32];cls.api.vs_sched_get.restype=ctypes.c_int
  cls.api.vs_sched_drive.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_uint32),ctypes.POINTER(ctypes.c_uint8),ctypes.c_uint32,ctypes.c_uint32]
  cls.api.vs_sched_drive.restype=ctypes.c_int
 @classmethod
 def tearDownClass(cls):cls.tmp.cleanup()
 def new(self,n):
  p=self.api.vs_sched_new(n);self.assertTrue(p);self.addCleanup(self.api.vs_sched_free,p);return p
 def drive(self,p,updates,workers=1):
  ids=(ctypes.c_uint32*len(updates))(*updates.keys())
  vals=(ctypes.c_uint8*len(updates))(*updates.values())
  return self.api.vs_sched_drive(p,ids,vals,len(updates),workers)
 def test_simultaneous_nba_all_threads(self):
  for workers in (1,2,4,8,16):
   p=self.new(4)
   self.assertEqual(self.api.vs_sched_add(p,0,2,1,1,0,0),0)
   self.assertEqual(self.api.vs_sched_add(p,0,3,2,2,0,0),0)
   self.drive(p,{0:0,1:1,2:0,3:0},workers)
   self.assertEqual(self.drive(p,{0:1},workers),2)
   self.assertEqual((self.api.vs_sched_get(p,2),self.api.vs_sched_get(p,3)),(1,0))
   self.drive(p,{0:0,1:0},workers);self.drive(p,{0:1},workers)
   self.assertEqual((self.api.vs_sched_get(p,2),self.api.vs_sched_get(p,3)),(0,1))
 def test_multiclock_randomized(self):
  histories=[]
  for workers in (1,2,4,8,16):
   p=self.new(6)
   self.api.vs_sched_add(p,0,4,2,2,0,0)
   self.api.vs_sched_add(p,1,5,3,3,1,0)
   self.drive(p,{0:0,1:1,2:0,3:0},workers)
   r=random.Random(0xC0C07B);hist=[]
   for _ in range(500):
    self.drive(p,{0:r.randrange(4),1:r.randrange(4),2:r.randrange(4),3:r.randrange(4)},workers)
    hist.append((self.api.vs_sched_get(p,4),self.api.vs_sched_get(p,5)))
   histories.append(hist)
  self.assertTrue(all(h==histories[0] for h in histories))
 def test_invalid_drive_is_atomic(self):
  p=self.new(3);self.drive(p,{0:0,1:1})
  self.assertEqual(self.drive(p,{0:7}),-2)
  self.assertEqual(self.api.vs_sched_get(p,0),0)
 def test_four_state_edges(self):
  for before,after,expected in [(0,1,1),(0,2,1),(0,3,1),(2,1,1),(3,1,1),(1,0,0),(1,2,0)]:
   p=self.new(3);self.api.vs_sched_add(p,0,2,1,1,0,0)
   self.drive(p,{0:before,1:1})
   self.assertEqual(self.drive(p,{0:after}),expected)
if __name__=="__main__":unittest.main()
