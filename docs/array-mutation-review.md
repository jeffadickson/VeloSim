# Array mutation follow-up

This review investigates the nine survivors and four uncovered mutations in the
earlier Stryker.NET 5.0.0 worker report. No production simulator defect was
reproduced. The changes add regression tests and expand the existing narrow
mutation test selection; production code is unchanged.

Historical mutation IDs below identify that report only; they are not stable
across source revisions or configurations.

| Historical IDs | Missing check | Regression |
| --- | --- | --- |
| 5256 | Useful error for a fixed array's dynamic-lifetime query | `FixedStorageRejectsLifetimeQueryWithUsefulDiagnostic` |
| 5262 | Unknown fields on an array access | `AccessSchemaRejectsUnknownFieldsBeforeEmission` |
| 5271, 5278 | Scalar versus coordinate form and useful shape error | `ScalarAndCoordinateFormsCannotBeInterchanged` |
| 5284 | Useful coordinate-rank error | `CoordinateRankErrorExplainsTheRejectedAccess` |
| 5291–5293 | Coordinate expressions, separators and declared bounds in emitted C++ | `CoordinateEmissionRetainsEveryValueAndDeclaredBound` |
| 5294–5297 | Zero and nonzero legacy lower bounds in both emitters | `LegacyLowerBoundIsAppliedOnlyWhenNonzero` |
| 5286 | Index recursion depth increment changed to decrement | See parser boundary below |

Generated-source assertions use independently specified input coordinates and
bounds, exercising standalone C++ and SDK emitters. They detect malformed
emission; this follow-up does not rerun generated C++ compilation or HDL
reference comparisons. Earlier HDL evidence remains historical. Diagnostic tests
check meaningful message fragments rather than entire punctuated strings.

## Parser boundary and retained survivor

`RtlDesign.Parse` uses `JsonDocument.Parse` with its default maximum JSON depth of
64. Index expressions consume object/array nesting at the public JSON boundary.
The selected mutation at `ValidateIndex(index, depth + 1)` changes only the
independent validation-depth argument. Source inspection indicates that the
parser limit masks its effect for currently accepted JSON; this is a contextual
equivalence assessment, not a formal proof or a killed mutation.

`ExcessiveIndexNestingFailsAtThePublicJsonBoundary` independently checks rejection
of over-nested public input with an `IrValidationException`. It does not use
reflection to bypass parsing or pretend to detect the masked mutation. Revisit
the survivor if parser options or validation entry points change. The mutation
is retained in the reported score and is not suppressed.

## Reproduction

The accepted run passed 31/31 focused NUnit tests, including 12 new cases.
The broader remote-baseline selection passed 72/72 tests; these overlap the
focused run and must not be summed as unique tests. Pester passed 17/17 tests,
and the changed Linux worker script passed a PowerShell parser check. The full
Windows launcher and long mutation campaign were not rerun.

Coverlet measured `RtlArrays.cs` at 22/22 lines (100%) and 61/62 branches (98.38%)
in the focused run. This is a single-file scope. Stryker reported 60 killed,
one surviving and zero uncovered mutations: 98.36%, compared with the historical
48 killed, nine surviving and four uncovered mutations on the same production
source. All twelve actionable cases in the table are now killed; ID 5286 remains
visible as the parser-masked survivor. No mutation exclusions were added.

Stryker excluded 1,540 compilation/instrumentation errors and 10,829 ignored
mutants across the analyzed Core project. Those are not kills, and the score
does not cover untestable mutations. Its safe-mode compilation handling includes
some `ValidateMemoryShape` mutations. The tool also reported target-framework
detection fallback to net10.0. Production builds and baseline tests passed.

[Machine-readable evidence](array-mutation-qualification.json) binds the source
hashes, report hashes, seeds and commands. Raw reports remain in ignored
`artifacts/array-mutation-followup` and the native Linux home workspace described
in `prompt.md`. Historical qualification records retain their original hashes;
they are not relabeled as current-source results.

Run in a Linux/WSL checkout with the repository's pinned .NET SDK. Use fresh
results directories and serialize jobs sharing instrumented build outputs.

```powershell
$ErrorActionPreference = 'Stop'
$repo = $PWD.Path
if (-not (Test-Path -LiteralPath $repo -PathType Container)) { throw 'Checkout missing' }
if (-not (Test-Path -LiteralPath (Join-Path $repo 'global.json') -PathType Leaf)) { throw 'Run from the checkout root' }
dotnet tool restore
if ($LASTEXITCODE) { throw 'Tool restore failed' }
dotnet test tests/VeloSim.Tests -c Release --filter '(FullyQualifiedName~RtlDynamicArrayTests&FullyQualifiedName!~AllocationResizeAccessAndDeleteMatchOriginalHdl)|FullyQualifiedName~MalformedShapeProperty|FullyQualifiedName~RtlArrayMutationRegressionTests' --collect:'XPlat Code Coverage'
if ($LASTEXITCODE) { throw 'Baseline tests failed' }
$tests = Join-Path $repo 'tests/VeloSim.Tests'
if (-not (Test-Path -LiteralPath $tests -PathType Container)) { throw 'Tests missing' }
Push-Location -LiteralPath $tests
try {
    dotnet tool run dotnet-stryker --config-file (Join-Path $repo 'verification/stryker-dynamic.json')
    if ($LASTEXITCODE) { throw 'Mutation run failed' }
} finally { Pop-Location }
```

The existing seeded FsCheck malformed-shape and dynamic-depth properties are
included. No additional input-fuzz campaign was run for this test-only change.
Coverage and mutation results apply to `RtlArrays.cs`, not the complete
simulator, native runtime, helpers or upstream libraries.
