"""Surelog -> UHDM -> normalized IR -> native engine orchestration.

Real UHDM normalization requires a separately compiled UHDM-aware executable.
Never treat mock tests as evidence of SystemVerilog frontend conformance.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
from .uhdm_lower import lower
from .native_ir import Simulation, validate

class PipelineError(RuntimeError): pass

def compile_design(sources, *, top, output_dir, surelog="surelog",
                   normalizer="velosim-uhdm-normalize", threads=1, timeout=120):
    if not sources or not isinstance(top,str) or not top:
        raise ValueError("sources and top required")
    if type(threads)!=int or not 1<=threads<=256:
        raise ValueError("threads must be 1..256")
    out=Path(output_dir).resolve()
    out.mkdir(parents=True,exist_ok=True)
    cmd=[str(surelog),"-parse","-elabuhdm","-sverilog","-top",top,
         "-mt",str(threads),"-odir",str(out/"surelog")]
    cmd += [str(Path(s).resolve()) for s in sources]
    try:
        result=subprocess.run(cmd,capture_output=True,text=True,timeout=timeout)
    except (OSError,subprocess.TimeoutExpired) as exc:
        raise PipelineError(f"Surelog failed to start: {exc}") from exc
    if result.returncode:
        raise PipelineError(f"Surelog failed ({result.returncode}): {result.stderr or result.stdout}")
    artifacts=list((out/"surelog").rglob("surelog.uhdm"))
    if len(artifacts)!=1:
        raise PipelineError(f"expected one UHDM artifact, found {len(artifacts)}")
    normalized=out/"normalized.json"
    try:
        result=subprocess.run([str(normalizer),str(artifacts[0]),str(normalized),top],
                              capture_output=True,text=True,timeout=timeout)
    except (OSError,subprocess.TimeoutExpired) as exc:
        raise PipelineError(f"UHDM normalizer failed to start: {exc}") from exc
    if result.returncode:
        raise PipelineError(f"UHDM normalizer failed ({result.returncode}): {result.stderr or result.stdout}")
    try:
        data=json.loads(normalized.read_text(encoding="utf-8"))
        ir=lower(data)
        validate(ir)
    except (OSError,ValueError,KeyError,TypeError) as exc:
        raise PipelineError(f"UHDM lowering rejected design: {exc}") from exc
    path=out/"design.ir.json"
    path.write_text(json.dumps(ir,indent=2)+"\n",encoding="utf-8")
    return path

def main(argv=None):
    p=argparse.ArgumentParser(description="Experimental Surelog-to-native pipeline")
    p.add_argument("sources",nargs="+")
    p.add_argument("--top",required=True)
    p.add_argument("--output-dir",default="build/surelog-run")
    p.add_argument("--surelog",default=os.environ.get("VELOSIM_SURELOG","surelog"))
    p.add_argument("--normalizer",default=os.environ.get("VELOSIM_UHDM_NORMALIZER","velosim-uhdm-normalize"))
    p.add_argument("--native-lib",required=True)
    p.add_argument("--threads",type=int,default=1)
    p.add_argument("--cycles",type=int,default=8)
    a=p.parse_args(argv)
    try:
        irpath=compile_design(a.sources,top=a.top,output_dir=a.output_dir,
                              surelog=a.surelog,normalizer=a.normalizer,threads=a.threads)
        with Simulation(json.loads(irpath.read_text()),a.native_lib) as sim:
            sim.drive({"clk":(0,0,0),"rst_n":(0,0,0)},a.threads)
            for cycle in range(a.cycles):
                if cycle==1:sim.drive({"rst_n":(1,0,0)},a.threads)
                sim.drive({"clk":(0,0,0)},a.threads)
                sim.drive({"clk":(1,0,0)},a.threads)
                print(cycle,sim.read("count"))
    except (PipelineError,RuntimeError,KeyError,ValueError) as exc:
        p.exit(1,str(exc)+"\n")
if __name__=="__main__":main()
