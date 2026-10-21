# Batch 2: fifty RTL implementation and validation iterations

Baseline `295ad4a`. Inventory: [fifty missing capabilities](verilog-missing-50-batch2.md). Previous batch documents are preserved. Each row is a bounded implementation or validation task; no row is complete merely because code was written. Status records actual evidence; source integration and final gates remain pending until run.

Builds and independent simulation/tooling expert reviews are required at checkpoints 10, 20, 30, 40 and 50. Local commits are planned at 25 and 50. Native builds use CMake/Ninja with explicit bounded parallelism; compiled C# helpers and available current NativeAOT executables remain the active tooling. No remote push is included.

| Iteration | Planned task | Evidence/status |
|---|---|---|
| 01 | Audit baseline support and record exactly fifty distinct missing capabilities. | Complete: audited baseline inventory contains exactly fifty gaps. |
| 02 | Specify packed declaration range, index direction, unsigned result width and supported-source contract. | Complete: bounded contract documented in README and reviewed. |
| 03 | Implement bounded native packed bit-select reads preserving X/Z payload bits. | Native string-oracle tests pass at widths1..64; five CTests pass in Release and address/undefined sanitizers. Logs runtime10 and sanitizers. |
| 04 | Add strict packed bit-select IR shape, reference traversal and width validation. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 05 | Export packed bit-select reads from actual UHDM node relationships. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 06 | Qualify known, unknown and out-of-range bit indices with independent expected values. | Native string-oracle tests pass at widths1..64; five CTests pass in Release and address/undefined sanitizers. Logs runtime10 and sanitizers. |
| 07 | Implement constant packed slice runtime extraction and partial out-of-range behavior. | Native string-oracle tests pass at widths1..64; five CTests pass in Release and address/undefined sanitizers. Logs runtime10 and sanitizers. |
| 08 | Validate constant slice IR bounds, widths and depth/node budgets. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 09 | Lower constant part-select source constructs through UHDM with explicit range checks. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 10 | Build checkpoint 10; obtain simulation and tooling reviews and resolve findings. | Build10 passed with zero warnings/errors; five CTests pass. Both reviews found concat depth/accounting and shared-interface test gaps; repairs tracked at20. |
| 11 | Qualify bit-select self-determined width and unsigned result semantics. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 12 | Check bit indices at zero, highest valid bit and first out-of-range position. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 13 | Qualify X/Z dynamic bit indices and preserved X/Z selected payload bits. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 14 | Check fixed slices at width 1/64, full-word and partial out-of-range boundaries. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 15 | Reject ascending/nonzero packed declaration ranges rather than misinterpreting indices. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 16 | Qualify fixed-slice four-state ordering and untouched self-determined operand widths. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 17 | Qualify select boundaries inside arithmetic, comparison and shift expression contexts. | Pending. |
| 18 | Verify selected values and dynamic index references participate in continuous DAG checks. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 19 | Qualify wide unsigned index values and reject unsupported signed index expressions. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 20 | Build checkpoint 20; obtain both expert reviews and resolve findings. | Build20 passed zero warnings/errors; both independent reviewers accepted repairs; focused4/4 pass. |
| 21 | Prove packed bit/slice/indexed assignment targets remain explicitly rejected. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 22 | Prove ascending/descending indexed part-select reads remain explicitly rejected. | Pending. |
| 23 | Disambiguate packed bit-select reads from existing unpacked-memory element reads. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 24 | Qualify malformed select IR, field/arity checks and native read-helper width-64 boundaries. | Passed: checkpoint20 selection4/4, actual172cycles at1/4/8 and six rejection variants; native string-oracle widths1..64. |
| 25 | Run focused managed/native regressions and preserve the first local checkpoint commit. | Focused4/4 and Release/sanitizer5/5 pass; first local checkpoint contains current qualified feature implementation. |
| 26 | Implement bounded n-ary concatenation using explicit self-determined operand widths. | Pending. |
| 27 | Export actual UHDM concatenations with three or more operands. | Pending. |
| 28 | Qualify nested concatenation with arithmetic/shifts/selects without outer-context leakage. | Pending. |
| 29 | Define and validate constant replication count, payload width and total-width limits. | Pending. |
| 30 | Build checkpoint 30; obtain both expert reviews and resolve findings. | Pending build logs and both review outcomes. |
| 31 | Export actual UHDM replication constructs with known constant repeat counts. | Pending. |
| 32 | Qualify nested replication and four-state payload ordering against independent expected values. | Pending. |
| 33 | Reject unsupported counts, overwide concatenations, malformed nodes and excessive expansion. | Pending. |
| 34 | Verify expression reference traversal and combinational DAG/writer checks for new forms. | Pending. |
| 35 | Build a real-source select/concatenation differential fixture and common stimulus harness. | Pending. |
| 36 | Assert exact source interface and requested output writers before constructing reference wiring. | Pending. |
| 37 | Add directed boundary vectors for low/high indices, width-one and full-word windows. | Pending. |
| 38 | Add X/Z payload and index vectors, plus stable known/four-state seeded phases. | Pending. |
| 39 | Run an explicit mixed-width matrix through Surelog, native C++ and Icarus. | Pending actual execution and retained traces. |
| 40 | Build checkpoint 40; obtain both expert reviews and resolve findings. | Pending build logs and both review outcomes. |
| 41 | Record unchanged same-source paths/hashes, UHDM, IR, copied runtime and generated build inputs. | Pending artifact evidence. |
| 42 | Record effective compiler/Ninja/tool identities and enforce trace/report resource limits. | Pending artifact and rejection evidence. |
| 43 | Qualify malformed/missing outputs, wrong widths and trace-order rejection to prevent false passes. | Pending negative test evidence. |
| 44 | Qualify unsupported declaration directions/ranges and unsupported lvalues with explicit rejections. | Pending negative source evidence. |
| 45 | Run existing operator/FIFO/counter regressions to detect changes outside the selected forms. | Pending regression evidence. |
| 46 | Update supported/backlog disposition using only implemented and qualified forms. | Pending review of final documentation. |
| 47 | Run the current compiled/native CLI on a real source example exercising the new forms. | Pending command, source and output evidence. |
| 48 | Run full managed/native tests and configured line/branch coverage gates against frozen sources. | Pending coverage and provenance validation. |
| 49 | Complete final independent simulation/tooling reviews and repair any outstanding findings. | Pending both final outcomes. |
| 50 | Build checkpoint 50, create the final local commit, then freshly qualify committed-source coverage and NativeAOT bundle. | Pending build, reviews, commit identity, coverage-current and bundle evidence. |

## Evidence rules

Record command outcomes, artifact locations and commit identities as they become available. Preserve failed attempts with their repairs; do not count them as passes. Source edits after a build invalidate that build's source-current claim. A final commit can alter managed assembly version metadata, so final binary/coverage evidence must come from fresh committed-source measurement.

Same-source means the same design `.sv` file path and unchanged contents reach Surelog and Icarus; Icarus additionally receives a generated stimulus testbench. Complete output traces must agree, and their interface must be independently specified rather than inferred solely from exported IR. A missing output or incorrect exported width must fail qualification. Requests for worker settings 1/4/8 retain the existing serial single-process execution scope.

The agreed language scope is inventory items 1, 2, 9 and 10: packed bit-select reads, constant packed part-select reads, n-ary concatenation and constant replication. Indexed reads and every selected assignment target remain rejected. Fifty rows distribute implementation, qualification and release work across those four capability groups; they are not fifty new language features. Final disposition must name what actually passed, what was conservatively rejected and what remains missing. All build/review/commit/publication gates above remain pending until their evidence is recorded.

## Checkpoint 10 review finding

The tooling reviewer identified that the exporter's permitted 64-operand concatenation or 64-count replication was folded into a deep left-associated binary JSON tree. That representation can exceed the JSON reader's depth limit despite a valid 64-bit result. The requested repair is balanced binary construction preserving operand order, plus expanded-output accounting for repeated subtrees. Repair and qualification are pending checkpoint 20; this review note does not mark any build or test as passed.
