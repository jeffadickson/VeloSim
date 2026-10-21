# VeloSim

Open-source experimental Verilog/SystemVerilog simulation project.

## Status

Early research prototype. C++20 kernels execute restricted four-state processes; compiled .NET10 C# helpers validate IR, orchestrate frontends and run regressions. The verified real Surelog/UHDM path supports a restricted synchronous-reset counter, not arbitrary SystemVerilog.

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
