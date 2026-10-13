# VeloSim v0.16 — normalized UHDM lowering

This milestone adds a strict Python lowerer for a normalized UHDM-shaped process tree, a native C++ opcode with procedural SystemVerilog `if` semantics, and a checked-in 8-bit counter fixture.

**Crucial limitation:** The normalized fixture is hand-authored; it is **not** the output of Surelog. The existing UHDM C++ process reader remains uncompiled against the pinned UHDM library in this environment. The UHDM-to-normalized conversion and real source-driven test remain to be completed.

The `sv_if_reset_increment` opcode takes the else branch for unknown `rst_n` in a procedural `if (!rst_n)`. The legacy experimental `reset_increment` opcode is retained for compatibility with previous tests.

## Run

```sh
PYTHONPATH=src python3 -m velosim.uhdm_lower examples/counter.normalized.json -o build/counter.ir.json
c++ -std=c++20 -O2 -shared -fPIC -pthread native/ir/engine.cpp -o build/libvelosim_ir.so
PYTHONPATH=src python3 -m velosim.native_ir build/counter.ir.json --library build/libvelosim_ir.so --threads 8 --cycles 5 --release-reset
```

Local development package tests: 28/28 passed, including 8 new lowering tests. This does not constitute full SystemVerilog conformance. Next: compile UHDM C++ reader against the pinned dependency, map actual elaborated nodes into the normalized schema, and run differential tests against Icarus/Verilator.
