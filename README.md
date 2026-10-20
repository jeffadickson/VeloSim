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
