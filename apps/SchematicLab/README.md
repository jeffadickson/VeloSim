# SchematicLab

The canonical source now lives in `apps/SchematicLab` in
[VeloSim](https://github.com/jeffadickson/VeloSim). Its original history is retained.
Use released .NET SDK 11.0.100. `SchematicLab.slnx` builds this application without
the HDL projects; the root VeloSim solution also includes it.

## Standalone application

The standalone distribution is a self-contained local server plus browser UI
and stdio MCP host. The installed application needs neither the source checkout
nor an installed .NET SDK/runtime. It remains a browser application, rather than
a native desktop GUI. Building the distribution requires .NET 11 and PowerShell 7.

```powershell
if (!(Test-Path -LiteralPath 'scripts' -PathType Container)) { throw 'Run from apps/SchematicLab' }
$artifactRoot = if ($env:CODEX_ARTIFACT_ROOT) { $env:CODEX_ARTIFACT_ROOT } else { 'E:/Artifacts' }
if (!(Test-Path -LiteralPath $artifactRoot -PathType Container)) { throw 'Missing artifact root' }
$out = Join-Path $artifactRoot ("SchematicLab-" + [guid]::NewGuid().ToString('N'))
# The publisher requires a fresh destination and creates it itself.
$solver = $env:SPICE_SOLVER_PATH
$solverCommit = $env:SPICE_SOLVER_SOURCE_COMMIT
if (!$solver -or ![IO.Path]::IsPathFullyQualified($solver) -or !(Test-Path -LiteralPath $solver -PathType Leaf)) { throw 'Set SPICE_SOLVER_PATH to a trusted native solver' }
if ($solverCommit -notmatch '^[0-9a-f]{40}$') { throw 'Set SPICE_SOLVER_SOURCE_COMMIT to its declared source revision' }
./scripts/Publish-Schematic.ps1 -OutputDirectory $out -Runtime win-x64 -SolverPath $solver -SolverSourceCommit $solverCommit
if (!(Test-Path -LiteralPath (Join-Path $out 'bundle') -PathType Container)) { throw 'Publication failed' }
& (Join-Path $out 'bundle/Launch-Schematic.ps1') -Port 5087
```

Open `http://localhost:5087/`. The default browser assets resolve from the
installed directory, irrespective of the current working directory. The launcher
verifies the recorded file hashes. The native apphost can also run directly:
`SchematicLab.WebHost --port 5087`, or use `--help` for explicit path options.
Listeners are localhost-only. Unknown/duplicate arguments or invalid explicit
paths return a structured startup error before opening a listener.

Native analyses use the bundled executable or an absolute `SPICE_SOLVER_PATH` or `--solver` override.
The standard publisher requires an explicitly selected trusted executable with
`-SolverPath` and its actual `-SolverSourceCommit`; the manifest records its
byte hash and caller-declared source provenance. It does not include optional
third-party model providers. Explicit `-EditorOnly` publication omits the solver;
editing and the analytical
divider remain available and native analyses return an explicit unavailable
result. The bundled MCP host also finds an included solver without environment
configuration. Its stdio entry point is `mcp/SchematicLab.Mcp` (with `.exe` on Windows).

Select `linux-x64` for a Linux bundle. Cross-publication does not establish Linux
execution coverage. ZIP extraction may require restoring executable permissions
as described in the bundle README. Retain an appropriate native Linux filesystem
for toolchains that require its semantics; do not assume exFAT is equivalent.

Verify the application with `scripts/Test-Schematic.ps1 -OutputDirectory <fresh
artifact child>`. It runs the complete unit/property suite and validates the TRX
report, including the required stale-edit and seeded property cases. An adapter
exit code alone is insufficient if the report contains an unhandled exception.

SchematicLab is a standalone .NET 11 application for embedding an interactive circuit in an electronics lesson. The default example opens with a 5 V supply and two 1 kΩ resistors. Select R2, enter 3000 Ω, and apply the value to see the midpoint rise from 2.5 V to 3.75 V. Select two terminals to wire them. Place parts using the marked points, delete the selected part from the inspector, or reset the example. Select **Run DC sweep** to plot the measured probe voltage over 21 input voltages.

## Build and embed

Publish and launch the standalone application above. To start the author-locked
RC profile, set these values before launching its WebHost (and the MCP process):

```powershell
$env:SCHEMATIC_LAYOUT_MODE = 'author-locked'
$env:SCHEMATIC_LESSON = 'rc'
```

Serve with WebHost, which supplies the required `/api/lesson-deployment` bootstrap
and native simulation endpoints. A plain static file server is insufficient.
The circuit document lives in the browser; native numerical analysis runs in
the local host. The article can embed that host in an iframe. Subdirectory
deployment requires matching base-path and API routing and is not qualified yet.

```html
<iframe title="Interactive RC lesson" src="http://localhost:5087/"
        style="width:100%;height:670px;border:0" loading="lazy"></iframe>
```

## Boundaries

This prototype includes the component/source drawing catalog, named nets,
editable wire routes and explicit junctions, save/reload with view and analysis
history, bounded two-port child sheets and named model definitions. Drawing
availability is broader than solver execution: readiness and unsupported-model
results remain explicit. Native RC, RLC, controlled-source and selected
semiconductor analyses use the separately qualified solver bridge. Select parts
to move, rotate, mirror or duplicate; undo/redo and circuit checks remain shared
typed commands. The versioned requirements and verification records below define
the actual limits; this is not full device-library or HDL/analog compatibility.

See [requirements](docs/requirements.sdoc), [design notes](docs/design.md), [catalog](docs/catalog.md), and [RSpice comparison](docs/rspice-requirements-scrub.md).


## Local agent access

Build the MCP host with the .NET 11 SDK and use its DLL as the argument to the
MCP client's `dotnet` command. This starts one local stdio session with the
built-in divider. Read the circuit before issuing edits so you have its
document ID and current revision.

```powershell
if (!(Test-Path -LiteralPath 'Mcp' -PathType Container)) { throw 'Run from SchematicLab' }
$artifactRoot = if ($env:CODEX_ARTIFACT_ROOT) { $env:CODEX_ARTIFACT_ROOT } else { 'E:/Artifacts' }
if (!(Test-Path -LiteralPath $artifactRoot -PathType Container)) { throw 'Missing artifact root' }
$out = Join-Path $artifactRoot ("SchematicLab-Mcp-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
dotnet build Mcp --artifacts-path (Join-Path $out 'build')
if ($LASTEXITCODE) { throw 'MCP build failed' }
$hostDll = Join-Path $out 'build/bin/SchematicLab.Mcp/debug/SchematicLab.Mcp.dll'
dotnet run --artifacts-path (Join-Path $out 'smoke') --file scripts/McpSmoke.cs -- $hostDll
```

Use the [requirements index](docs/requirements.md) for the complete product
scope. The [command verification](docs/command-verification.md) records the
browser and MCP results.

## Run the native RC lesson

Publish the standalone simulation package above. Both hosts find its included
Rust executable. Use `SPICE_SOLVER_PATH` only for an explicit trusted override.

```powershell
if (!$out) { throw 'Set out to the published package directory from the publication example' }
$bundle = Join-Path $out 'bundle'
if (!(Test-Path -LiteralPath $bundle -PathType Container)) { throw 'Missing published application' }
& (Join-Path $bundle 'Launch-Schematic.ps1') -Port 5087
```

Open http://127.0.0.1:5087/, choose **Open RC example**, select **P1**, and run
the transient. In MCP, inspect the circuit, call `schematic_load_rc` at the
current revision, then call `schematic_run_transient` with the new revision and
probe ID `P1`. Use `schematic_read_run` to fetch the retained waveform.
## Wiring and saved sheets

In **Wiring tools**, select a terminal and enter a net name. Labels with the
same name, ignoring case, connect their terminals. Clear the name to remove a
label. To edit a route, select its wire and enter a space-separated list of
sheet coordinates such as `250,145 300,145`. To add a junction, give both
wires the same bend, select both wires, enter that one point and choose **Join
wires at point**. Crossings remain separate until joined.

**Save sheet** stores one sheet for the browser origin. **Reload sheet**
replaces the active sheet and can be undone. In MCP, use
`schematic_save_document` to get the same JSON and `schematic_load_document`
to load it at the expected revision. The client controls where the JSON is
stored. The file contains the circuit, probe connections, selected part, catalog
visibility and filter, and net highlight. It retains the last 16 completed
runs of each analysis type with their original IDs, revisions and samples.
Version 1 sheets open with an empty view and analysis history. Pending edits
and the undo stack are local to the session.

Choose a terminal and select **Highlight net** to mark its connected
terminals, wires, junctions and labels. **Analysis history** lets you view
retained runs for the displayed lesson. Reload initially shows its latest
run. MCP exposes the same state via `schematic_highlight_net`,
`schematic_read_view`, `schematic_read_analysis_history` and
`schematic_read_run`.

## General circuit analysis

Run the loopback solver host as described above. Open **Mixed RLC example**
or build a supported circuit, then choose **Analyze Dc**, **Analyze Ac** or
**Analyze Transient** in **Circuit analysis**. DC reports an operating point.
AC takes ascending frequency points in hertz and source magnitude and phase
in degrees. Transient takes stop and step sizes in seconds and optional
independent voltage/current pulses. Use spaces within a source row and
semicolons between rows. F and H require a voltage-source ID whose current
is measured from its positive to its negative pin.

**Open semiconductor example** loads a diode at P1 and an NMOS level 1
channel at P2. The model rows take effective SI parameters. MOS1 beta
includes width divided by length, and threshold voltage is signed.
General analysis settings and results are scoped to the current browser
panel or MCP response. The legacy RC history remains available.

MCP exposes the same run command as `schematic_run_circuit`, with a
revision and typed `options` object. Example tools are
`schematic_load_bridge_demo` and `schematic_load_model_demo`.
See [bridge verification](docs/bridge-verification.md) for ngspice cases.

The analysis panel accepts inline PWL, damped sine and exponential rows.
Model and waveform entry fields travel with the saved sheet. MCP can
inspect and transfer those fields with schematic_read_analysis_fields
and schematic_apply_analysis_fields. Set DC source stepping to true
for the bounded opt-in continuation policy. The pinned FD-SOI example
requires SPICE_BSIM_SOI_FD_HOST and supports DC operating point only.

## Live book and presentation lessons

See [live lessons](docs/live-lessons.md) for the author-controlled layout lock,
standalone solver packaging, the bounded textbook-readiness matrix and WALLY
iframe deployment. Browser and stdio MCP use the same commands and policy but
currently retain independent document sessions.

## Linear solver selection

Choose **Linear solver** in the analysis panel. The same selection applies to
the RC probe run and general circuit runs; it is saved with the sheet and uses
the normal stale-revision and parameter-only undo rules. LU remains the legacy
default. The legacy analytical divider sweep does not call a linear provider.
LU and QR are dense reference adapters in the same numerical library;
Faer is the CPU sparse adapter. **QR supports DC/transient but has no complex
AC implementation**. AC with QR refuses explicitly; it does not fall back to LU.

MCP exposes `schematic_read_solver_backends` (including known capabilities) and
`schematic_set_solver_backend` with `documentId`, `revision` and `backend`
(`Lu`, `Qr` or `Faer`). General `BridgeOptions.Backend` may explicitly override
the session choice for one run; null/omitted inherits it. Results retain the
effective choice, and the native adapter verifies the response provider ID.
See [backend verification](../../docs/linear-backend-selection.md) for the exact
analytical cases and limits. This does not add arbitrary C++/GPU providers.
