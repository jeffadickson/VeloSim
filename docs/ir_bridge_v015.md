# VeloSim v0.15 — native IR bridge

A tested development package implements a strict `velosim.ir.v1` validator and Python ctypes runner over `native/ir/engine.cpp`, plus a UHDM process-tree extractor prototype using `UHDM::Serializer::Restore()`.

**Local results:** 20/20 tests passed, including 300-cycle counter checks at 1, 2, 4, 8, and 16 worker counts; X/Z handling; invalid IR rejection; and prior native engine tests.

The source package is supplied in the development conversation as `velosim-v0.15-ir-bridge.zip`. This commit adds the checked-in example IR and milestone record; the full new Python runner and UHDM extractor have not yet been committed to GitHub.

**Current limitation:** The counter IR is hand-authored, not produced by Surelog. UHDM extractor code is written but has not been compiled or tested against the real pinned UHDM library. The native engine is a restricted process kernel, not a full IEEE 1800 simulator. The `reset_increment` opcode's unknown-reset X-merge differs from ordinary SystemVerilog procedural `if` semantics.

**Next acceptance gate:** Compile UHDM extractor against pinned Surelog/UHDM; inspect the elaborated counter; implement validated UHDM AST → IR lowering; execute that generated IR; compare against Icarus and Verilator.
