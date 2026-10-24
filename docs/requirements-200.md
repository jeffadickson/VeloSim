# Requirements and coverage: 200 qualification cases

Baseline `dcb1ede` was pushed to `origin/main` before this batch. The new work is on `requirements-coverage-200`; its final commit follows the deep review.

The 200 numbered cases and all twenty ten-case Release solution/native builds passed. Thirteen independent AI peer checkpoints, at 015 through 195, were accepted. All three final deep source and evidence reviews accepted the qualified results with the measurement limitations documented below. These are 200 implementation/qualification cases, rather than 200 new language features. No human expert review is claimed.

## What changed

Isolated blocking assignments now support bit/fixed/indexed selections, concatenations of distinct signal parents, and whole fixed-memory elements. Later statements observe the process's own blocking updates immediately. Concatenations sample one RHS and every destination index before publishing any local result. Other processes start from pre-commit state; nonblocking updates commit at the process-group boundary.

The validator rejects mixed blocking/nonblocking writes to a parent word or whole memory, and rejects blocking storage read by another process or continuous logic. Memory dependency tracking now includes storage identity as well as index expressions. Two-state signal storage converts X/Z to zero before later local reads.

The real frontend tests found that UHDM can expose an internal logic declaration directly as `vpiLogicVar` (3009) behind a selection. The exporter now recognizes that already-supported word reference. A minimized two-bit regression tests all sixteen four-state inputs.

Surelog and Icarus receive the same original HDL file. Surelog supplies parsing/elaboration, UHDM supplies typed design objects, and VeloSim supplies lowering, validation and execution. UHDM already understood the declaration in the failing example; our exporter needed the reference-shape fix. Reusing those libraries accelerates language understanding while execution support remains a separate, bounded contract. The [HTML introduction](introduction.html) explains that division of work.

## Stable requirements and case map

The [StrictDoc specification](../requirements/velosim.sdoc) is authoritative. IDs retain file-level implementation/test links and named verification methods; automatic C# method-level tracing is not claimed.

| Cases | Contract and evidence |
| --- | --- |
| 001–020 | VS-RTL-BLOCK-001: blocking bit writes and immediate reads, 1–128-bit words |
| 021–040 | VS-RTL-BLOCK-001: fixed parts and untouched-bit preservation |
| 041–060 | VS-RTL-BLOCK-001: ascending indexed writes, clipping and unknown bases |
| 061–080 | VS-RTL-BLOCK-001: descending writes through full width 128 |
| 081–100 | VS-RTL-BLOCK-002: concat swaps, nested 93-bit rotation and old destination-index sampling |
| 101–120 | VS-RTL-BLOCK-003: fixed four-state memory, immediate reads and persistence, depth 4 / lower bound 4 |
| 121–140 | VS-RTL-BLOCK-004: process/continuous-reader isolation, both process orders |
| 141–160 | VS-RTL-BLOCK-004: mixed-kind rejection by parent storage |
| 161–180 | Same-source Surelog/UHDM versus Icarus for selections, concat and memory; standalone and SDK |
| 181–200 | VS-RTL-BLOCK-005: two-state bit-selected blocking writes; 199/200 add disjoint processes |

VS-QUAL-001 traces the evidence pipeline. Baseline event, hierarchy, word, repeat and memory contracts remain separate from draft backlog requirements.

Each native execution case has twelve directed and thirty-two randomized input vectors, seed `20261024 + case ID`, and an independent C# MSB-first string-bit oracle. Executables run with worker arguments 1, 4 and 8. This qualifies result stability; it does not establish parallel RTL execution speed. Final regression requalifies earlier cases with all 44 current vectors.

The 20 frontend cases use 44 vectors each and compare standalone and SDK traces with Icarus. The minimized frontend regression adds sixteen vectors. Complete interfaces are specified independently before deriving the testbench.

## Verification status

| Gate | Status |
| --- | --- |
| Numbered qualification | 200/200 passed |
| Checkpoint builds | 20/20 passed, Ninja and managed/native compiler limits 4 |
| Peer checkpoints | 13/13 AI reviews accepted |
| Final deep review | Source and final evidence accepted by three AI peers |
| NativeAOT mutation helper | Passing four-test control; all four focused mutants caught semantically |
| Managed helper counterpart | Passing control and all four focused mutants caught under coverage |
| Generated-model ASan/UBSan | 076, 099, 120, 199 passed 44 vectors each |
| Native publication | Linux-x64 bundle and packaged smoke checks passed |
| StrictDoc | Export with source links passed |
| Full regression and main coverage | 620 regular tests passed; zero failures/skips; both gates passed. Four pre-existing explicit opt-in tests were excluded. |
| Exporter component coverage | 183 profiles merged; exact-binary JSON/LCOV/HTML reports generated. |

The native mutation report is `artifacts/requirements-200/mutations/20261024-204519-3560675/report.json`; the measured managed counterpart is `mutations/20261024-210238-0567835/report.json` under the same parent. They remove local word visibility, local memory visibility, memory dependencies, and the mixed-kind guard. Compile failures, timeouts, missing/zero-test results and unexpected exceptions are invalid probes, never semantic kills. Mutations affect copied projects; production sources are unchanged.

FsCheck uses 500 trials per guard property, replay seeds `(20261024,864013)` and `(20261025,864013)`. Malformed-IR fuzzing uses 500 structural mutations with seed 20261024 and retains concrete inputs on failure. The deliberately disabled mixed-kind guard yields a retained minimized counterexample `(0uy, 0uy, true)` after two shrinks. This is a four-target mutation exercise and structured-IR fuzzing, rather than a whole-program mutation score or general HDL grammar fuzzer.

## Coverage scopes

| Component | Lines | Branches | Evidence |
| --- | --- | --- | --- |
| Managed simulator/build helpers | 2871/3176 (90.40%) | 4280/5829 (73.43%) | Full-run combined report and gate |
| Native kernels/runtime/ABI | 528/533 (99.06%) | 1087/1242 (87.52%) | LLVM 18 runtime object set |
| UHDM RTL exporter | 185/199 (92.96%) | 629/890 (70.67%) | Separate exact-binary LLVM report |
| Mutation helper managed source | 72/72 (100%) | 39/58 (67.24%) | `mutation-helper-coverage/coverage-final.cobertura.xml` |

All paths in this section are local under `artifacts/requirements-200/`, except the main report under `artifacts/coverage/`. Helper source/native/managed/report hashes are in `mutation-helper-coverage/evidence.json`; its HTML report is in `mutation-helper-coverage/html/`.

The main gate requires managed line/branch coverage of 85%/70% and native line/branch coverage of 95%/85%. Exporter and helper measurements have separate denominators. Upstream Surelog/UHDM, NativeAOT machine code, workflow YAML and generated model C++ coverage are outside these measured scopes. Generated models have behavioral comparisons and sanitizer checks. Orchestration outside the managed collector also needs functional evidence. A line hit does not establish every branch or correct behavior; 19 of 58 helper branches remain uncovered despite its line result.

The coverage-built exporter accepts a dedicated nonempty `VELOSIM_UHDM_PROFILE_FILE`. The setup probe produced zero runtime-directory profiles and one exporter-directory profile. Unset/empty custom paths fall back to `LLVM_PROFILE_FILE`; two negative CLI usage probes deliberately direct that fallback to the exporter-only directory. The final suite uses the dedicated custom path, and exporter profiles are merged separately with the exact binary and matching LLVM 18.

## Evidence and reproduction

- `checkpoint-builds.json` lists all twenty successful build logs with SHA-256 hashes.
- `reviews.md` records checkpoint and deep-review outcomes.
- `native/<run>/NNN/` retains design JSON/C++, runtime header, reproducible stimuli, expected traces, executable, command logs, final manifest and status.
- Frontend evidence uses unique Linux-side `rtl-indexed-*` directories beneath `/home/jeffd/.local/share/VeloSim/requirements-200/frontend`; 135 complete reports and their referenced fixture files are hash-verified in artifacts/requirements-200/frontend-evidence/, with source-to-local mappings in index.json.
- `sanitizer-report-all.json` fingerprints the four generated-model sanitizer binaries and their original qualification manifests.
- `coverage-final.log` records the full regression and coverage gates.

Run `make build JOBS=4`, `make mutation-probe JOBS=4` and `make requirements-docs` inside WSL. The mutation target publishes/runs the C# file-based app as a Linux NativeAOT ELF; its ILC response records `--parallelism:4`. StrictDoc 0.30.4 requires its Python runtime and declarative `strictdoc_config.py` hook. Build and mutation automation are C#.

The helper source measurement uses its managed publication, Coverlet console 10.1.0 with `--include-test-assembly --include "[BlockingMutationProbe]*"`, and an actual successful helper run. Omitting inclusion of the supplied module produced an empty report, which was rejected. A separate `dotnet --version` probe established instrumentation readiness only.

The full test-suite deadline is 240 minutes; other build-tool child commands retain fifteen minutes. Mutation restore/build/run probes have twenty minutes and native fixture commands retain three minutes. CI allows 300 minutes, publishes before measurement, and runs the Linux suite once through coverage. macOS retains its normal test step. CI changes were source reviewed; no executed GitHub Actions result is claimed.

The bundle was published from the qualified working tree at base `dcb1ede` with dirty status. Its manifest and hashes identify those bytes; it is not represented as an archive built after the final commit.

## Retained failures and corrections

Initial evidence races were corrected by taking the exclusive compiler lock before writes. Mounted-filesystem timestamp reuse exposed stale model binaries; each case now uses a clean Ninja rebuild, and the retained executable is checked byte-for-byte against the executed binary. Cases 001–030 were requalified cleanly. Expanded slice widths through 128 prompted requalification of 021–060. Final manifests are written only after every comparison succeeds.

Frontend cases 161–165 initially failed on reference type 3009; the narrow exporter fix, rebuilt five cases and minimized regression all passed. Case 170 and the first 171 attempt hit CMake configuration timeouts. Linux-side evidence plus a Linux-only PATH produced clean retries; a separate configuration probe completed in 4.10 seconds. The interrupted unsanitized group also triggered an early build180; that build is retained separately and does not qualify the group. A fresh build180 followed all ten successful retries.

Mutation attempts retain a classifier false negative for FsCheck's exact Boolean assertion, compiler-lock contention from overlapping runs, an outer ten-minute timeout, and a concurrent-publication file-lock failure. The final native and measured managed runs completed with passing controls and all four semantic kills. Empty coverage and setup-only zero-hit reports were rejected. No failed attempt is counted as qualification.

## Remaining scope

General timed event queues/delays, always_comb, initialization, runtime-controlled loops, functions/tasks, structs/unions/enums, multidimensional/dynamic/associative arrays and queues remain open. Blocking storage remains isolated; concat parents must be distinct and total width at most 128. Selected memory-element writes, concat-memory targets and two-state blocking memories remain outside this contract. Known selected bases above INT32_MAX raise the existing qualified-range error. Passing this batch does not establish arbitrary SystemVerilog conformance.



The accepted main report is artifacts/coverage/20261024-211558-3593909/index.html, with gate.json, sources.json, doctor.json and evidence.json alongside it. LLVM reported 43 mismatched functions in the aggregate runtime report; the warning is retained as a measurement limitation despite passing configured gates. Exporter successful export logs contain no mismatch warning. Its separate evidence.json records the exact binary/source/report hashes and 183-profile manifest. A first exporter reporting command had an argument-interpolation error; its failed log is retained and the corrected command produced the accepted reports without rebuilding code.
