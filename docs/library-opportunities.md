# Library reuse review: existing VeloSim code and remaining work

Reviewed on 2026-10-26 against the current dirty `procedural-types-arrays` checkout based on `fa7cd2fbd962dde1f95ffb29e6b3e46a01a5cae1`. This supplements the [gap analysis](verilator-gap-analysis.md) and [20 work packages](verilator-work-packages.md). The [GitHub epic](https://github.com/jeffadickson/VeloSim/issues/1) and its existing issues remain the work tracker; no duplicate implementation issues are needed.

**19 opportunities cover all 20 packages**, including refactoring already-written code. “Prioritize/prototype” is a recommendation, not a dependency installation or implemented feature. No runtime code, package lock, ABI, CI setting or production dependency is changed by this review. No new build, coverage, performance, mutation or campaign result is claimed. Evidence consists of repository inspection, installed UHDM-header inspection and current upstream primary documentation. Application-specific correctness, speed, allocation, NativeAOT and Windows/Linux compatibility remain acceptance work.

## Highest-value decisions

Validation: all 19 opportunity IDs map to existing tasks, all 20 GitHub issue sections were read back successfully, and StrictDoc 0.30.4 exported the updated requirements successfully. The structured index retains hashes of inspected source files and the validation scope. Whitespace checks passed. These validate the review and task tracking; no library-specific execution or new coverage measurement was performed.

1. **Reuse the generated UHDM traversal now (LIB-01; WP-20/WP-06).** Its visitor/listener facilities are already in the pinned dependency. This directly addresses missed case-method arguments and silently discarded assertions without writing another generic tree walker. Keep semantic and scope checks.
2. **Prototype a maintained arithmetic backend (LIB-02/LIB-03; WP-07).** Slang SVInt is the closest four-state semantic fit; compare Boost.Multiprecision and LLVM APInt as alternatives with different dependency/semantic costs. Do not install three production numeric stacks. Isolate the word ABI and retain independent .NET/bit-string oracles.
3. **Consolidate existing container adapters (LIB-04; WP-09).** The library choice is already largely correct: STL map/vector/deque. The opportunity is eliminating duplicated conversion/storage glue and measuring queue representation, not replacing maps with a new custom data structure.
4. **Use existing JSON and CLI facilities (LIB-06/LIB-08).** Keep System.Text.Json, use source-generated DTO paths where appropriate, and evaluate System.CommandLine against the existing strict CLI contract. Evaluate native JSON serialization only if it reduces exporter complexity enough to justify compile/memory cost.
5. **Use libfst rather than inventing FST (LIB-11; WP-17).** Waveform hierarchy/time/source metadata still needs VeloSim integration. Solver, worker pool, SystemC and analytics candidates are later, gated integrations.

These conclusions are engineering inferences from the cited APIs and inspected code; they are not measured speedups or blanket endorsements. Existing library use is recorded separately from new candidate adoption.

## Where custom code is still justified

HDL scheduling regions and reactivation, sampled NBA values/addresses, declaration identity, X/Z conversion, net-strength resolution, SVA sampling, container lifetime/rollback, source traceability and an external ABI are domain contracts. A generic event queue, coroutine, formatter, serializer or graph executor does not supply those contracts. Keep custom code focused there. Use standard libraries for storage, arithmetic mechanics, file/process operations and serialization wherever their behavior matches.

A full frontend/library migration is not implied: Surelog/UHDM remains the current structural input. SlangFrontend currently runs a separate executable; using SVInt as a linked component is a new isolated integration to evaluate. Cocotb remains an optional external Python-based client capability, while development helpers stay C#/F# by default.

## Opportunity index

| ID | Candidate / reuse | Decision | Scope | Appropriate GitHub work packages |
|---|---|---|---|---|
| [LIB-01](#lib-01) | UHDM generated visitors/listeners | Prioritize reuse of an existing dependency | existing-and-remaining | [WP-20](https://github.com/jeffadickson/VeloSim/issues/21), [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-03](https://github.com/jeffadickson/VeloSim/issues/4), [WP-05](https://github.com/jeffadickson/VeloSim/issues/6), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-14](https://github.com/jeffadickson/VeloSim/issues/15) |
| [LIB-02](#lib-02) | slang::SVInt for four-state arithmetic | High-value prototype before replacing current arithmetic | existing-and-remaining | [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-10](https://github.com/jeffadickson/VeloSim/issues/11) |
| [LIB-03](#lib-03) | Boost.Multiprecision or LLVM APInt as numeric alternatives | Compare alternatives; select one backend, not all | existing-and-remaining | [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19), [WP-19](https://github.com/jeffadickson/VeloSim/issues/20) |
| [LIB-04](#lib-04) | Consolidate standard-library container adapters | Reuse and consolidate existing vector/deque/map facilities | existing-and-remaining | [WP-09](https://github.com/jeffadickson/VeloSim/issues/10), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9) |
| [LIB-05](#lib-05) | .NET BigInteger and ordered collections as independent references | Extend existing standard-library oracle reuse | existing-and-remaining | [WP-01](https://github.com/jeffadickson/VeloSim/issues/2), [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-09](https://github.com/jeffadickson/VeloSim/issues/10) |
| [LIB-06](#lib-06) | Use established JSON serializers instead of structural string assembly | Reuse .NET facilities; prototype one native serializer if justified | existing-and-remaining | [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-20](https://github.com/jeffadickson/VeloSim/issues/21), [WP-17](https://github.com/jeffadickson/VeloSim/issues/18), [WP-19](https://github.com/jeffadickson/VeloSim/issues/20) |
| [LIB-07](#lib-07) | Standard event storage and explicit suspended-process state | Use standard containers; retain HDL scheduling policy | existing-and-remaining | [WP-02](https://github.com/jeffadickson/VeloSim/issues/3), [WP-03](https://github.com/jeffadickson/VeloSim/issues/4), [WP-04](https://github.com/jeffadickson/VeloSim/issues/5) |
| [LIB-08](#lib-08) | System.CommandLine for existing CLI parsing | Good low-risk migration candidate, gated by compatibility tests | existing | [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19) |
| [LIB-09](#lib-09) | Process execution: retain BCL first, evaluate CliWrap narrowly | Keep current safeguards unless a candidate proves equivalent | existing | [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-01](https://github.com/jeffadickson/VeloSim/issues/2) |
| [LIB-10](#lib-10) | Native formatting and standard I/O facilities | Reuse low-level facilities, keep HDL format semantics explicit | existing-and-remaining | [WP-10](https://github.com/jeffadickson/VeloSim/issues/11), [WP-17](https://github.com/jeffadickson/VeloSim/issues/18) |
| [LIB-11](#lib-11) | GTKWave libfst for waveform files | Strong candidate for FST; avoid writing a compressor/format engine | remaining | [WP-17](https://github.com/jeffadickson/VeloSim/issues/18) |
| [LIB-12](#lib-12) | Versioned checkpoint serialization | Prefer existing JSON facilities initially; evaluate Cap'n Proto only with measured need | remaining | [WP-17](https://github.com/jeffadickson/VeloSim/issues/18), [WP-02](https://github.com/jeffadickson/VeloSim/issues/3) |
| [LIB-13](#lib-13) | Z3 for the later constrained-random milestone | Use a maintained solver when the language subset is specified | remaining | [WP-16](https://github.com/jeffadickson/VeloSim/issues/17) |
| [LIB-14](#lib-14) | oneTBB / standard bounded parallelism | Defer new runtime dependency until profiling justifies it | existing-and-remaining | [WP-18](https://github.com/jeffadickson/VeloSim/issues/19), [WP-04](https://github.com/jeffadickson/VeloSim/issues/5) |
| [LIB-15](#lib-15) | Reuse the existing verification ecosystem | Extend established tools; custom harnesses remain thin domain glue | existing-and-remaining | [WP-01](https://github.com/jeffadickson/VeloSim/issues/2), [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-09](https://github.com/jeffadickson/VeloSim/issues/10), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19) |
| [LIB-16](#lib-16) | DPI/VPI headers and real cocotb client integration | Reuse standard interfaces and clients; implement missing runtime behavior | existing-and-remaining | [WP-12](https://github.com/jeffadickson/VeloSim/issues/13), [WP-13](https://github.com/jeffadickson/VeloSim/issues/14) |
| [LIB-17](#lib-17) | SystemC as an optional adapter and selected independent reference | Useful integration candidate; not an SV scheduler replacement | remaining | [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-11](https://github.com/jeffadickson/VeloSim/issues/12), [WP-02](https://github.com/jeffadickson/VeloSim/issues/3) |
| [LIB-18](#lib-18) | DUT coverage reports and large-result analytics | Reuse report/storage tooling; defer heavy analytics for small reports | remaining | [WP-15](https://github.com/jeffadickson/VeloSim/issues/16), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19) |
| [LIB-19](#lib-19) | Typed frontend metadata for SVA, aggregates and procedural lowering | Reuse Surelog/UHDM; evaluate slang APIs selectively | existing-and-remaining | [WP-05](https://github.com/jeffadickson/VeloSim/issues/6), [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-11](https://github.com/jeffadickson/VeloSim/issues/12), [WP-14](https://github.com/jeffadickson/VeloSim/issues/15), [WP-16](https://github.com/jeffadickson/VeloSim/issues/17) |

## Common adoption gate

For any selected dependency or replacement, record: pinned version/commit and maintenance status; source and transitive license notices; reuse of already-installed/pinned components; native SDK redistribution; target compiler/OS and .NET 10/NativeAOT compatibility; default/offline build behavior; existing API/ABI and exception compatibility; measured compile time/size/runtime/allocation where relevant; independent semantic/negative/resource/failure tests; applicable property/mutation checks and implementation line/branch coverage scope. Retain the baseline until those gates pass. Research is not permission to add every candidate.

Current tool pins include FsCheck 3.3.2, Coverlet collector 10.1.0, Stryker.NET 5.0.0 and ReportGenerator 5.4.3 in repository project/tool files; this review does not update them. LLVM's rolling APInt documentation is not a claim that the same API exists in the pinned LLVM 18 environment. Candidate versions must be pinned during the corresponding spike.

## LIB-01

**UHDM generated visitors/listeners** — Prioritize reuse of an existing dependency.

Owner tasks: [WP-20](https://github.com/jeffadickson/VeloSim/issues/21), [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-03](https://github.com/jeffadickson/VeloSim/issues/4), [WP-05](https://github.com/jeffadickson/VeloSim/issues/6), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-14](https://github.com/jeffadickson/VeloSim/issues/15). Scope: existing-and-remaining.

Inspected code: `native/uhdm/rtl.cpp`, `native/uhdm/rtl_flatten.h`, `native/uhdm/rtl_original_expr.h`, `native/uhdm/rtl_subroutines.h`.

Existing situation: The exporter hand-enumerates VPI relations in several scans. The last audit confirmed that a retained UHDM assertion was omitted; method arguments in case items previously escaped a lexical-reference scan. Installed headers already provide VpiListener::listenDesign(s), UhdmListener::listenDesign and enterAny hooks.

Recommendation: Use the pinned UHDM generated traversal to inventory executable nodes and centralize exhaustive discovery; retain VeloSim's scoped identity binding, semantic validation and resource limits. Use hooks to classify handled/elaboration-only/rejected nodes, not to automatically execute everything visited.

Tradeoffs and compatibility: No new parser is needed. Verify the pinned generated API, handle ownership, pre/post-elaboration traversal, graph cycles, visit budgets and callbacks for method path elements; an upstream walker is not itself a lowering implementation.

Trust-but-verify acceptance: Replay assertion/final/primitive/clocking and case-method fixtures; compare exhaustive node counts with uhdm-dump, prove unsupported executable items fail closed, and preserve shadowing/instance-override tests.

Evidence status: Primary-source review plus local installed-header inspection only; no new visitor integration or library tests run.

Primary sources: [UHDM upstream](https://github.com/chipsalliance/UHDM).

## LIB-02

Follow-up: [128-bit SIMD source review](svint-simd-review.md), with ranked candidates and proposed qualification; no SIMD implementation or measured speedup claimed.

**slang::SVInt for four-state arithmetic** — High-value prototype before replacing current arithmetic.

Owner tasks: [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-10](https://github.com/jeffadickson/VeloSim/issues/11). Scope: existing-and-remaining.

Inspected code: `native/rtl/runtime.h`, `src/VeloSim.Core/SlangFrontend.cs`, `native/uhdm/rtl_constants.h`.

Existing situation: Bits manually implements four-limb add/multiply/divide/shift/compare using unsigned __int128. W layers X/Z masks over it. SlangFrontend currently invokes an external slang executable; it does not link SVInt into the runtime.

Recommendation: Prototype the native slang numeric library behind a narrow internal word adapter. SVInt supplies SystemVerilog-sized four-state values and operations; it is the closest semantic fit among the reviewed numeric candidates. Keep C# orchestration and the public word ABI separate.

Tradeoffs and compatibility: SVInt's linked code, heap allocation for wide/unknown values, compiler/toolchain needs and exported-SDK packaging must be measured. It does not provide event scheduling. Never expose its C++ layout as the stable external ABI or use the same implementation as the sole independent oracle.

Trust-but-verify acceptance: Compare current W and candidate behavior at widths 1/63/64/65/128/255/256/257/512/1024 for X/Z, sign extension, division, shifts and conditional merges; use independent BigInteger/bit-string/Icarus expectations, Native Windows/Linux builds and measured size/throughput/allocation.

Evidence status: Documentation and existing-code inspection only; candidate not linked or benchmarked.

Primary sources: [SVInt API](https://sv-lang.com/classslang_1_1_s_v_int.html); [slang library guide](https://sv-lang.com/developer-guide.html); [slang upstream/license](https://github.com/MikePopoloski/slang).

## LIB-03

**Boost.Multiprecision or LLVM APInt as numeric alternatives** — Compare alternatives; select one backend, not all.

Owner tasks: [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19), [WP-19](https://github.com/jeffadickson/VeloSim/issues/20). Scope: existing-and-remaining.

Inspected code: `native/rtl/runtime.h`, `native/rtl/external_model.h`, `CMakeLists.txt`.

Existing situation: Custom limbs duplicate common unsigned arithmetic; explicit __int128 use is a portability concern to check on the intended native MSVC toolchain.

Recommendation: Benchmark Boost fixed-width unsigned unchecked types for the current 256-bit plane, cpp_int with explicit HDL masking for variable widths, and LLVM APInt only if its link/deployment cost is justified. Treat these as alternatives to LIB-02 or as independently isolated numeric references.

Tradeoffs and compatibility: Boost/APInt do not supply X/Z semantics. Boost signed-magnitude types are not HDL two's-complement bit patterns; arbitrary precision is not automatically modulo a declared width. APInt requires matched widths and distinct signed/unsigned operations and adds LLVM Support linkage. Installed clang does not mean generated SDK users already have these libraries.

Trust-but-verify acceptance: Prove modulo/sign/mask/divide/shift and little-endian limb conversions independently; preserve ABI 2 or version deliberately; compare native Windows/Linux, compile time, binary size and runtime before removing current Bits.

Evidence status: Source/doc review only; no replacement, speedup or native MSVC success claimed.

Primary sources: [Boost cpp_int reference](https://www.boost.org/doc/libs/latest/libs/multiprecision/doc/html/boost_multiprecision/ref/cpp_int_ref.html); [Boost numeric caveats](https://www.boost.org/doc/libs/latest/libs/multiprecision/doc/html/boost_multiprecision/tut/map/faq.html); [LLVM APInt](https://llvm.org/doxygen/classllvm_1_1APInt.html).

## LIB-04

**Consolidate standard-library container adapters** — Reuse and consolidate existing vector/deque/map facilities.

Owner tasks: [WP-09](https://github.com/jeffadickson/VeloSim/issues/10), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9). Scope: existing-and-remaining.

Inspected code: `native/rtl/containers.h`, `native/rtl/runtime.h`, `src/VeloSim.Core/RtlAssociative.cs`, `src/VeloSim.Core/RtlQueues.cs`.

Existing situation: containers.h has DynamicArray/Queue/AssociativeArray wrappers, while runtime.h exposes separate generated-model allocation/queue/associative adapters. Both already use STL storage. The primitive Queue and AssociativeArray conversions allocate a one-element DynamicArray copy, while generated queues use vector front erase/insert.

Recommendation: Share typed defaults/conversion/capacity/ownership logic and remove redundant paths only after identifying their separate contracts. Keep std::map for ordered associative keys and std::vector for contiguous dynamic arrays; evaluate std::deque for queue ends under actual snapshot/index workloads.

Tradeoffs and compatibility: Current 256-element bounds can make vector adequate; no presumed speedup. deque changes contiguity and iterator/reference behavior, and moving it into vector-based generated memory inventory is an integration change. Keep signed-key normalization, unknown-key policy, missing reads and rollback custom and tested.

Trust-but-verify acceptance: Run the same semantic/overflow/rollback corpus through both old paths and the consolidated adapter; benchmark mixed push/pop/index/snapshot operations; preserve negative-key and missing-read independent tests.

Evidence status: Existing source reviewed; prior focused container evidence remains historical, not new refactor qualification.

Primary sources: [C++ deque](https://learn.microsoft.com/en-us/cpp/standard-library/deque-class?view=msvc-170); [C++ map](https://learn.microsoft.com/en-us/cpp/standard-library/map-class?view=msvc-170).

## LIB-05

**.NET BigInteger and ordered collections as independent references** — Extend existing standard-library oracle reuse.

Owner tasks: [WP-01](https://github.com/jeffadickson/VeloSim/issues/2), [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-09](https://github.com/jeffadickson/VeloSim/issues/10). Scope: existing-and-remaining.

Inspected code: `tests/VeloSim.Tests/RtlAssociativeTests.cs`, `src/VeloSim.Core/FourStateWord.cs`, `src/VeloSim.Core/FourStateArithmetic.cs`.

Existing situation: The associative tests already use SortedDictionary<BigInteger,byte>, independent of native biased-key ordering. The separate managed FourStateWord intentionally remains ulong/64-bit.

Recommendation: Use BigInteger plus explicit width/sign/mask rules for wider arithmetic expectations and SortedDictionary/List models for container expectations. Preserve independent bit-string truth tables for X/Z. Do not widen the legacy managed production IR merely to host an oracle or share the candidate native backend with the expected-result generator.

Tradeoffs and compatibility: BigInteger supplies mathematical integers rather than HDL width, signedness or four-state semantics. Oracle code still needs explicit masks, shift rules and denominator/unknown handling.

Trust-but-verify acceptance: Known analytical boundary cases and minimized cross-tool disagreements validate the oracle first; record seeds and counterexamples and ensure expected results do not call the production adapter.

Evidence status: Current use inspected; no new oracle execution in this review.

Primary sources: [BigInteger .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.biginteger?view=net-10.0); [SortedDictionary .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2?view=net-10.0).

## LIB-06

**Use established JSON serializers instead of structural string assembly** — Reuse .NET facilities; prototype one native serializer if justified.

Owner tasks: [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-20](https://github.com/jeffadickson/VeloSim/issues/21), [WP-17](https://github.com/jeffadickson/VeloSim/issues/18), [WP-19](https://github.com/jeffadickson/VeloSim/issues/20). Scope: existing-and-remaining.

Inspected code: `native/uhdm/json_escape.h`, `native/uhdm/rtl.cpp`, `native/uhdm/rtl_flatten.h`, `src/VeloSim.Core/RtlDesign.cs`, `src/VeloSim.Core/ToolCli.cs`.

Existing situation: Managed code already uses System.Text.Json, and ToolCli writes structured errors with Utf8JsonWriter. Several internal IR fragments and the native exporter are still built by manual string concatenation around a custom JSON escaper.

Recommendation: Reuse Utf8JsonWriter/JsonDocument and source-generated typed DTO serialization for managed reports/IR/checkpoints. Evaluate nlohmann/json for native structural output if it removes enough string/escaping code; compare its compile/memory cost with retaining the small already-tested escape helper. Do not add another .NET JSON stack.

Tradeoffs and compatibility: Serializer adoption must preserve strict unknown/duplicate-field policy, node/depth/byte budgets, number precision, UTF-8 validation, determinism and source spans. JSON libraries do not perform HDL typing. Native DOM construction can amplify memory; an isolated writer may be preferable.

Trust-but-verify acceptance: Golden normalized IR, control characters/Unicode/invalid UTF-8, deep/large/malformed input and budget tests; NativeAOT publish/run with reflection disabled for managed DTO paths; do not count serializer success as valid executable HDL.

Evidence status: Source/doc review; no new dependency, serializer migration or AOT certification.

Primary sources: [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation); [JSON for Modern C++](https://json.nlohmann.me/).

## LIB-07

**Standard event storage and explicit suspended-process state** — Use standard containers; retain HDL scheduling policy.

Owner tasks: [WP-02](https://github.com/jeffadickson/VeloSim/issues/3), [WP-03](https://github.com/jeffadickson/VeloSim/issues/4), [WP-04](https://github.com/jeffadickson/VeloSim/issues/5). Scope: existing-and-remaining.

Inspected code: `native/rtl/timed_events.h`, `native/rtl/external_model.h`, `src/VeloSim.Core/RtlDesign.cs`.

Existing situation: TimedEvents already uses std::map of timestamp/delta/region/serial keys. It transports opaque IDs and does not implement executable HDL regions or suspended process frames.

Recommendation: Reuse ordered maps and per-slot deques/standard RAII for event storage, and explicit copyable process frames for SDK rollback. Research SystemC only as a co-simulation adapter (LIB-17), not an automatic substitute for SystemVerilog active/inactive/NBA/reactivation semantics.

Tradeoffs and compatibility: A generic priority queue or coroutine library supplies mechanics, not the required region fixpoint. Coroutine frames and callback captures must not silently make rollback impossible. Keep simulated tick precision/overflow distinct from wall-clock chrono measurements.

Trust-but-verify acceptance: Independent #0/NBA/reactivation traces, concurrent startup, sampled RHS/address, host-time jumps, cancellation, overflow/budget rejection and snapshot replay must pass before any scheduler closure.

Evidence status: Architecture/source review; no executing timed scheduler or new queue implementation supplied.

Primary sources: [C++ map](https://learn.microsoft.com/en-us/cpp/standard-library/map-class?view=msvc-170); [C++ deque](https://learn.microsoft.com/en-us/cpp/standard-library/deque-class?view=msvc-170); [SystemC semantic scope](https://github.com/accellera-official/systemc).

## LIB-08

**System.CommandLine for existing CLI parsing** — Good low-risk migration candidate, gated by compatibility tests.

Owner tasks: [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19). Scope: existing.

Inspected code: `src/VeloSim.Core/ToolCli.cs`, `src/VeloSim.Core/BenchmarkCli.cs`.

Existing situation: ToolCli repeatedly parses positional option/value pairs and duplicate-option sets by hand, with custom usage text. Existing --json-errors and exit categories are intentional product behavior.

Recommendation: Evaluate a pinned System.CommandLine release with explicit handlers/validation to centralize help, command structure and option parsing; preserve the injected IProcessRunner boundary and machine-readable errors.

Tradeoffs and compatibility: Avoid reflection-heavy binding. Parser defaults such as response files, option ordering, duplicate handling and culture differ from the current strict contract; do not change them accidentally. The package advertises trimming support, but this application's NativeAOT build still needs execution evidence.

Trust-but-verify acceptance: Replay all existing valid/invalid/duplicate/unknown/cancellation/JSON-error CLI fixtures unchanged or document intentional versioned changes; publish and run NativeAOT on target platforms and measure startup/size.

Evidence status: Current manual parser and official documentation reviewed; no package added.

Primary sources: [System.CommandLine overview](https://learn.microsoft.com/en-us/dotnet/standard/commandline/).

## LIB-09

**Process execution: retain BCL first, evaluate CliWrap narrowly** — Keep current safeguards unless a candidate proves equivalent.

Owner tasks: [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-01](https://github.com/jeffadickson/VeloSim/issues/2). Scope: existing.

Inspected code: `src/VeloSim.Core/ProcessRunner.cs`, `src/VeloSim.Core/ArtifactMetadata.cs`.

Existing situation: ProcessRunner already delegates launch/async IO to System.Diagnostics.Process but adds Linux setsid/group kill, bounded concurrent output drains, timeout/cancellation distinctions and preserved diagnostics.

Recommendation: Keep BCL Process and the tested wrapper as baseline. CliWrap can reduce argument/pipeline boilerplate, but evaluate it only behind IProcessRunner with the existing hardening suite; do not replace hard limits with unbounded buffered capture.

Tradeoffs and compatibility: Process-tree termination, child-retained pipe handles, quoting, timeout output, platform isolation and NativeAOT are behavioral requirements, not solved by fluent syntax. Reuse the installed Git CLI for authentication/repo operations unless a specific LibGit2-style benefit is demonstrated.

Trust-but-verify acceptance: Cross-platform child/grandchild timeout, cancellation, output flood, spaces/Unicode args, failure classification, missing-tool and no-leaked-process tests must match current behavior.

Evidence status: Source/doc review only; replacing the hardened wrapper is not recommended yet.

Primary sources: [Process .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process?view=net-10.0); [CliWrap upstream](https://github.com/Tyrrrz/CliWrap).

## LIB-10

**Native formatting and standard I/O facilities** — Reuse low-level facilities, keep HDL format semantics explicit.

Owner tasks: [WP-10](https://github.com/jeffadickson/VeloSim/issues/11), [WP-17](https://github.com/jeffadickson/VeloSim/issues/18). Scope: existing-and-remaining.

Inspected code: `native/rtl/runtime.h`, `native/uhdm/rtl.cpp`, `src/VeloSim.Core/RtlExternalSdk.cs`.

Existing situation: HDL display/file/time system tasks remain open; generated host traces and text assembly currently supply only test/runtime diagnostics.

Recommendation: Evaluate std::format where supported by pinned compilers and {fmt} as a portable alternative for host diagnostics and output buffers. Use standard file streams/filesystem and C#/BCL decimal/BigInteger for reference calculations rather than hand-built number/file libraries.

Tradeoffs and compatibility: Neither fmt's brace syntax nor host printf implements SystemVerilog %b/%h/%t/%m, X/Z rendering, $strobe/$monitor region timing, locale or SV real rounding. Keep a tested HDL-format translation layer; preserve the generated SDK's dependency manifest.

Trust-but-verify acceptance: Hand-calculated width/padding/signed/X/Z/real/time format cases plus file failures, binary content, Unicode paths and region-sensitive printing; benchmark only after correct output.

Evidence status: Candidate review only; no HDL formatting behavior implemented.

Primary sources: [fmt official documentation](https://fmt.dev/latest/); [BigInteger reference](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.biginteger?view=net-10.0).

## LIB-11

**GTKWave libfst for waveform files** — Strong candidate for FST; avoid writing a compressor/format engine.

Product requirement: [VS-TRACE-COMPRESSED-001](compressed-waveforms.md) (Draft) requires compressed waveform output. libfst remains implementation information to evaluate; the requirement does not mandate this dependency or establish current support.

Owner tasks: [WP-17](https://github.com/jeffadickson/VeloSim/issues/18). Scope: remaining.

Inspected code: `src/VeloSim.Core/RtlExternalSdk.cs`, `native/rtl/external_model.h`.

Existing situation: Current VS_RTL strings are comparison traces, not a general hierarchical waveform writer. libfst is a separate GTKWave C library with writer/reader infrastructure.

Recommendation: Use a pinned libfst build for FST once source/hierarchy/time metadata exists; keep a simple VCD path if it meets initial needs. Include its bundled compression notices and build dependencies explicitly.

Tradeoffs and compatibility: The upstream license includes component notices; audit the chosen revision rather than copying GTKWave viewer licensing assumptions. Thread ownership, timestamps, arrays/wide X/Z values, flushing and failure handling still require VeloSim integration.

Trust-but-verify acceptance: Open produced waveforms with an independent reader/GTKWave; verify hierarchy, widths, X/Z and exact times against golden traces; test empty/large/interrupted outputs on native Windows/Linux.

Evidence status: Official library/docs reviewed; no writer installed, linked or executed.

Primary sources: [libfst upstream](https://github.com/gtkwave/libfst); [GTKWave FST documentation](https://gtkwave.github.io/gtkwave/internals/fst-file-format.html).

## LIB-12

**Versioned checkpoint serialization** — Prefer existing JSON facilities initially; evaluate Cap'n Proto only with measured need.

Owner tasks: [WP-17](https://github.com/jeffadickson/VeloSim/issues/18), [WP-02](https://github.com/jeffadickson/VeloSim/issues/3). Scope: remaining.

Inspected code: `native/rtl/external_model.h`, `native/rtl/timed_events.h`, `src/VeloSim.Core/RtlExternalSdk.cs`.

Existing situation: SDK exception rollback is a copy of live in-memory state, not a stable checkpoint format. UHDM's serialization is a frontend interchange format, not a runtime model snapshot.

Recommendation: Define typed versioned state DTOs and stable process/event IDs first. Compare source-generated System.Text.Json for a managed control/manifest boundary with generated native Cap'n Proto serialization if checkpoint size/throughput justifies it; reuse an already-pinned frontend dependency only when exported hosts can package it independently.

Tradeoffs and compatibility: Never serialize C++ object layout, pointers, iterators, std::function targets or raw coroutine frames. Checkpoint schema must include words/masks, container ordering, scheduler state, source hash and ABI version. Presence inside Surelog does not make Cap'n Proto free for standalone SDK consumers.

Trust-but-verify acceptance: Restart reproduces future events/outputs; truncated/corrupt/wrong-source/wrong-ABI checkpoints reject atomically; measure file size/load/save time and cross-platform compatibility.

Evidence status: Design/candidate review; no checkpoint serializer implemented.

Primary sources: [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation); [Cap'n Proto C++ serialization](https://capnproto.org/cxx.html); [Cap'n Proto code generation](https://capnproto.org/capnp-tool.html).

## LIB-13

Product requirement: **VS-RANDOM-CONSTRAINT-001** (Draft), [acceptance and Z3 information](constrained-randomization.md). Z3 remains an informational candidate.

**Z3 for the later constrained-random milestone** — Use a maintained solver when the language subset is specified.

Owner tasks: [WP-16](https://github.com/jeffadickson/VeloSim/issues/17). Scope: remaining.

Inspected code: `native/uhdm/rtl_types.h`, `src/VeloSim.Core/RtlDesign.cs`.

Existing situation: VeloSim has no class constraint solver. Creating one would duplicate general SMT machinery, but using an SMT library does not implement class lifetimes, random stability or the SystemVerilog constraint language.

Recommendation: Evaluate Z3's native C/C++ API for runtime integration and .NET API for C#/F# qualification/orchestration. Keep constraint-to-SMT translation small and explicit; test SAT, UNSAT and timeout/unknown results without turning unknown into success.

Tradeoffs and compatibility: Package native DLL/SO dependencies and verify .NET NativeAOT binding behavior. Random model selection is not automatically SV distribution/random stability. Solver build tooling may have mandatory upstream prerequisites; do not rewrite upstream tooling solely to avoid them.

Trust-but-verify acceptance: Independently enumerable small constraint domains, signed-width conversions, distributions, deterministic replay, unsatisfied constraints and timeouts; do not call sampled solver results a proof of broader simulator correctness.

Evidence status: Research only; no solver added or constraints executed.

Primary sources: [Z3 upstream and language bindings](https://github.com/Z3Prover/z3).

## LIB-14

**oneTBB / standard bounded parallelism** — Defer new runtime dependency until profiling justifies it.

Owner tasks: [WP-18](https://github.com/jeffadickson/VeloSim/issues/19), [WP-04](https://github.com/jeffadickson/VeloSim/issues/5). Scope: existing-and-remaining.

Inspected code: `native/scheduler.cpp`, `native/ir/engine.cpp`, `src/VeloSim.Core/RtlDesign.cs`, `CMakeLists.txt`.

Existing situation: The small native schedulers create thread-based work partitions; the structural RTL backend has no established process-parallel speedup. Correct worker-count output is already tested in limited scopes.

Recommendation: Use existing bounded .NET/native facilities for independent jobs first. Evaluate oneTBB arenas/task groups for a persistent native worker pool or dependency graph only after profiling useful parallel regions. Keep all HDL dependency/commit/region policy outside the generic executor.

Tradeoffs and compatibility: New binary/runtime deployment and nested parallel oversubscription costs may exceed gains for bounded designs. A task scheduler cannot infer race-free HDL partitions; exceptions and deterministic allowed ordering must survive.

Trust-but-verify acceptance: Identical source/output and reference checks, worker budget/cancellation/failure propagation, repeat timing/memory measurements and TSAN or equivalent justified race checks; report no gain when none is measured.

Evidence status: Source/doc review only; no parallel speedup claimed.

Primary sources: [oneTBB upstream](https://github.com/uxlfoundation/oneTBB).

## LIB-15

**Reuse the existing verification ecosystem** — Extend established tools; custom harnesses remain thin domain glue.

Owner tasks: [WP-01](https://github.com/jeffadickson/VeloSim/issues/2), [WP-07](https://github.com/jeffadickson/VeloSim/issues/8), [WP-09](https://github.com/jeffadickson/VeloSim/issues/10), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19). Scope: existing-and-remaining.

Inspected code: `tests/VeloSim.Tests/VeloSim.Tests.csproj`, `.config/dotnet-tools.json`, `tools/ContainerEventMutationProbe.cs`, `tools/RuntimeFuzzCampaign.cs`.

Existing situation: NUnit/FsCheck/Coverlet and pinned Stryker.NET/ReportGenerator are already present. Domain-specific C# mutation probes and native campaigns also exist.

Recommendation: Extend FsCheck generators/shrinkers and Stryker.NET rather than inventing new generic engines. Evaluate SharpFuzz with an appropriate managed instrumentation build and AFL++, and Google FuzzTest/AFL++ for native adapters. Evaluate Mull for native generic mutations if its LLVM requirements match the pinned toolchain; retain focused semantic mutations where generic tools cannot represent the fault.

Tradeoffs and compatibility: Instrumented managed campaigns and NativeAOT release verification are distinct scopes. Tool setup is not a run result. Mutation survivors, excluded code and compiler/tool version mismatches must be reported; no replacement may silently reduce retained corpus or failure classification.

Trust-but-verify acceptance: Record exact tool/source versions, seeds/corpus hashes, minimized failures, actual execution counts, mutation outcomes and coverage line/branch scopes; retain independent oracles rather than production-code mirrors.

Evidence status: Configuration and documentation review only; no new campaign, mutation score or coverage result.

Primary sources: [FsCheck](https://fscheck.github.io/FsCheck/); [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/); [Coverlet](https://github.com/coverlet-coverage/coverlet); [SharpFuzz](https://github.com/Metalnem/sharpfuzz); [AFL++](https://github.com/AFLplusplus/AFLplusplus); [Google FuzzTest](https://github.com/google/fuzztest); [Mull](https://mull.readthedocs.io/en/latest/).

## LIB-16

**DPI/VPI headers and real cocotb client integration** — Reuse standard interfaces and clients; implement missing runtime behavior.

Owner tasks: [WP-12](https://github.com/jeffadickson/VeloSim/issues/13), [WP-13](https://github.com/jeffadickson/VeloSim/issues/14). Scope: existing-and-remaining.

Inspected code: `native/rtl/vpi_user.h`, `native/rtl/vpi.cpp`, `src/VeloSim.Core/RtlExternalSdk.cs`.

Existing situation: VeloSim maintains a deliberately small VPI header/runtime. Standard headers can supply consistent declarations but do not create callbacks, hierarchy or DPI implementations.

Recommendation: Evaluate a pinned standard svdpi.h/vpi_user.h source with documented redistribution terms, generated typed DPI-C stubs and layout tests. Use cocotb's existing GPI/VPI client as integration validation rather than writing a substitute testbench-client layer. Keep cocotb/Python an optional external product integration, not a new default helper language.

Tradeoffs and compatibility: Header availability is not API support; full headers must not imply every symbol is implemented. Avoid libffi unless genuinely dynamic signatures justify it; ordinary generated DPI stubs can be typed. Client timing behavior depends on WP-02/03 and actual runtime callbacks.

Trust-but-verify acceptance: C/C++ layout/calling-convention and symbol tests, same host/HDL imports/exports, wide/X/Z values, timed callbacks and a real pinned cocotb smoke suite on supported platforms.

Evidence status: Interface/source research; VeloSim is not claimed to be cocotb-compatible.

Primary sources: [Verilator DPI/VPI integration and header isolation](https://verilator.org/guide/latest/connecting.html); [cocotb simulator support](https://docs.cocotb.org/en/stable/simulator_support.html).

## LIB-17

**SystemC as an optional adapter and selected independent reference** — Useful integration candidate; not an SV scheduler replacement.

Owner tasks: [WP-19](https://github.com/jeffadickson/VeloSim/issues/20), [WP-11](https://github.com/jeffadickson/VeloSim/issues/12), [WP-02](https://github.com/jeffadickson/VeloSim/issues/3). Scope: remaining.

Inspected code: `src/VeloSim.Core/RtlExternalSdk.cs`, `native/rtl/external_model.h`, `native/rtl/runtime.h`.

Existing situation: The official Accellera SystemC implementation is a separate C++ modeling library with its own standardized semantics. Current VeloSim exposes ordinary generated C++ models, not SC_MODULE integration.

Recommendation: Use the official SystemC library for an explicit SC_MODULE/TLM adapter when required; evaluate its digital logic value facilities as a secondary reference for a carefully matched resolution subset. Retain SV scheduling/strength semantics in VeloSim or explicitly scope unsupported differences.

Tradeoffs and compatibility: Event/delta behavior and scheduling boundaries do not automatically match SystemVerilog. SystemC runtime/global context, time precision, ABI, host thread ownership and Apache/component notices require qualification.

Trust-but-verify acceptance: A small bidirectional co-simulation example, clock/time handoff and lifecycle tests; compare matched four-state tables independently and reject any claim of full SV net/strength conformance from SystemC agreement.

Evidence status: Research only; no SystemC dependency or compatibility certification.

Primary sources: [Official SystemC implementation](https://github.com/accellera-official/systemc).

## LIB-18

**DUT coverage reports and large-result analytics** — Reuse report/storage tooling; defer heavy analytics for small reports.

Owner tasks: [WP-15](https://github.com/jeffadickson/VeloSim/issues/16), [WP-18](https://github.com/jeffadickson/VeloSim/issues/19). Scope: remaining.

Inspected code: `src/VeloSim.Core/RtlDesign.cs`, `src/VeloSim.Core/RtlExternalSdk.cs`, `tools/`.

Existing situation: Existing Coverlet/LLVM reports measure simulator source, not simulated HDL execution. Current small evidence manifests already use JSON.

Recommendation: Reuse established coverage interchange/report tooling for exported DUT counters where semantics match. For large campaigns, evaluate Apache Arrow .NET record batches and a documented DuckDB ingestion/query path; retain JSON for small manifests and do not add columnar/database dependencies just to store a few counters.

Tradeoffs and compatibility: Arrow IPC is not Parquet, and Arrow .NET does not automatically provide a Parquet writer. If Parquet is required, research/pin that adapter separately. Verify chosen DuckDB client ownership/native packaging/AOT; source mapping, bins/crosses and sampling remain domain work.

Trust-but-verify acceptance: Hand-counted HDL counter values survive export/merge/analytics exactly, schema/source hashes and uint64 counter overflow are explicit, invalid/truncated data rejects, and memory/size/query costs justify the added dependencies.

Evidence status: Candidate review only; no DUT coverage or analytics integration claimed.

Primary sources: [Apache Arrow .NET](https://arrow.apache.org/dotnet/current/index.html); [DuckDB upstream; client integration still to qualify](https://github.com/duckdb/duckdb).

## LIB-19

**Typed frontend metadata for SVA, aggregates and procedural lowering** — Reuse Surelog/UHDM; evaluate slang APIs selectively.

Owner tasks: [WP-05](https://github.com/jeffadickson/VeloSim/issues/6), [WP-06](https://github.com/jeffadickson/VeloSim/issues/7), [WP-08](https://github.com/jeffadickson/VeloSim/issues/9), [WP-11](https://github.com/jeffadickson/VeloSim/issues/12), [WP-14](https://github.com/jeffadickson/VeloSim/issues/15), [WP-16](https://github.com/jeffadickson/VeloSim/issues/17). Scope: existing-and-remaining.

Inspected code: `native/uhdm/rtl_types.h`, `native/uhdm/rtl_subroutines.h`, `native/uhdm/rtl_flatten.h`, `src/VeloSim.Core/SlangFrontend.cs`.

Existing situation: The project already benefits from external elaboration, but custom lowering still reconstructs some constant expressions, member layout, scope identity and statement traversal.

Recommendation: Reuse pinned UHDM typed nodes and elaborated identity first; evaluate slang constant-value/type/source-management APIs where they replace demonstrably duplicated compiler work or supply an independent diagnostic comparison. Avoid another regex/parser/type-layout implementation.

Tradeoffs and compatibility: Neither frontend is a complete execution engine. Slang as a numeric component (LIB-02) is not a decision to migrate elaboration. Different frontend identities/folding rules must never be mixed by bare names. A regex/automaton library alone does not implement temporal SVA sampling; retain a typed monitor translator with explicit semantics.

Trust-but-verify acceptance: Golden typed layout/constants/scopes, original-source spans and generated instance overrides; compare against independent expectations, and prove every accepted executable property or construct has observable behavior.

Evidence status: Source/API research only; no frontend migration or SVA implementation.

Primary sources: [UHDM](https://github.com/chipsalliance/UHDM); [slang developer guide](https://sv-lang.com/developer-guide.html).

