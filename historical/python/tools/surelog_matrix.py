"""Reproducible Surelog elaboration matrix; never claims simulation correctness."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from velosim.surelog_frontend import SurelogError, elaborate

CASES = {
    "counter": ("examples/slang_counter.sv", "slang_counter"),
    "fifo": ("examples/surelog_fifo.sv", "surelog_fifo"),
    "multiclock": ("examples/multiclock.sv", "multiclock"),
}

def run_matrix(root: Path, out: Path, threads=(1, 2, 4, 8), executable=None):
    """Run a fresh Surelog elaboration for each fixture and thread count."""
    records = []
    for name, (source, top) in CASES.items():
        for count in threads:
            destination = out / name / f"threads-{count}"
            start = time.perf_counter()
            record = {"case": name, "top": top, "threads": count, "source": source}
            try:
                result = elaborate([root / source], top=top, threads=count,
                                   executable=executable, output_dir=destination)
                record.update(status="pass",
                              uhdm=str(result.uhdm.relative_to(out)),
                              uhdm_bytes=result.uhdm.stat().st_size,
                              sha256=hashlib.sha256(result.uhdm.read_bytes()).hexdigest())
            except (SurelogError, ValueError, OSError) as exc:
                record.update(status="fail", error=str(exc))
            record["elapsed_seconds"] = round(time.perf_counter() - start, 6)
            records.append(record)
    out.mkdir(parents=True, exist_ok=True)
    (out / "matrix.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    return records

def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--surelog")
    parser.add_argument("--output-dir", type=Path, default=Path("build/surelog-matrix"))
    parser.add_argument("--threads", type=int, nargs="+", default=[1, 2, 4, 8])
    args = parser.parse_args(argv)
    if not args.threads or any(x < 1 for x in args.threads):
        parser.error("positive thread counts required")
    records = run_matrix(Path(__file__).resolve().parents[1], args.output_dir,
                         threads=args.threads, executable=args.surelog)
    for record in records:
        print(f"{record['status'].upper():4} {record['case']:12} threads={record['threads']:2} {record['elapsed_seconds']:.3f}s")
    print("Results:", args.output_dir / "matrix.json")
    return int(any(x["status"] != "pass" for x in records))

if __name__ == "__main__":
    raise SystemExit(main())
