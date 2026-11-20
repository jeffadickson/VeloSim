# Structural RTL compilation handoff

Addresses [VeloFormal issue #2](https://github.com/jeffadickson/VeloFormal/issues/2).
Base inspected: VeloSim `dcb1ede2abfe2073a7b40aebc270892cf16f093c`.

## Contract and choice

`compile` remains the legacy normalized-UHDM-to-flat-IR workflow. It retains
`normalized.json` and produces `design.ir.json`. A structural exporter selected
as its normalizer now produces an actionable diagnostic directing callers to
`compile-rtl --exporter`.

The separate command avoids changing `CompileAsync`'s return schema for callers
such as compiled regressions and counter differential workflows:

```sh
dotnet run --project src/VeloSim.Tools -c Release -- compile-rtl \
  tests/fixtures/structural/velosim-bit-register.sv \
  --top velosim_bit_register --output-dir artifacts/bit-register \
  --surelog /absolute/path/to/surelog \
  --exporter /absolute/path/to/velosim-uhdm-rtl --threads 1
```

The output directory must be empty. Sources and tool paths containing a directory
separator resolve relative to the invoking directory, before changing the
subprocess working directory. Bare tool names use PATH. Defaults:
`VELOSIM_SURELOG` / `surelog`, and `VELOSIM_UHDM_RTL` /
`velosim-uhdm-rtl`. `VELOSIM_UHDM_NORMALIZER` is deliberately not used.
Duplicate, unknown, incomplete options and `--normalizer` are rejected.
`--json-errors compile-rtl ...` uses the existing structured CLI diagnostics.

Successful output:

- Retained Surelog UHDM under `surelog/`.
- `design.rtl.json`: exact exporter bytes with `format: velosim.rtl.v1`.
- `compile-rtl.json`: `velosim.compile-rtl.v1` provenance and completion marker.
  It records requested top, ordered source fingerprints, include scope, selected
  tool commands, thread count, relative artifact name and artifact SHA-256.
  No timestamp or output-directory path is included.

The pipeline validates a bounded (4 MiB, JSON depth 64) structural envelope,
required field types, known top-level fields and duplicate properties at every
depth. It permits the pinned fixture's `initialization` and `combinational`
fields without discarding them or invoking the narrower managed simulation
parser. This is a producer transport contract, **not** qualification of all
structural semantics for VeloSim simulation or VeloFormal. Unsupported source
constructs are rejected by the selected exporter; consumers must validate their
own supported subset. The exporter is trusted to honor the requested top: the
structural format itself does not contain a top-name field.

Source hashes cover explicit source files and recursive literal relative
includes, using the existing bounded source-closure helper with comment-aware
include discovery. Macro-based include operands are rejected. This is not a
general preprocessor dependency trace or a hash of installed tool binaries.
Hashes are checked again after export; detected changes reject publication.
Same unchanged inputs, tool commands, worker count and exporter bytes produce
identical manifests. Absolute source paths intentionally bind provenance to the
source checkout.

Exporter failure, malformed/missing/oversized output and changed source inputs
do not publish the final IR or completion marker. Intermediate UHDM/export
files can remain for diagnosis; use a fresh output directory for another run.
The manifest is fully written and closed under a temporary name before its
final rename, after the IR rename. Consumers should require the completion
marker, then verify the artifact hash.

## Regression fixtures

The bit-register source and direct structural output are copied unchanged from
VeloFormal's `tests/fixtures/velohdl/`, retrieved November 13, 2026:

- [source](https://github.com/jeffadickson/VeloFormal/blob/main/tests/fixtures/velohdl/velosim-bit-register.sv),
  Git blob `b727cbc15dc279ae5ccb88ccc4fe7ceed0790ab3`.
- [export](https://github.com/jeffadickson/VeloFormal/blob/main/tests/fixtures/velohdl/velosim-bit-register.rtl.json),
  Git blob `b7366eaf614fc28cd92072d06e982496e3df4554`.

The issue associates the direct export with VeloSim
`3a2953c4faaafab846a9b8ffd03b0935660b9054`. Transport tests inject this
recorded output at the process boundary; they do not claim to perform fresh
elaboration.

Run focused tests:

```sh
dotnet test tests/VeloSim.Tests -c Release -m:1 --filter FullyQualifiedName~StructuralCompileTests
```

Real-tool regression (retains evidence under `artifacts/structural-compile-*`):

```sh
VELOSIM_REAL_FIFO=1 VELOSIM_SURELOG=/absolute/path/to/surelog \
VELOSIM_UHDM_RTL=/absolute/path/to/velosim-uhdm-rtl \
dotnet test tests/VeloSim.Tests -c Release -m:1 \
  --filter FullyQualifiedName~ActualSurelogStructuralCompileIsDeterministicAndRejectsUnsupportedDelay
```

The real test compiles the bit register twice, checks structural schema,
artifact and manifest determinism and source hash, then requires rejection of
a delayed nonblocking assignment without final publication.

## Verification on November 13, 2026

Linux, .NET SDK 10.0.402, GCC 14.2.0; CMake/Ninja installed locally.
Single-process MSBuild with `DOTNET_EnableDiagnostics=0` avoids an initial
sandbox named-pipe restriction.

- Before implementation: nine new cases failed, including the exact legacy
  `Unexpected or duplicate field: memories` reproduction.
- Focused final regression: 15 passed, one real-tool test skipped by default.
  The comment-prefixed include regression was also observed failing before its
  provenance fix.
- Native Release build succeeded; CTest: 6/6 passed.
- CLI Release build succeeded with zero warnings/errors, including NativeAOT
  compatibility analysis. A native AOT executable was not published.
- Full managed suite: 294 passed, 132 skipped, six failed. Unchanged base
  checkout: 279 passed, 131 skipped, the same six failed. These failures rely on
  effective installed tool paths or binary fingerprints absent here:
  - `CounterWidthTests.WidthReplayBindsNegotiatedWidthToFingerprintOfCompiledIr`
  - `SchedulerHdlTests.SchedulerProvenanceHashesActualFixtureSourceLibraryAndToolCommands`
  - `SchedulerReplayTests.SchedulerReplayCliRunsValidatedFixtureAndWorkerMatrixInFreshDirectory`
  - `SchedulerReplayTests.StrictSchedulerReplayVerifiesArtifactHashesAndPinnedCompilerRuntimeRoles`
  - `WordReplayTests.StrictWordReplayBindsFixtureIrSourceArtifactHashAndToolRoles`
  - `WordReplayTests.WordReplayCliPreservesPinnedToolsFixtureAndAllNativeWorkers`
- Explicit real-tool test attempt failed at `Executable not found: surelog`.
  Configuring `VELOSIM_BUILD_UHDM=ON` also failed:
  `Could not find UHDM_INCLUDE_DIR ... uhdm/uhdm.h`. The pinned submodules are
  uninitialized and no matching installation is present. Therefore fresh
  Surelog/UHDM end-to-end success remains unverified; issue #2 should remain open
  pending execution on the equipped WSL2 toolchain.

No existing pull request was merged.
