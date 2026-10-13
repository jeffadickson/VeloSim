# Native multibit process kernel — stage 2

The tested C++ source is now committed at `native/ir/engine.cpp` with its Python/ctypes regression suite at `tests/test_ir_engine.py`.

The kernel supports 1–64-bit signals represented by value, X-mask and Z-mask, programmatically registered COPY/INC/RESET_INC edge-triggered processes, pre-commit snapshot evaluation, and worker counts 1/2/4/8/16.

The increment operator now preserves known bits when other input bits are X/Z. An exhaustive independent Python oracle enumerates all 256 four-state input combinations for a 4-bit increment, for each of 1, 4 and 16 workers (768 comparisons). The full 11-test native IR suite passed locally on Linux. This does **not** establish general SystemVerilog arithmetic conformance.

Run:
```sh
python3 -m unittest discover -s tests -p test_ir_engine.py -v
```

**Important limitations:** No Surelog/UHDM-to-IR lowering yet, no general expression interpreter, no full IEEE event-region scheduler, no persistent worker pool, no cocotb simulator adapter. The current UHDM inventory reader has not been compiled against real UHDM here.

Next: compile UHDM reader with pinned UHDM; lower counter signals and always_ff process into a versioned executable IR; compare source-driven execution with Icarus/Verilator.
