# Open items: 100 implementation and qualification iterations

Baseline: `91ae2ef`. Iterations are concrete implementation or qualification cases, not a claim of 100 new language features. Each ten-case checkpoint receives a Release solution/native build using Ninja with four jobs. C# owns test and build tooling; no Python helper is introduced.

The existing baseline already supports shared-clock processes, bounded hierarchy and signed 128-bit words. This batch expands those contracts incrementally. General timed simulation, arbitrary composite types and dynamic containers require further work; only implemented and tested behavior is reported as supported.

| Iterations | Work | Result |
|---|---|---|
| 001–010 | Explicit per-process input-edge IR, shape and ownership constraints | Build passed; 10/10 contract cases passed |
| 011–020 | Four-state edge transitions and generated execution | Build passed; native 6/6 and managed 10/10 passed |
| 021–030 | Actual frontend multiple clocks and falling edges | Build passed; 10/10 same-source standalone + SDK comparisons passed |
| 031–040 | Asynchronous reset event lists and interacting process qualification | Build passed; 10/10 same-source comparisons passed |
| 041–050 | Constant-count repeat statements (0..256), expanded execution budget | Build passed; 10/10 same-source comparisons passed |
| 051–060 | Isolated whole-word blocking assignments, conditionals and repeat ordering | Build passed; six actual/four rejection cases passed after refreshing exporter |
| 061–070 | Packed two-state bit words through 128 bits | Build passed; 10/10 same-source standalone + SDK comparisons passed |
| 071–080 | Unequal-width input/output ports in ordinary/generated instances | Build passed; five ordinary and five corrected generated fixtures passed; primary-negedge startup regression passed |
| 081–090 | One-word, nonzero and descending fixed-memory bounds | Build passed; native 6/6 and 10/10 same-source comparisons passed; strengthened upper-endpoint stimuli repeat at checkpoint 100 |
| 091–100 | Unsupported initialization, delay, combinational, subroutine, composite and container boundaries | Build passed; 28/28 checkpoint cases passed, including final array endpoints, integration and resource guards (container case has three variants) |

Build/test logs are retained under `artifacts/open-items-100/`. Later checkpoints may adapt to findings; the final record lists what actually ran.

## Implemented execution contract

The explicit event model accepts up to 256 independently triggered processes, each with 1–16 distinct scalar top-input rising/falling edge controls. Direct instance input-port aliases resolve to those inputs. It recognizes the scalar 0/1/X/Z edge table. One host `eval()` samples a batch of input changes; triggered processes read one old-state snapshot, stage nonblocking updates, commit together and settle continuous logic. Unchanged-input evaluations do not rerun an edge process. Generated clocks, arbitrary event expressions, delays, delta-cycle scheduling and a timed event queue remain unsupported. The existing implicit shared-posedge model retains its known-clock contract.

Whole-word blocking assignments use local process storage so later expressions in that process see them immediately. Destinations must be isolated from readers in other processes and from continuous logic, and cannot also receive nonblocking assignments. Selected-word or memory blocking assignments remain unsupported. Constant `repeat` counts are 0–256; an expanded statement budget also bounds nested loops. Runtime-controlled loops, `for`/`while`, functions and tasks remain open.

Packed two-state `bit` signals from 1–128 bits start at zero. X/Z converts to zero at input deposits and signal assignment boundaries, including selected writes. Signedness is independent of the two-state flag. Unpacked two-state memories, structs, unions, enums and additional integer atom types remain unsupported. Four-state logic retains its previous X/Z behavior.

Instance input/output words may differ in width. Bridges size to the destination using source expression signedness, preserving sign extension or truncating high bits. Output connections still require whole declared words. Event aliases remain scalar and require matching signed/two-state metadata.

Fixed one-dimensional four-state memories accept 1–256 words and nonnegative bounds through INT32_MAX, in either declaration direction. Storage indexes are normalized by the smaller bound using checked unsigned arithmetic; below-bound, above-bound and X/Z accesses retain the unknown-read/ignored-write policy. Multidimensional, selected-memory writes, aggregate assignment, queues and dynamic/associative arrays remain open. Explicit initialization and general `always_comb` also remain open.

Surelog parses and elaborates the same source used by the Icarus comparisons. No feature uses HDL source-text pattern matching. The normalized IR and generated C++ execute the bounded contract above; passing these scenarios is not full SystemVerilog conformance.

## Review and verification

An independent AI peer reviewed event-table detection, process snapshots/commit, blocking isolation, repeat budgets, two-state metadata/conversion, instance sizing and memory bounds. This was an AI source review, not a human expert review; the reviewer made no edits or builds. Source acceptance was conditional on actual conformance and final gates. A proposed dynamic-case-label reader issue was withdrawn because labels already require constants.

Implementation inspection found that the primary clock's previous-event value must start at zero, matching host storage, while other logic event inputs start X. This prevents an invented startup falling edge. The added same-source startup regression passed. Additional integration tests combine hierarchical falling clocks, signed two-state blocking temporaries and selected nonblocking writes.

Failed attempts remain in the logs: checkpoint 60 initially used a stale exporter; two test-source templates lacked whitespace; checkpoint 80 exposed a native test variable-name collision. The affected code or fixtures were corrected and rerun. No failing scenario is waived. The native release and AddressSanitizer/UndefinedBehaviorSanitizer suites passed all six tests after the changes.

All 100 numbered cases have passing checkpoint evidence. The checkpoint 100 build and 28 selected tests passed, including the strengthened array endpoints and integration/resource regressions. The existing fast regression passed 285 tests with the native-library directory configured; an initial direct invocation omitted that configuration and its failed log is retained. Full regression, NativeAOT publication and coverage results will be appended after their final gates complete. NativeAOT publication precedes coverage capture so the measured assembly files remain intact afterward.
