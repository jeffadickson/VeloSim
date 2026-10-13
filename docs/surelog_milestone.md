# Surelog integration milestone: reproducible elaboration matrix

Run:
```bash
python3 tools/surelog_matrix.py --surelog /path/to/surelog --threads 1 2 4 8
```

The matrix attempts counter, FIFO, and multiclock RTL elaboration, stores UHDM artifacts separately, and writes a JSON manifest with status, elapsed time, size, and SHA-256.

**Important:** UHDM byte hashes are diagnostic artifacts, not evidence of semantic equivalence; serialized data may vary with paths, ordering, or tool version. This stage does not execute RTL or traverse UHDM objects. Mock tests cover orchestration only.

Next engineering gate: use the UHDM C++ API to walk elaborated instances, nets, processes, and expressions; lower a counter into a typed VeloSim IR; execute it on the native scheduler. Verify real Surelog CLI arguments and output layout with a pinned tool version before declaring the frontend supported.

The previous slang frontend remains available for comparison.
