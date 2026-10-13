# Real Surelog/UHDM execution gate

The experimental `native/uhdm/normalize.cpp` traverses a **single** elaborated top module using the UHDM VPI interface, matches one `always_ff @(posedge clk)` counter process, and rejects unsupported constructs rather than guessing their behavior.

It is intentionally a **strict prototype**. It does not support general expressions, multiple modules, timed testbenches, or broad SystemVerilog.

## Build and test

1. Check out pinned Surelog and UHDM recursively.
2. Build Surelog and its matching UHDM using their upstream documented CMake flows.
3. Build `velosim-uhdm-normalize` with `cmake -S native/uhdm -B build/uhdm -DUHDM_INCLUDE_DIR=... -DUHDM_LIBRARY=...`.
4. Run `PYTHONPATH=src python3 tests/real_surelog_smoke.py --surelog <surelog-path> --normalizer <normalizer-path>`.
5. Compare output against Icarus/Verilator using independent testbenches.

## Current verification

- The existing mocked frontend-to-native pipeline tests pass locally.
- The real UHDM C++ normalizer is **written and committed**, but has **not been compiled or run against UHDM** in this environment.
- The actual Surelog-to-UHDM node layout and compatibility of this exact lowering pattern remain **unverified**.
- A failing real smoke test is expected to reveal the first incompatible node shape; fix by inspecting the UHDM tree, never by silently substituting hand-authored IR.

Do not claim a real SystemVerilog counter has run through Surelog until the opt-in smoke test passes with actual Surelog/UHDM.
