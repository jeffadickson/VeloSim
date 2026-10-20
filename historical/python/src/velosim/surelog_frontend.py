"""Surelog/UHDM elaboration adapter. No simulation or HDL lowering yet."""
from __future__ import annotations
import argparse
import dataclasses
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

class SurelogError(RuntimeError):
    pass

@dataclasses.dataclass(frozen=True)
class Elaboration:
    uhdm: Path
    log: Path
    command: tuple[str, ...]
    stdout: str
    stderr: str

def elaborate(sources, *, top=None, executable=None, threads=1, output_dir=None,
              include_dirs=(), defines=(), timeout=180, keep=False):
    """Elaborate SystemVerilog to UHDM using the real Surelog executable.

    Output must persist in output_dir. The caller owns its lifecycle.
    """
    if not sources:
        raise ValueError("at least one source is required")
    if not isinstance(threads, int) or not 1 <= threads <= 512:
        raise ValueError("threads must be 1..512")
    exe = executable or os.environ.get("VELOSIM_SURELOG") or shutil.which("surelog")
    if not exe:
        raise SurelogError("Surelog not installed; set VELOSIM_SURELOG")
    if output_dir is None:
        raise ValueError("output_dir required to retain the UHDM database")
    out = Path(output_dir).resolve()
    out.mkdir(parents=True, exist_ok=True)
    cmd = [str(exe), "-parse", "-elabuhdm", "-sverilog",
           "-mt", str(threads), "-odir", str(out)]
    if top:
        cmd += ["-top", top]
    for directory in include_dirs:
        cmd.append("+incdir+" + str(Path(directory).resolve()))
    for define in defines:
        if not define or any(c.isspace() for c in define):
            raise ValueError("invalid macro definition")
        cmd.append("+define+" + define)
    cmd += [str(Path(p).resolve()) for p in sources]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                                check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise SurelogError(f"Surelog invocation failed: {exc}") from exc
    if result.returncode:
        raise SurelogError(f"Surelog failed ({result.returncode}):\n{result.stderr}\n{result.stdout}")
    candidates = sorted(out.rglob("surelog.uhdm"))
    if len(candidates) != 1:
        raise SurelogError(f"Expected one surelog.uhdm under {out}; found {len(candidates)}")
    return Elaboration(candidates[0], out / "surelog.log", tuple(cmd),
                       result.stdout, result.stderr)

def main(argv=None):
    parser = argparse.ArgumentParser(description="VeloSim Surelog/UHDM elaboration")
    parser.add_argument("sources", nargs="+")
    parser.add_argument("--top")
    parser.add_argument("--surelog")
    parser.add_argument("--threads", type=int, default=1)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("-I", dest="include_dirs", action="append", default=[])
    parser.add_argument("-D", dest="defines", action="append", default=[])
    args = parser.parse_args(argv)
    try:
        result = elaborate(args.sources, top=args.top, executable=args.surelog,
                           threads=args.threads, output_dir=args.output_dir,
                           include_dirs=args.include_dirs, defines=args.defines)
    except (ValueError, SurelogError) as exc:
        parser.exit(1, str(exc) + "\n")
    print(result.uhdm)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
