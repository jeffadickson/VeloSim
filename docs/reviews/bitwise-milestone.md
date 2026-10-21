# Structural bitwise RTL milestone

Adds unsigned four-state AND, OR, XOR and NOT to the structural UHDM exporter, IR validation, C++ emitter and native runtime. Existing single-clock/single-process limits remain.

The independent semantic AI peer review found no blocker: known-zero/known-one dominance, X/Z propagation, zero extension before bitwise evaluation, destination-width propagation into nested arithmetic, and UHDM opcode mappings were checked. The reviewer made no edits.

Verification includes exhaustive one-bit four-state truth tables in the native runtime test, unequal-width and 64-bit boundaries, malformed IR operand counts, and actual Surelog/UHDM elaboration of `examples/surelog_bitwise.sv`. The compiled native trace matches Icarus for 144 vectors and worker arguments 1/4/8. These worker arguments still execute a single process serially. Integration evidence is retained under `artifacts/rtl-bitwise-*`; CI includes the new integration tests and uploads their artifacts.

The focused managed tests and all five native CTests passed before the checkpoint. Full regression and measured coverage results are delivered separately under the output directory; the default LLVM coverage denominator excludes the UHDM exporter.
