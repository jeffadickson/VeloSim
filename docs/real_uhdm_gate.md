# Real Surelog/UHDM execution gate

The experimental `native/uhdm/normalize.cpp` traverses a **single** elaborated top module using the UHDM VPI interface, matches one `always_ff @(posedge clk)` counter process, and rejects unsupported constructs rather than guessing their behavior.

It is intentionally a **strict prototype**. It does not support general expressions, multiple modules, timed testbenches, or broad SystemVerilog.

## Build and test

1. Check out pinned Surelog and UHDM recursively.
2. Build Surelog and its matching UHDM using their upstream documented CMake flows.
3. Install Surelog/UHDM, then build the native tools with `cmake -S native/uhdm -B build/uhdm -DCMAKE_PREFIX_PATH=<install-prefix>`. The exported UHDM CMake target supplies its required headers, compile options, and static-library dependencies. Run `cmake --build build/uhdm`.
4. Run `PYTHONPATH=src python3 tests/real_surelog_smoke.py --surelog <surelog-path> --normalizer <normalizer-path>`.
5. Compare output against Icarus/Verilator using independent testbenches.

## Current verification

- Verified on Ubuntu 24.04 under WSL2 on 2026-10-20 using GCC 13.3, Surelog `6db44ea2048c8ab9c702e389c2897f7e2318ccc5`, and its matching UHDM `7c42de4057d0a7e2ef14048b8ee570c7ac3db94b`.
- Both native UHDM tools compile and link against the installed UHDM CMake package.
- The real smoke test passes: `examples/slang_counter.sv` is elaborated by Surelog, normalized from its binary UHDM database, lowered, and executed for reset plus 300 increments, including 8-bit rollover, with four native workers.
- The normalizer resolves the default `work@` library-qualified top name and uses UHDM's type evaluator for parameter-dependent signal widths. Port widths come from their resolved low connections.
- Opt-in real-UHDM regressions cover a 16-bit parameterized counter and reject 128-bit signals, blocking assignments, and a missing top. Run them with `VELOSIM_SURELOG=<surelog> VELOSIM_UHDM_NORMALIZER=<normalizer> python3 -m unittest discover -s tests -p test_real_uhdm.py -v`.

This verifies the restricted counter path, not arbitrary SystemVerilog. The differential harness still uses hand-authored IR for its VeloSim adapter; it is a separate check from this source-driven smoke test.
