# VeloSim

Open-source experimental Verilog/SystemVerilog simulation project.

## Status

Early research prototype. Native C++ scheduler, Python reference testbench runner, LLVM expression compilation, and cocotb test fixtures are separate experimental components; full SystemVerilog support and a unified native simulator are not yet implemented.

## Project layout

- `src/velosim/` — Python compiler/reference tooling
- `native/` — C++ simulation and tracing experiments
- `tests/` — unit and regression tests
- `verification/cocotb/` — cocotb testbenches
- `examples/` — Verilog/SystemVerilog fixtures
- `docs/` — requirements and milestone notes
- `tools/` — local test and differential runners
- `.github/workflows/` — CI

This repository is being populated from the tested v0.10 development snapshot. Do not infer feature completeness from directory names.
