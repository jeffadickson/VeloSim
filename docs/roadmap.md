# VeloSim roadmap

The current roadmap is the [2026-10-26 Verilator gap analysis](verilator-gap-analysis.md), [work-package plan](verilator-work-packages.md) and [GitHub tracking epic](https://github.com/jeffadickson/VeloSim/issues/1). The text below is an early historical checkpoint and does not describe the current checkout.

The GitHub repository currently contains the initial native scheduler and its tests. The complete local v0.10 development snapshot also contains Python compiler/reference modules, LLVM experiments, native tracing, cocotb fixtures, and additional tests that still need importing.

Next: import full snapshot, integrate HDL elaboration with native processes, multibit four-state values, full event regions, persistent workers, cocotb GPI/VPI, and cross-simulator regressions.

Current scheduler is restricted; it does not execute arbitrary SystemVerilog.
