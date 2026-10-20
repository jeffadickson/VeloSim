# C# / native development cycle

Requested: 200 substantive engineering iterations; independent simulation and tooling AI peer reviews every 10; local commits every 25; full builds/tests every 50. Focused compilation/tests also run when needed to validate changes. A parameterized test row is not counted as an iteration. No push is authorized in this cycle.

The previous uncommitted Surelog integration fixes are preserved in the first local checkpoint. Historical Python sources are archived under historical/python; active helper commands use compiled C#.

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
|076|Archive historical Python sources outside active C# helper/test folders.|
|077|Version native ABIs and reject missing/incompatible modules before engine allocation.|
|078|Expose native IR ticks and compare oracle/native transition counts.|
|079|Hide implementation symbols while exporting the declared C ABI.|
|080|Compile and run a C11 consumer against both native libraries.|
|081|Infer and validate supported counter widths1..64 from lowered IR.|
|082|Compile RTL first and gate reference execution on a valid counter contract.|
|083|Generate width-aware testbenches with runtime DUT port-width assertions.|
|084|Negotiate reference trace widths explicitly and reject inconsistent output.|
|085|Compare every engine against an independent counter/reset/rollover specification.|
|086|Version width-aware differential reports with shape and specification validation.|
|087|Bind replay width and specification to the fingerprinted compiled IR.|
|088|Record frontend/contract/native/reference/dependency failure stages.|
|089|Verify expected HDL semantic rejections without masking setup/tool failures.|
|090|Run declared-width counter matrices with per-case evidence and ordered index.|
|091|Build regular native kernels in Release while retaining Debug coverage instrumentation.|
|092|Stage native libraries and C headers through CMake installation.|
|093|Assemble a Linux-x64 native helper/library/example bundle.|
|094|Record exact file sizes, hashes and executable modes in versioned bundle manifests.|
|095|Validate bundle completeness, integrity, paths, links and resource limits.|
|096|Expose standalone compiled bundle integrity checks.|
|097|Make build-helper help usable outside a source checkout.|
|098|Create validated compressed tar archives preserving native executable permissions.|
|099|Include source revision/dirty state, compiler/generator settings and tool diagnostics.|
|100|Integrate Make bundle orchestration and isolated native execution smoke checks.|
|101|Emit supported scalar scheduler operators as independent SystemVerilog expressions.|
|102|Generate atomic-drive/NBA testbenches with startup guarding and feedback rejection.|
|103|Parse exact scheduler dimensions, step/tick/activation and four-state reference traces.|
|104|Compile/run fresh Icarus scheduler references with bounded subprocesses.|
|105|Compare Icarus, native worker matrices and the managed scheduler oracle.|
|106|Record scheduler fixture/source/native/tool/version/command provenance.|
|107|Write typed scheduler pass/fail/incomplete reports with mismatch precedence.|
|108|Expose compiled scheduler differential execution through CLI.|
|109|Generate reproducible seeded multiclock four-state scheduler fixtures.|
|110|Run built-in independent scheduler conformance batches with evidence indexes.|
|111|Validate bounded multiactive-process benchmark plans before native allocation.|
|112|Build deterministic native counter workloads and verify activation/tick/result checksums.|
|113|Measure fresh-engine warmups and repeated worker configurations.|
|114|Record explicit managed-drive/native timing boundaries with monotonic clocks.|
|115|Calculate finite min/median/nearest-rank-P95/max statistics and baseline ratios.|
|116|Preserve partial benchmark outcomes during cancellation and bound total work.|
|117|Record native/host fingerprints, CPU count and runtime capabilities.|
|118|Expose the compiled native optimization profile through the C ABI.|
|119|Add a standalone compiled/native-publishable benchmark CLI.|
|120|Integrate benchmark builds, NativeAOT packaging and bundle smoke into Make workflows.|
|121|Structurally inspect scheduler reports independently of current artifact availability.|
|122|Validate scheduler replay hashes, emitted source/fixture binding and exact tool roles.|
|123|Expose scheduler inspection and replay eligibility summaries through CLI.|
|124|Replay scheduler cases with pinned tools, fresh artifacts and saved worker matrices.|
|125|Persist first-mismatch prefix fixtures and explicit reproduction commands.|
## Peer reviews and validation

- 010: independent simulation and tooling reviewers inspected the foundation. Fixed scheduler allocation exception ownership, added genuinely parallel native tests, fixed standalone native coverage builds, propagated instrumentation to UHDM tools, included JSON coverage and moved initialization under error handling. Native CTest 2/2 passes; C# build helper compiles without warnings. Core 011–015 focused NUnit 5/5 passes.
- 020: two reviewers identified reset assertion/schema inconsistencies and subprocess cancellation/drain deadlines. See `reviews/checkpoint020.md`; fixes precede local checkpoint025. Focused NUnit10/10 passed before the added regressions.
- 025: all020 findings fixed; NUnit16/16 and native CTest2/2 pass. Real C# compile→simulate runs301 cycles with reset then300 increments, final8-bit count44. Upgraded Coverlet to10.1.0 and Test SDK18.5.1 for .NET10 after finding the older collector emitted empty managed reports. Build helper rejects empty managed coverage. Actual combined report contains both VeloSim managed code and native sources; combined line96.5%, branch69.6% (not a managed-only percentage). LLVM native line93.2%.
- 030: both reviewers required exact-width reference traces to avoid corrupting X/Z values. Other reviewed scheduling/frontend contracts accepted. NUnit21/21 and an explicit real same-RTL differential test pass. CLI NativeAOT publish/simulate smoke passed. Coverage config now includes compiler-generated async/lambda state machines; future reports include this additional code rather than excluding it.
- 035: exact-width trace regression fixed. NUnit26/26 passes; real100-cycle Surelog-derived VeloSim/Icarus/Verilator comparison passes with4 simulation workers and4 build jobs. Reports distinguish Verilator's binary scope from four-state conformance and never pass missing requested engines.
- 040: NUnit31/31, three-engine differential/replay passed; independent reviewers found tool binding, contradictory report fields and timeout-metadata issues. See review040; fixes precede045. Root validated a Ninja-based Verilator build independently.

## Local commit checkpoints

025 `6a97ab2`: compiled helper foundation and frontend/native C# port, including prior local Surelog fixes. 050 `2072450`;075 `3b31a1d`. 100 `55e9cd6`. Pending125,150,175,200.

## Full build checkpoints

050 and100 complete; remaining150,200. Full validation includes managed tests, compiled native tests, coverage and NativeAOT publish/smoke where supported. Coverage percentages will be recorded from actual reports, separately for managed and native code.

050: two independent AI peer reviews completed and findings fixed/confirmed. Full coverage build: NUnit41/41, CTest2/2, managed line90%, native96.3%, combined91%, branch67.5%; report artifacts/coverage/20261020-191133-7245342. NativeAOT1000-cycle actual-RTL three-engine comparison passed. Native publish dispatch uses managed bootstrap to prevent self-overwrite.

050 follow-up evidence: republished NativeAOT CLI replayed the1000-cycle report, inspected fingerprints, and passed scheduler worker matrix1/2/4/8. Both published helpers verified ELF native executables. Optional installed-UHDM tools build through root Ninja; ASan/UBSan native CTest2/2 passes. Commit2072450 is the050 local checkpoint.

060: two independent reviews completed; fixes propagate native loop cancellation, classify malformed reference traces, update weighted-budget regression and CI paths. NUnit53/53 passes. Root066–070 focused diagnostics and coverage gate tests add5;58/58 passes before061–065 lands. Doctor and coverage-check ran successfully against actual050report.

070: both independent reviews completed; required native-file/main-assembly coverage checks fixed and confirmed. Final NUnit63/63 passes. Doctor actualtoolreport and actual050coverage-check pass; malformed XML exits1 with controlled diagnostic. Coverage thresholds are opt-in, with nonempty expected modules always required.

070 CLI smoke: generated1000 seeded four-state drives for real Surelog-derived counter IR; native/oracle/checkpoints pass across1/4/8 workers. The first CLI smoke found an old copied Core DLL; rebuilding the CLI resolved the stale artifact before validation.

075: NUnit68/68 passes; actual Surelog matrix counter/FIFO/multiclock ×workers1/4 all6pass, artifacts/matrix075/matrix.json. Matrix results claim elaboration only. Coverage selection now includes the compiled build-helper assembly as well as core/CLI, so next100report includes this additional denominator.

075 localcommit3b31a1d. 080 focusedbuild:69NUnit/3CTestpass. Nativecoverage compiler selection now explicitlymatches Clang forC andC++ after introducing the Cconsumer. Both080peerreviews completed; matrixexecutablebinding/version-probecancellation findings beingfixed before090.

080 evidence: LLVM native coverage completes with matching Clang18 C/C++ (69 NUnit/3CTest). ABI999 negative CLI returns controlled BackendUnavailable/exit1. Export inspection confirms only declared C ABI functions on Linux.

090 implementation: combined85NUnitpasses, real100cycle16/64-bit three-engine runs pass with4workers/4Ninjajobs; three expected realHDL rejection cases pass. Rootbundle5focusedtests pass; final100validationpendingreviews.

100 fullcoverage:86regularNUnit/3nativeCTest pass. Report artifacts/coverage/20261020-193947-4151949: combined87.7% lines/69.1%branches; managed(Core+Build)86.8%, core96.7%, native98.2%. Including build-helper code adds denominator; CLI bootstrap is not directly measured. LLVM missing C-test binary triggered external debug lookup; fixed by including every instrumented object, disabling debuginfod and requiring all profile binary IDs to resolve locally. New fullcoverage passes without lookup.

100 NativeAOTbundle success: artifacts/bundles/20261020-194012-4322870/velosim-linux-x64.tar.gz. Packaged CLI exactreset/increment trace and standaloneBuildhelp/bundle-check pass withbundlelibraries explicitlyselected. ASan/UBSan nativeCTest3/3 passes. Optionalactual16-bit1000cycleAOTcomparison/replay running;86NUnit/3CTestfullbuild,coverageandnativepublicationcomplete.

100 optionalNativeAOTreal16bit1000-cyclethree-enginecomparison/replay/inspectionallpass. 110 bothpeerreviewsfoundnoblockingissues;96NUnitpass, actualIcarusfourcasebatch×workers1/2/4/8allpass. 120 root5benchmarktests pass; actualbenchmark/alltestsunderway.

120 bothreviewsacceptedbenchmarkcorrectness; fixedJITimplementationidentity withCore/entryassemblyhashes andsource-content-stamped AOTpreference. Nativewarmup/repetition/cancellation tests5/5pass; initialcombined101NUnitpasses; actualmanagedbenchmarkpasses. NativeAOTBenchvalidationwillrunafter125Corefreeze. Source-stampnormalizescompiler-onlyNativeAOTrestoreentries whilepreservingapplicationdependencyhashes.

125:106regularNUnitpass, actualfourcaseIcarusconformance workers1/4 pass, strict500stepseededmulticlock replaypass. Prefix reduction explicitly means prefix throughfirstmismatch, notgeneral delta debugging. Corefrozenfornativepublication/benchmarkdispatchvalidation.

125 NativeAOTpublicationpassedallthreehelpers. makebenchmark selectedactualnative artifacts/publish/VeloSim.Bench/VeloSim.Bench (source-contentstampmatchedafterstandardrestore), report artifacts/benchmarks/20261020-195733-8892514.json pass withoptimizednativekernel. BothCLIandbenchmarkexecuteELFbinarypaths; nativepreference/provenancefixvalidated.
