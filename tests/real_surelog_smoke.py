"""Opt-in integration smoke test. Requires real Surelog and UHDM builds.

Run explicitly after building dependencies:
  PYTHONPATH=src python3 tests/real_surelog_smoke.py --surelog /path/to/surelog --normalizer /path/to/velosim-uhdm-normalize

This test intentionally fails when dependencies are missing, rather than silently skipping.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import sys
import tempfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"src"))
from velosim.pipeline import compile_design
from velosim.native_ir import Simulation,build_native_library

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--surelog",required=True)
    parser.add_argument("--normalizer",required=True)
    parser.add_argument("--threads",type=int,default=4)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    source=root/"examples/counter.sv"
    if not source.is_file():
        source=root/"examples/slang_counter.sv"
    if not source.is_file():
        raise SystemExit("counter RTL fixture missing")
    with tempfile.TemporaryDirectory(prefix="velosim-real-uhdm-") as tmp:
        tmp=Path(tmp)
        library=build_native_library(root/"native/ir/engine.cpp",tmp/"libir.so")
        irpath=compile_design([source],top="counter" if source.name=="counter.sv" else "slang_counter",
                              output_dir=tmp/"build",surelog=args.surelog,
                              normalizer=args.normalizer,threads=args.threads)
        ir=json.loads(irpath.read_text())
        with Simulation(ir,library) as sim:
            sim.drive({"clk":(0,0,0),"rst_n":(0,0,0)},args.threads)
            sim.drive({"clk":(1,0,0)},args.threads)
            assert sim.read("count")== (8,0,0,0),sim.read("count")
            for n in range(1,301):
                sim.drive({"clk":(0,0,0),"rst_n":(1,0,0)},args.threads)
                sim.drive({"clk":(1,0,0)},args.threads)
                assert sim.read("count")== (8,n%256,0,0),(n,sim.read("count"))
        print("PASS: real Surelog -> UHDM -> normalized IR -> native engine, 300 increments")
if __name__=="__main__":
    main()
