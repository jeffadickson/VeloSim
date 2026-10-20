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
