"""Harness tests; no external simulators needed for these tests."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location("differential",ROOT/"tools/differential.py")
diff=importlib.util.module_from_spec(spec);spec.loader.exec_module(diff)
class DifferentialTests(unittest.TestCase):
 def test_stimulus_reproducible(self):
  a=diff.stimulus(91,1000)
  self.assertEqual(a,diff.stimulus(91,1000))
  self.assertNotEqual(a,diff.stimulus(92,1000))
  self.assertEqual(a[0],0)
 def test_output_parse(self):
  self.assertEqual(diff.parse_output("header\nVS_RESULT 0 00000000\nVS_RESULT 1 00000001\n",2),
                   ["00000000","00000001"])
  with self.assertRaises(ValueError):
   diff.parse_output("VS_RESULT 0 0\nVS_RESULT 0 1\n",2)
  with self.assertRaises(ValueError):
   diff.parse_output("VS_RESULT 1 1\n",2)
 def test_mismatch_classification(self):
  self.assertEqual(diff.classify("0000000x","00000000"),"four_state_divergence")
  self.assertEqual(diff.classify("00000001","00000000"),"binary_mismatch")
 def test_minimal_prefix(self):
  result={"a":["0","1","1"],"b":["0","0","1"]}
  self.assertEqual(diff.first_failure_prefix(result),2)
  self.assertIsNone(diff.first_failure_prefix({"a":["0"],"b":["0"]}))
 def test_native_run_and_report_incomplete_without_references(self):
  with tempfile.TemporaryDirectory() as td:
   status,report=diff.run(seed=7,cycles=64,engines=("velosim",),
                          report_dir=Path(td))
   self.assertEqual(status,"incomplete")
   self.assertEqual(len(report["results"]["velosim"]),64)
   self.assertTrue((Path(td)/"report.json").exists())
 def test_mocked_engines_mismatch_report(self):
  original=diff.run_external
  try:
   diff.run_external=lambda engine,vector,work:["00000000"]*len(vector)
   with tempfile.TemporaryDirectory() as td:
    status,report=diff.run(seed=7,cycles=6,engines=("velosim","icarus"),
                           report_dir=Path(td))
    self.assertEqual(status,"fail")
    self.assertEqual(report["minimal_failing_prefix_cycles"],2)
    self.assertEqual((Path(td)/"minimal_prefix.txt").read_text().splitlines(),
                     ["0","1"])
  finally:diff.run_external=original
if __name__=="__main__":unittest.main()
