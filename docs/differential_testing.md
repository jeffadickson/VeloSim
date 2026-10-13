# Differential testing — counter vertical slice

Run:
```bash
PYTHONPATH=src python3 tools/differential.py --seed 42 --cycles 1000 \
  --engines velosim,icarus,verilator --threads 4 --require-all
```

Requirements: C++20 compiler, Python 3, and optionally `iverilog`/`vvp` and `verilator`. Verilator needs a working C++ toolchain.

The harness produces `report.json`, `stimulus.txt`, and when a mismatch occurs, `minimal_prefix.txt` containing the shortest prefix that reaches the first mismatch. This is **prefix reduction, not general delta debugging**. All executions are seeded and the report includes exact output values and failure classification.

- `pass`: at least two engines ran and agreed on every recorded cycle, with no missing requested engines.
- `fail`: a mismatch occurred, or `--require-all` detected unavailable engines.
- `incomplete`: no mismatch observed but engines were missing or only one ran.

**Major limitation:** The Icarus and Verilator adapters compile `counter_tb.sv`. The VeloSim adapter executes `examples/counter.ir.json`, with the strict SV opcode, because the Surelog-to-native RTL compiler is not yet operational. This tests behavioral equivalence of the restricted counter model, not HDL frontend correctness. Full HDL differential verification is a future acceptance gate.

The VeloSim model starts from a known zero state and each cycle applies reset before a rising edge. The external HDL fixture uses the same schedule. Four-state mismatches are reported separately, never silently coerced into binary values.

The test runner does not yet capture simulator versions or minimize arbitrary input traces. Both should be added before publication-grade benchmark results.
