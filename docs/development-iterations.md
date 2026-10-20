# C# / native development cycle

Requested: 200 substantive engineering iterations; independent simulation and tooling AI peer reviews every 10; local commits every 25; full builds/tests every 50. Focused compilation/tests also run when needed to validate changes. A parameterized test row is not counted as an iteration. No push is authorized in this cycle.

The previous uncommitted Surelog integration fixes are preserved in the first local checkpoint. Historical Python stays as a compatibility reference; active helper commands are being ported to compiled C#.

## Implemented iterations

| Iteration | Concrete change |
|---|---|
|001|Pin .NET SDK and shared deterministic .NET10 project settings.|
|002|Add NUnit test project, Coverlet collector and pinned ReportGenerator manifest.|
|003|Create root CMake/Ninja build for native IR and scheduler shared libraries.|
|004|Define and expose the typed native C ABI header.|
|005|Add compiled CTest ABI guards, atomic-drive and parallel NBA regressions.|
|006|Join native worker threads safely during exception unwinding.|
|007|Reject multibit reset inputs for scalar-reset native operations.|
|008|Implement compiled C# build/test orchestration with bounded jobs and subprocess deadlines.|
|009|Implement separate LLVM native and Coverlet managed coverage, with HTML reports.|
|010|Replace Makefile Python entrypoints with compiled C# commands and add NativeAOT publishing.|
|011|Validate immutable four-state words including 64-bit masks.|
|012|Parse strict IR signals/schema and reject duplicate/unknown JSON fields.|
|013|Validate process references, supported opcodes and destination ownership.|
|014|Write deterministic IR without reflection for NativeAOT compatibility.|
|015|Lower normalized UHDM counter processes with initially unknown SV logic.|
|016|Own native engines through generated P/Invoke and SafeHandle disposal.|
|017|Validate atomic batched drives, width matching and worker bounds before native calls.|
|018|Implement compiled simulation CLI with trace and reset-release controls.|
|019|Run external tools with argument lists, concurrent output reads and deadlines.|
|020|Elaborate HDL through a validated Surelog adapter with artifact uniqueness checks.|
|021|Orchestrate real Surelog/UHDM normalization and strict lowering into persistent IR.|
|022|Port slang AST elaboration with structured diagnostics and temporary-file cleanup.|
|023|Expose HDL compilation through the compiled asynchronous CLI.|
|024|Parse monotonic four-state traces and compare values, widths and lengths.|
|025|Run native regression cases with fresh engine ownership for every worker configuration.|
|026|Add strict compiled CLI IR validation and normalized-UHDM lowering commands.|
|027|Load/write strict regression manifests with resolved source paths and trace constraints.|
|028|Compile manifest HDL and execute a validated native worker matrix.|
|029|Compile and execute independent Icarus reference traces with output/artifact checks.|
|030|Compare real Surelog-derived native execution against independent HDL simulation.|
|031|Generate deterministic seeded counter/reset stimuli and replay them natively.|
|032|Generate an independent testbench that instantiates the actual input RTL.|
|033|Compile/run Verilator references with explicit bounded build parallelism.|
|034|Write structured three-engine differential reports with incomplete/mismatch semantics.|
|035|Expose seeded, real-HDL differential execution through the compiled CLI.|
|036|Fingerprint source/include closures, native binaries, tools and command evidence.|
|037|Validate differential replay inputs against saved stimulus and artifact fingerprints.|
|038|Persist the prefix through first mismatch with explicit limits on reduction claims.|
|039|Add compiled report-inspection and reproducible replay commands.|
|040|Preserve timeout output/diagnostics and propagate cancellation through native cycles.|

|041|Build independent Verilator references with safely quoted CMake and parallel Ninja.|
|042|Expose compiled regression manifests through the CLI.|
|043|Validate bounded suite manifests and per-case resource budgets.|
|044|Schedule independent regression cases under bounded concurrency.|
|045|Write ordered suite results with typed failure categories.|
|046|Own native scalar schedulers and cache each native module identity.|
|047|Validate scheduler registration, atomic drives, ticks and disposal.|
|048|Cross-check native scalar operators and edges against independent four-state tables.|
|049|Execute strict JSON scheduler fixtures through compiled CLI commands.|
|050|Compare multithread scheduler execution with independent precommit oracle traces.|
|051|Preserve completed and canceled cases in partial suite reports.|
|052|Select suite cases through validated include/exclude patterns.|
|053|Assign stable SHA-based shards and retain selection metadata.|
|054|Admit cases by aggregate worker weight without exceeding job budget.|
|055|Return structured JSON CLI errors and cancellation exit130.|
|056|Evaluate word bitwise operations through independent scalar truth tables.|
|057|Evaluate increment/reset arithmetic with independent possible-carry sets.|
|058|Encode canonical binary four-state words with unsigned resize semantics.|
|059|Model IR edge activation and simultaneous commits independently in C#.|
|060|Compare native IR traces, activation counts and all signals against the oracle.|
|061|Load immutable strict IR word fixtures with validated binary drives.|
|062|Assert explicit checkpoints and report native/oracle mismatches.|
|063|Expose word conformance through CLI with exclusive report creation.|
|064|Generate reproducible binary/four-state IR stimulus from a fixed seed.|
|065|Bound conformance dimensions, memory/work estimates and CLI fixture size.|
|066|Resolve required build tools explicitly and probe versions with enforced deadlines.|
|067|Write a compiled build-doctor environment report without exporting environment secrets.|
|068|Read actual managed/native covered-line denominators separately and reject missing reports.|
|069|Apply validated optional managed/native line coverage thresholds.|
|070|Fingerprint measured coverage inputs and integrate gate evidence into Make/build workflows.|
|071|Validate immutable Surelog elaboration matrix plans and resource dimensions.|
|072|Elaborate each case/worker combination into fresh isolated artifacts.|
|073|Record source/tool/UHDM fingerprints, duration and process evidence.|
|074|Parse strict complete-grid elaboration reports with explicit scope and statuses.|
|075|Expose compiled matrix execution and inspection with sample configuration.|
## Peer reviews and validation

- 010: independent simulation and tooling reviewers inspected the foundation. Fixed scheduler allocation exception ownership, added genuinely parallel native tests, fixed standalone native coverage builds, propagated instrumentation to UHDM tools, included JSON coverage and moved initialization under error handling. Native CTest 2/2 passes; C# build helper compiles without warnings. Core 011–015 focused NUnit 5/5 passes.
- 020: two reviewers identified reset assertion/schema inconsistencies and subprocess cancellation/drain deadlines. See `reviews/checkpoint020.md`; fixes precede local checkpoint025. Focused NUnit10/10 passed before the added regressions.
- 025: all020 findings fixed; NUnit16/16 and native CTest2/2 pass. Real C# compile→simulate runs301 cycles with reset then300 increments, final8-bit count44. Upgraded Coverlet to10.1.0 and Test SDK18.5.1 for .NET10 after finding the older collector emitted empty managed reports. Build helper rejects empty managed coverage. Actual combined report contains both VeloSim managed code and native sources; combined line96.5%, branch69.6% (not a managed-only percentage). LLVM native line93.2%.
- 030: both reviewers required exact-width reference traces to avoid corrupting X/Z values. Other reviewed scheduling/frontend contracts accepted. NUnit21/21 and an explicit real same-RTL differential test pass. CLI NativeAOT publish/simulate smoke passed. Coverage config now includes compiler-generated async/lambda state machines; future reports include this additional code rather than excluding it.
- 035: exact-width trace regression fixed. NUnit26/26 passes; real100-cycle Surelog-derived VeloSim/Icarus/Verilator comparison passes with4 simulation workers and4 build jobs. Reports distinguish Verilator's binary scope from four-state conformance and never pass missing requested engines.
- 040: NUnit31/31, three-engine differential/replay passed; independent reviewers found tool binding, contradictory report fields and timeout-metadata issues. See review040; fixes precede045. Root validated a Ninja-based Verilator build independently.

## Local commit checkpoints

025 `6a97ab2`: compiled helper foundation and frontend/native C# port, including prior local Surelog fixes. Pending050,075,100,125,150,175,200.

## Full build checkpoints

Pending 050, 100, 150, 200. Full validation includes managed tests, compiled native tests, coverage and NativeAOT publish/smoke where supported. Coverage percentages will be recorded from actual reports, separately for managed and native code.

050: two independent AI peer reviews completed and findings fixed/confirmed. Full coverage build: NUnit41/41, CTest2/2, managed line90%, native96.3%, combined91%, branch67.5%; report artifacts/coverage/20261020-191133-7245342. NativeAOT1000-cycle actual-RTL three-engine comparison passed. Native publish dispatch uses managed bootstrap to prevent self-overwrite.

050 follow-up evidence: republished NativeAOT CLI replayed the1000-cycle report, inspected fingerprints, and passed scheduler worker matrix1/2/4/8. Both published helpers verified ELF native executables. Optional installed-UHDM tools build through root Ninja; ASan/UBSan native CTest2/2 passes. Commit2072450 is the050 local checkpoint.

060: two independent reviews completed; fixes propagate native loop cancellation, classify malformed reference traces, update weighted-budget regression and CI paths. NUnit53/53 passes. Root066–070 focused diagnostics and coverage gate tests add5;58/58 passes before061–065 lands. Doctor and coverage-check ran successfully against actual050report.

070: both independent reviews completed; required native-file/main-assembly coverage checks fixed and confirmed. Final NUnit63/63 passes. Doctor actualtoolreport and actual050coverage-check pass; malformed XML exits1 with controlled diagnostic. Coverage thresholds are opt-in, with nonempty expected modules always required.

070 CLI smoke: generated1000 seeded four-state drives for real Surelog-derived counter IR; native/oracle/checkpoints pass across1/4/8 workers. The first CLI smoke found an old copied Core DLL; rebuilding the CLI resolved the stale artifact before validation.

075: NUnit68/68 passes; actual Surelog matrix counter/FIFO/multiclock ×workers1/4 all6pass, artifacts/matrix075/matrix.json. Matrix results claim elaboration only. Coverage selection now includes the compiled build-helper assembly as well as core/CLI, so next100report includes this additional denominator.
