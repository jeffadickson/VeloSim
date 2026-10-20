"""Opt-in regressions against actual Surelog/UHDM, with no mock frontend.

Set VELOSIM_SURELOG and VELOSIM_UHDM_NORMALIZER to built executables.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from velosim.surelog_frontend import elaborate


@unittest.skipUnless(
    os.environ.get("VELOSIM_SURELOG") and os.environ.get("VELOSIM_UHDM_NORMALIZER"),
    "set VELOSIM_SURELOG and VELOSIM_UHDM_NORMALIZER for real UHDM tests",
)
class RealUHDMTests(unittest.TestCase):
    def normalize(self, width=8, assignment="<=", top="counter"):
        with tempfile.TemporaryDirectory(prefix="velosim-uhdm-test-") as directory:
            root = Path(directory)
            source = root / "counter.sv"
            source.write_text(f"""module counter #(parameter int WIDTH={width})(
  input logic clk, rst_n, output logic [WIDTH-1:0] count);
  always_ff @(posedge clk) begin
    if (!rst_n) count {assignment} '0;
    else count {assignment} count + 1'b1;
  end
endmodule
""")
            design = elaborate([source], top="counter", threads=1,
                               executable=os.environ["VELOSIM_SURELOG"],
                               output_dir=root / "elaboration")
            output = root / "normalized.json"
            result = subprocess.run(
                [os.environ["VELOSIM_UHDM_NORMALIZER"], str(design.uhdm), str(output), top],
                capture_output=True, text=True, timeout=30,
            )
            data = json.loads(output.read_text()) if output.exists() else None
            return result, data

    def test_qualified_top_and_parameter_width(self):
        result, data = self.normalize(width=16)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual({s["name"]: s["width"] for s in data["signals"]},
                         {"clk": 1, "rst_n": 1, "count": 16})

    def test_reject_unsupported_width(self):
        result, data = self.normalize(width=128)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("width outside 1..64", result.stderr)
        self.assertIsNone(data)

    def test_reject_blocking_assignment(self):
        result, data = self.normalize(assignment="=")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("blocking assignment unsupported", result.stderr)
        self.assertIsNone(data)

    def test_reject_wrong_top(self):
        result, data = self.normalize(top="missing")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("top not found", result.stderr)
        self.assertIsNone(data)


if __name__ == "__main__":
    unittest.main()
