# VeloSim

New worktree slice: [explicit-time SDK execution](docs/timed-execution.md), including wait-first `always @*`, resumable delays/waits, runtime identifier intra-assignment delays, internal clocks, sampled assignments, conservative race diagnostics and bounded fork/named-event behavior. Pure automatic subroutines also support whole-word local initializers, task early return and [input defaults with preserved positional/named slots](docs/procedural-types-arrays.md#input-defaults-and-frontend-compatibility). Constant-default width evaluation requires re-elaboration with frontend argument protocol v4. The six broad work packages remain open. [Experimental SVInt AVX128 checks](docs/svint-simd-review.md) preserve unchanged SVInt and current-runtime comparisons; production arithmetic is unchanged.

Library planning: [reuse review for existing code and remaining features](docs/library-opportunities.md), mapped into the existing GitHub work packages. Recommendations require compatibility and behavioral qualification before adoption.

Current planning: [detailed Verilator gap analysis](docs/verilator-gap-analysis.md), [20 work packages](docs/verilator-work-packages.md), and [live GitHub task epic](https://github.com/jeffadickson/VeloSim/issues/1). The audit separates current worktree support, historical evidence and open requirements; milestone descriptions below retain their historical scope.

Current development: [bounded HDL queues](docs/queues.md) under focused qualification. Earlier development: [clockless models, function control flow and constant captures](docs/functions-100.md). Previous development: [combinational and automatic-subroutine qualification](docs/combinational-subroutines-200.md), covering bounded `always_comb` and pure automatic functions/tasks. The previous milestone is [200 requirement and coverage qualification cases](docs/requirements-200.md) and [stable StrictDoc requirements](requirements/velosim.sdoc), extending isolated blocking assignments to selected words, concatenations and fixed-memory elements. See the [earlier execution contract](docs/open-items-100.md) and [process, hierarchy and signed-word bounds](docs/rtl-processes-hierarchy-signed.md). Older milestone reports below retain their historical scope.

Open-source experimental Verilog/SystemVerilog simulation project.

Start with the illustrated [HTML introduction](docs/introduction.html) to see how Surelog, UHDM, native compilation and independent simulation checks fit together. It includes an interactive clock-edge walkthrough and explains the current execution boundaries. Download/open the HTML in a browser to view it; GitHub's file view displays its source.

## Status

The [dynamic-array HDL slice](docs/dynamic-arrays.md) adds bounded allocation, self-preserving resize, indexed access, size and delete. The [bounded HDL queue slice](docs/queues.md) adds ordered push/pop, indexing, size and deletion under focused qualification. Associative HDL lowering and suspended-process scheduling remain open. Native container and timed-event [runtime primitives](docs/container-events.md) and optional [LLVM fuzz campaigns](docs/runtime-fuzz.md) support continued development.

This branch adds bounded untimed startup and runtime loops, integral enums and integer types, packed aggregates, fixed rank-one-to-four arrays, dynamic-array lifetimes and RTL words through 256 bits. See [the previous qualification milestone](docs/procedural-types-arrays.md) and the newer feature notes above. Associative arrays remain open; queues have the bounded subset described above. External C++ hosts must rebuild against word ABI 2; union models require the corrected frontend described there.

Early research prototype. C++20 kernels execute restricted four-state processes; compiled .NET10 C# helpers validate IR, orchestrate frontends and run regressions. The real Surelog/UHDM structural path supports bounded edge-driven processes, instance hierarchy, signed words through 256 bits, two-state storage and fixed word memories. See the current execution contract above for restrictions.

## Build and test

Run in Linux or WSL with .NET10 SDK, CMake, Ninja, and a C++20 compiler:

```sh
make test JOBS=4
make coverage JOBS=4
make publish JOBS=4
```

Native builds use CMake's Ninja generator and an explicit parallel limit. The Makefile bootstraps a compiled C# build helper and prefers its current NativeAOT executable after publication. The published CLI is `artifacts/publish/VeloSim.Tools/VeloSim.Tools`; native libraries are copied beside it. NativeAOT publication currently targets Linux x64.

Coverlet measures managed C# code under VSTest. LLVM source coverage measures native kernels and JSON escaping separately. ReportGenerator produces an HTML report containing both; native and managed coverage must not be confused. LLVM coverage requires a matching Clang/LLVM installation (`VELOSIM_LLVM_BIN` can select it). Reports are under `artifacts/coverage/` and `artifacts/native-coverage/html/`.

The installed Surelog/UHDM tools are optional for basic IR tests. See [real HDL integration](docs/real_uhdm_gate.md) for the pinned revisions and [development checkpoints](docs/development-iterations.md) for iteration/review evidence. Upstream dependencies retain their own build generators.

## Project layout

- `src/VeloSim.Core/` — compiled C# validation, frontend and native interop library
- `src/VeloSim.Tools/` — compiled CLI, publishable with NativeAOT
- `tools/VeloSim.Build/` — compiled build/test/coverage helper
- `historical/python/` — archived Python prototype reference
- `native/` — C++ simulation and tracing experiments
- `tests/` — unit and regression tests
- `verification/cocotb/` — cocotb testbenches
- `examples/` — Verilog/SystemVerilog fixtures
- `docs/` — requirements and milestone notes
- `tools/` — local test and differential runners
- `.github/workflows/` — CI

Historical Python sources remain archived as a reference but are not the active Makefile/CI helper path. Do not infer feature completeness from directory names or test counts.

Run `make doctor JOBS=4` for a compiled tool/version report. `make coverage` validates separate managed/native coverage and writes fingerprinted `gate.json` evidence; optional `VELOSIM_MIN_MANAGED_LINE` and `VELOSIM_MIN_NATIVE_LINE` percentages enforce thresholds. `make coverage-check` rechecks the latest successful report. The build helper also accepts `coverage-check path/to/Cobertura.xml`.

The native IR module exposes ABI version2 and the scalar scheduler ABI version1 and reject incompatible versions before allocation. Rebuild shared libraries after ABI changes. Native IR ticks advance only after successful atomic drives. Linux exports are restricted to the declared C API; a compiled C11 consumer is included in CTest.

`make bundle JOBS=4` publishes and stages native Linux-x64 tools, libraries, C headers and a runnable IR example, verifies file hashes, runs the packaged executables, and writes a compressed tar archive under `artifacts/bundles`. The bundled `VeloSim.Build bundle-check <directory>` verifies extracted files without requiring a checkout. The manifest records source revision; `build.txt` records whether the working tree was dirty.

`make benchmark JOBS=4` runs a bounded multi-process worker comparison and verifies every result. It prefers a source-matched published NativeAOT benchmark. Reports contain timings, checksums, native optimization profile and implementation fingerprints; the measured boundary includes C# drive/interop overhead. The standalone `VeloSim.Bench --help` lists workload options.

Coverage uses Coverlet and ReportGenerator for C# plus LLVM18 for native code. make coverage-current verifies hashes of measured binaries, reports and current first-party source/dependency/build inputs. Optional gates: VELOSIM_MIN_MANAGED_LINE, VELOSIM_MIN_NATIVE_LINE, VELOSIM_MIN_MANAGED_BRANCH and VELOSIM_MIN_NATIVE_BRANCH (0–100).

External-tool capture is bounded to16,777,216 characters per stream by default. Linux commands run in owned setsid process groups so deadline and output overflow cleanup includes descendants after their parent exits. Non-Linux uses .NET process-tree cleanup. Literal include provenance is bounded to128 nested levels,4096 files and64MiB total. Scalar/word workflows enforce total state/process work budgets before execution.

Native CLI word-conformance compares generated strict-SystemVerilog fixtures against Icarus, the independent managed oracle and native workers. word-differential runs an individual word fixture; word-inspect reads report structure, word-replay verifies current artifact/tool/library hashes before rerunning, and word-reduce saves the prefix through the first mismatch with exact reproduction arguments. Experimental bit-precise increment/reset operators are excluded from strict HDL comparison. This does not extend the counter-only frontend lowering scope.

Coverage source hashes preserve application dependency locks and lock metadata, with compiler-only NativeAOT restore entries normalized out. This keeps ordinary test and native publish restores compatible without hiding application dependency changes. Reports retain measured managed/native binary hashes and tool versions separately.

word-seeded-conformance records a deterministic seed and known/four-state phases across five width dimensions, all ten strict operations and NBA history, then compares repeated full native traces. For example: word-seeded-conformance --output-dir artifacts/seeded --seed 3735928559 --steps 256 --workers 1,4,8 --repetitions 3. Individual word differential runs preserve completed evidence on cancellation; pre-cancel and oracle preflight can stop before a run directory is created.

### Actual synchronous FIFO milestone

VeloSim now executes `examples/surelog_fifo.sv` through a separate bounded native RTL compilation backend: actual source → Surelog/UHDM → structural `velosim.rtl.v1` IR → generated C++ → CMake/Ninja executable. This is not a hardcoded FIFO model. The existing flat-process IR/kernel remains available; its counter frontend is unchanged.

Build the structural exporter against the installed pinned UHDM package:

```sh
cmake -S . -B artifacts/with-uhdm -G Ninja -DCMAKE_BUILD_TYPE=Release -DVELOSIM_BUILD_UHDM=ON -DCMAKE_PREFIX_PATH=/opt/velosim
cmake --build artifacts/with-uhdm --parallel 4
make publish JOBS=4
artifacts/publish/VeloSim.Tools/VeloSim.Tools fifo-differential examples/surelog_fifo.sv --top surelog_fifo --exporter artifacts/with-uhdm/bin/velosim-uhdm-rtl --output-dir artifacts/fifo-run --cycles 1000 --workers 1,4,8 --build-jobs 4
```

The workflow compares every output each cycle with Icarus, including retained/unknown `dout` while empty. An independent queue checks full/empty flags and valid data ordering using pre-edge acceptance: a simultaneous request at full rejects push and accepts pop; at empty it accepts push and rejects pop. The report records directed/seeded stimulus, scenario counts, differences, tool/compiler/Ninja fingerprints, copied build inputs and complete traces. A wrong-read-pointer mutation is detected by the queue even though both simulators agree.

Verified dimensions are width/depth 1/2, 8/4, 16/8 and 64/8, with reset, overflow, underflow, pointer wrap, simultaneous requests and X/Z payloads. Controls are binary in these tests. Exactly one posedge `always_ff`, unsigned words up to64bits, zero-based unpacked word memories, ordered NBAs, if/exact-case, supported expressions and acyclic continuous assignments are accepted. Two-state declarations, undriven non-input signals, pull/resolved nets, initializers, signed state, fill literals other than zero, timing/strengths, primitives, generate scopes, hierarchy, functions/tasks and clock-dependent continuous logic are rejected. No general SystemVerilog or multi-process scheduling claim is made. Worker configurations are recorded, but the single sequential process executes serially; no parallel speedup is claimed.

Enable actual frontend integration tests and instrumented core/kernel/runtime coverage with `VELOSIM_REAL_FIFO=1 make coverage JOBS=4`. Optional `VELOSIM_UHDM_RTL` selects the exporter. The UHDM exporter itself is exercised by real positive/negative integration tests but is outside the default LLVM kernel/runtime coverage denominator. Native FIFO compilation needs a source checkout, C++/Ninja, the pinned frontend and Icarus; the produced design executable runs without .NET.

The structural backend also accepts unsigned bitwise `&`, `|`, `^` and `~`. AND/OR preserve controlling known bits, and XOR/NOT convert affected X/Z bits to X. Operands extend to the expression context before evaluation, including nested arithmetic. `examples/surelog_bitwise.sv` exercises mixed 8/16/17/64-bit widths. Its opt-in `RtlBitwiseTests` integration test elaborates actual RTL and compares 144 cycles against Icarus for worker settings 1/4/8, with all scalar four-state pairs, known random inputs and X/Z random inputs. It uses the same `VELOSIM_REAL_FIFO=1` frontend-test switch and leaves evidence under `artifacts/rtl-bitwise-*`. Single-process serial execution remains the scope.

### Fifty-iteration operator expansion

The [50 missing-capability inventory](docs/verilog-missing-50.md) records the baseline gaps; the [iteration ledger](docs/verilog-50-iterations.md) separates implementation from qualification work. The expansion adds 24 unsigned operators: logical OR; logical/case inequalities and case equality; four relational comparisons; XNOR; unary minus/plus; left/right/unsigned arithmetic-right shifts; six reductions; multiplication/division/remainder; and conditional expressions. Unknown divisors or zero divisors yield X. Unknown conditional selectors merge equal branch bits, preserving identical Z bits. Signed operation trees, folded negative signed constants and negative integer parameters remain rejected.

`VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 --filter FullyQualifiedName~RtlOperatorTests` runs actual same-file Surelog/Icarus comparisons across eleven width pairs, with 201 vectors per pair and worker settings 1/4/8. Directed four-state pairs, known/four-state seeded phases, shift boundaries and a concat-of-shift sizing case are included. The tests independently assert the source interface before generating the reference testbench, strictly parse traces, verify unchanged source/build/tool fingerprints and retain reports under `artifacts/rtl-operators-*`. These tests execute one synchronous process serially. They do not establish general Verilog support or parallel speedup.

Both compilers receive the same generated design `.sv` path, recorded in each report. Surelog parses that file to UHDM; VeloSim structurally lowers UHDM and compiles C++. Icarus compiles the same design plus a generated stimulus testbench. `examples/surelog_fifo_operators.sv` is a runnable FIFO variant exercising the new operators through the NativeAOT CLI: use the earlier `fifo-differential` command with this source and `--top surelog_fifo_ops`. Its queue contract assumes binary controls, as in the original FIFO tests.

### Packed selections and concatenation expansion

The second [50-gap inventory](docs/verilog-missing-50-batch2.md) starts at `295ad4a`; its [iteration ledger](docs/verilog-50-iterations-batch2.md) records implementation and qualification separately. This batch targets packed bit reads, descending constant part reads, concatenations with multiple operands and constant replication. `examples/surelog_selections.sv` demonstrates those expressions in one synchronous process. The original inventory and operator results remain available above.

Packed declarations use canonical descending bounds `[W-1:0]`. Selection indices are unsigned; unknown or out-of-range bit indices return X. Part reads preserve X/Z data and produce X for positions outside the source word. Negative constant indices and ascending or nonzero declaration bounds remain outside this bounded subset. Indexed part selects and selected nonblocking targets are covered by the subsequent expansion below. Generated designs retain the 64-bit word limit.

Run `VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 --filter FullyQualifiedName~RtlSelectionTests` for the same-file Surelog/Icarus comparison and rejection tests. The test independently specifies every source port and output writer, strictly checks complete traces, and records source, build-input, tool and artifact hashes under `artifacts/rtl-selections-*`. Native string-oracle tests additionally check every source width from 1 through 64. `examples/surelog_fifo_selections.sv` preserves the eight-bit FIFO contract while reconstructing payloads with selected reads and concatenation; use the FIFO CLI command above with that file and `--top surelog_fifo_selections`.

### Indexed reads and selected nonblocking writes

The [new structural selection guide](docs/rtl-selected-writes.md) documents indexed `+:`/`-:` reads and nonblocking writes to packed bits, fixed slices and indexed slices. Updates merge into staged next-state words in source order while RHS expressions and indices read pre-edge state. Known bases for indexed reads and selected writes are bounded to `0..INT32_MAX`; larger known values fail explicitly. Selected targets must be directly writable declared words. See the [100-cycle ledger](docs/rtl-100-cycles.md) for implementation, independent review and same-file Icarus qualification, including the retained extreme-index reference discrepancy. The single-process, unsigned, 64-bit execution boundary remains.

Run `VELOSIM_REAL_FIFO=1 dotnet test tests/VeloSim.Tests -c Release -m:4 --filter FullyQualifiedName~RtlIndexed` with the pinned frontend/exporter installed to reproduce the actual-source checks.

### Concatenation nonblocking targets

Assignments such as `{high, low} <= data` and nested groups of supported packed targets are covered by the [concatenation target guide](docs/concat-targets.md). The RHS is evaluated once in the total target width and split MSB-first. Each leaf must name a distinct destination word, and total width is limited to 64 bits. See the [fresh 100-scenario ledger](docs/concat-target-100-cycles.md) for implementation, AI peer reviews and same-source qualification.

### External C++ hosts and VPI plugins

`rtl-export <design.rtl.json> --top <name> --output-dir <empty-directory>` exports a reusable `V<name>.h` model, four-state public ports, `eval()`/`final()` lifecycle, host time and a shared VPI subset runtime. External C plugins can look up signals, inspect/deposit four-state values and register callbacks against the same model state. See [external interfaces](docs/external-interfaces.md) for the complete build/run example, supported API and qualification scope. `examples/external_main.cpp` and `external_plugin.c` demonstrate both interfaces; this preserves the current bounded HDL subset.

## Standalone Windows testing

The [complete remote Windows guide](docs/remote-windows-campaign.md) includes
prerequisites, PowerShell download commands, short qualification,12-hour runs,
result locations and authorization recovery. The launcher commits small reports to
a fresh results branch and attempts a normal push; publication can resume without
rerunning tests. [PowerShell launcher source](tools/remote/Start-VeloSimCampaign.ps1).
