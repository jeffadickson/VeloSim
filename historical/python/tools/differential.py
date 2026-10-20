"""VeloSim differential regression harness.

Reference HDL is compiled by Icarus/Verilator. VeloSim currently executes a
matching, independently maintained counter IR fixture, NOT the HDL source.
This distinction is recorded in every report.
"""
from __future__ import annotations
import argparse
import json
import random
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
from velosim.native_ir import Simulation,build_native_library

RESULT=re.compile(r"^VS_RESULT\s+(\d+)\s+([01xXzZ]+)\s*$")

def stimulus(seed,cycles):
    if cycles<1:raise ValueError("cycles must be positive")
    rng=random.Random(seed)
    # Each value drives rst_n before a clock rising edge.
    return [0]+[0 if rng.randrange(19)==0 else 1 for _ in range(cycles-1)]

def parse_output(text,expected):
    values={}
    for line in text.splitlines():
        if not line.startswith("VS_RESULT"):continue
        match=RESULT.fullmatch(line.strip())
        if not match:raise ValueError("malformed VS_RESULT: "+line)
        idx=int(match.group(1))
        if idx in values:raise ValueError(f"duplicate cycle {idx}")
        bits=match.group(2).lower()
        if len(bits)>8:raise ValueError("unexpected output width")
        values[idx]=bits.zfill(8)
    if set(values)!=set(range(expected)):
        raise ValueError(f"missing/extra cycles: {sorted(values)}")
    return [values[i] for i in range(expected)]

def execute(command,cwd,timeout=90):
    try:
        result=subprocess.run(command,cwd=cwd,text=True,capture_output=True,timeout=timeout)
    except (OSError,subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"command failed to launch: {command}: {exc}") from exc
    if result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {command}\n{result.stdout}\n{result.stderr}")
    return result.stdout

def run_external(engine,vector,work):
    work=Path(work)
    (work/"stimulus.txt").write_text("".join(str(x)+"\n" for x in vector))
    rtl=ROOT/"verification/differential/counter_tb.sv"
    if engine=="icarus":
        exe=shutil.which("iverilog");runner=shutil.which("vvp")
        if not exe or not runner:raise FileNotFoundError("iverilog/vvp not installed")
        execute([exe,"-g2012","-s","counter_tb","-o","sim.vvp",str(rtl)],work)
        output=execute([runner,"sim.vvp"],work)
    elif engine=="verilator":
        exe=shutil.which("verilator")
        if not exe:raise FileNotFoundError("verilator not installed")
        execute([exe,"--binary","--timing","-Wno-fatal","--top-module","counter_tb",
                 "--Mdir","obj_dir",str(rtl)],work,timeout=180)
        output=execute([str(work/"obj_dir/Vcounter_tb")],work)
    else:raise ValueError("unknown external engine")
    return parse_output(output,len(vector))

def run_native(vector,work,workers=1):
    ir=json.loads((ROOT/"examples/counter.ir.json").read_text())
    # Strict IEEE four-state arithmetic and procedural if behavior.
    ir["processes"][0]["operation"]="sv_if_reset_increment_strict"
    lib=build_native_library(ROOT/"native/ir/engine.cpp",Path(work)/"libir.so")
    output=[]
    with Simulation(ir,lib) as sim:
        for reset in vector:
            sim.drive({"clk":(0,0,0),"rst_n":(reset,0,0)},workers)
            sim.drive({"clk":(1,0,0)},workers)
            width,value,x,z=sim.read("count")
            assert width==8
            bits="".join("z" if z>>i&1 else "x" if x>>i&1 else str(value>>i&1)
                         for i in range(7,-1,-1))
            output.append(bits)
    return output

def classify(a,b):
    if a==b:return "equal"
    if any(c in "xz" for c in a+b):return "four_state_divergence"
    return "binary_mismatch"

def compare(results):
    names=list(results)
    differences=[]
    for i in range(len(next(iter(results.values())))):
        for x in range(len(names)):
            for y in range(x+1,len(names)):
                a,b=names[x],names[y]
                label=classify(results[a][i],results[b][i])
                if label!="equal":
                    differences.append({"cycle":i,"engines":[a,b],
                                        "values":[results[a][i],results[b][i]],
                                        "classification":label})
    return differences

def first_failure_prefix(results):
    diffs=compare(results)
    return None if not diffs else min(d["cycle"] for d in diffs)+1

def run(seed=42,cycles=100,engines=("velosim","icarus","verilator"),workers=1,
        report_dir=Path("build/differential"),require_all=False):
    vector=stimulus(seed,cycles)
    report_dir=Path(report_dir).resolve();report_dir.mkdir(parents=True,exist_ok=True)
    results={};errors={}
    with tempfile.TemporaryDirectory(prefix="velosim-diff-") as td:
        for engine in engines:
            try:
                work=Path(td)/engine;work.mkdir()
                results[engine]=(run_native(vector,work,workers) if engine=="velosim"
                                 else run_external(engine,vector,work))
            except Exception as exc:
                errors[engine]=str(exc)
    differences=compare(results) if len(results)>=2 else []
    prefix=first_failure_prefix(results) if len(results)>=2 else None
    if prefix is not None:
        (report_dir/"minimal_prefix.txt").write_text("".join(f"{x}\n" for x in vector[:prefix]))
    (report_dir/"stimulus.txt").write_text("".join(f"{x}\n" for x in vector))
    status=("fail" if differences else
            "incomplete" if errors or len(results)<2 else "pass")
    data={"schema":"velosim.diff.v1","status":status,"seed":seed,"cycles":cycles,
          "workers":workers,"requested_engines":list(engines),
          "executed_engines":list(results),"errors":errors,
          "results":results,"differences":differences,
          "minimal_failing_prefix_cycles":prefix,
          "velosim_source_mode":"hand_authored_ir_fixture_not_hdl_compilation"}
    (report_dir/"report.json").write_text(json.dumps(data,indent=2)+"\n")
    if require_all and errors:status="fail"
    return status,data

def main():
    p=argparse.ArgumentParser()
    p.add_argument("--seed",type=int,default=42)
    p.add_argument("--cycles",type=int,default=100)
    p.add_argument("--threads",type=int,default=1)
    p.add_argument("--engines",default="velosim,icarus,verilator")
    p.add_argument("--report-dir",type=Path,default=Path("build/differential"))
    p.add_argument("--require-all",action="store_true")
    args=p.parse_args()
    try:
        engines=tuple(x.strip() for x in args.engines.split(","))
        if not engines or any(x not in {"velosim","icarus","verilator"} for x in engines):
            p.error("invalid engines")
        status,data=run(args.seed,args.cycles,engines,args.threads,args.report_dir,args.require_all)
    except (ValueError,OSError) as exc:
        p.exit(2,str(exc)+"\n")
    print(json.dumps({"status":status,"engines":data["executed_engines"],
                      "errors":data["errors"],"mismatches":len(data["differences"]),
                      "report":str(args.report_dir/"report.json")},indent=2))
    return 0 if status=="pass" else 1

if __name__=="__main__":
    raise SystemExit(main())
