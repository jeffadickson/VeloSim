# Standalone schematic and live lesson checkpoint — 2026-11-14

This checkpoint implements the bounded RC/divider book mode. It does not close
all textbook model families, general WALLY rendering, shared visible-session MCP
control, Linux runtime qualification or the full VeloSim Windows test baseline.
The SCH-BOOK requirements remain Draft with the evidence and limits below.

## Executed checks

Raw artifacts are under `E:/Artifacts/VeloSim-net11-schematic-20261112`.
The pre-commit source base is `bf67c78`; publication truthfully records a dirty
working tree, individual application source hashes, root SDK/build input hashes
and caller-declared native solver provenance. The final evidence JSON records
the package and source hashes without pretending this was a clean pinned build.

* Released SDK 11.0.100: self-contained Windows web/MCP hosts and Blazor browser
  publication. This is apphost/JIT publication, not a NativeAOT qualification.
  The optional wasm-tools optimization workload was unavailable; publication
  completed using the supported unoptimized WASM path.
  The complete root `VeloSim.slnx` build also succeeded with zero warnings/errors;
  build success does not erase the broader test failures below.
* `dotnet test apps/SchematicLab/Tests/SchematicLab.Tests.csproj` with explicit
  E: artifacts/results, `--collect:'XPlat Code Coverage'`, bounded MSBuild and
  shared compilation disabled: **72 passed, zero failed/skipped** after review.
  The actual TRX is `book-final-reviewed-tests/book.trx`.
* Coverlet scope (Core, Solver and WebHost code reached by this suite):
  **4450/5597 lines; 1524/2494 branches**. This is not browser rendering, MCP
  protocol, Rust numerical, whole-VeloSim or all model-family coverage.
* FsCheck: 128 arbitrary startup vectors, replay `110011,2026111201`; 128 arbitrary
  locked move coordinates, replay `111413,2026111401`. Malformed bootstrap,
  stale edits, rejected replacement, F/H rebinding, parameter undo/redo and
  malformed unrelated drafts have explicit regression checks.
* Pester 5.7.1: **18 passed, zero failed/skipped**, including changed/missing/
  unrecorded bytes, path traversal, manifest identity and required solver
  provenance. Its two-script command coverage is **32.45% (86/265 commands)**;
  publication success was executed separately, outside this instrumented pass.
  Pester command coverage is not line/branch coverage. No whole-application
  Stryker.NET or long fuzz campaign was run for this increment.
* One deliberate structural-undo bypass mutation was detected by its regression
  (one failed test), source restored and six policy tests passed afterward.
  This was the earlier policy snapshot recorded in `lock-mutant.json`, not a
  broad mutation score for the final assembly. Review findings separately got
  red/green regressions for valid F/H rebind and unrelated malformed drafts.
* Real StrictDoc 0.30.4 reader parsed **13 documents**. The full export/cache API
  initially hit an existing missing `html2pdf4doc` dependency; the actual
  `SDReader.read_with_parse_context` path parsed the files without mocking it.
  HTML documentation export was not qualified by this parsing check.
* The published Windows package was launched with a working directory outside
  the checkout, author-locked RC deployment and the included native executable.
  Browser automation embedded it in an iframe, verified a refused reset,
  parameter change with preserved geometry, and a real probe/waveform.
  MCP verified discovery, immutable author lock, value change, stale refusal and
  native RC endpoint agreement with the analytical exponential (0.02 V bound).
  Final package replay paths/counts are in the evidence JSON.
* The existing editor browser smoke passed geometry/history, crossing/junction
  round trip, divider edit/probe/sweep, stale refusal, delete/place/reconnect and
  reset. Its test server supplies an explicit deployment bootstrap. The first
  attempt failed during WASM fetch with `ERR_NETWORK_CHANGED`; the unmodified
  retry passed. Both logs are retained rather than relabeling the first passing.

## Independent numerical and source provenance

The bundled Rust executable SHA-256 is
`c3caf38a08fa03c4cb375408d96ddbcf984fe4e1ef19f34b2e3c4fedce46888b`.
Its declared Spice source checkpoint is
`1aa258e40749f842d913b7a0242c4b7775ffe606`. That executable was built from the
equivalent pre-commit working source; the declaration is not an independently
reproduced clean-build attestation. The original numerical checkpoint contains
ngspice evidence for its own bounded slices. This schematic deployment test
used the independent RC analytical reference, not a new whole-corpus ngspice run.
Optional BSIM model providers were not bundled or exercised here.

## Broader migration limits and retained failures

The VeloSim root test run on Windows/exFAT produced **263 passed, 15 failed,
141 skipped, 419 total**, retained in `root-relocated-tests/velosim.trx`.
Failures include Linux tool-path assumptions, fake executable naming, source
working-directory assumptions in SDK export, replay tool provenance and exFAT
link fixtures. They remain unresolved; do not call the whole .NET 11 repository
passing. Relocated fixture lookup now uses a validated source checkout instead
of walking parents of the E: test output, and its three new tests passed.
No source-to-native Linux RTL, macOS, full compiler toolchain, NativeAOT runtime
or remote CI success is claimed. Existing CI configuration was preserved.

The UI still displays some structural tools in a locked lesson; commands refuse
them explicitly. Compact presentation styling and actual WALLY render acceptance
remain open. Browser and stdio MCP sessions remain independent. The iframe test
is a browser integration test, not proof of WALLY or a security sandbox.

## Review disposition

A fresh simulated code-review perspective inspected the whole working branch.
F/H rebind bypass and stale publication/static-host instructions were fixed.
The initial fix coupled the control check to unrelated numeric drafts; its
regression was reproduced and fixed by independent `ParseControls()` reuse.
The reviewer rechecked those source fixes without running tests. Runtime and
test outcomes above were executed by the primary agent, not an external expert.
