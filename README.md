# SchematicLab

SchematicLab is a standalone .NET 10 prototype for embedding an interactive circuit in an electronics lesson. The example opens with a 5 V supply and two 1 kΩ resistors. Select R2, enter 3000 Ω, and apply the value to see the midpoint rise from 2.5 V to 3.75 V. Select two terminals to wire them. Place parts using the marked points, delete the selected part from the inspector, or reset the example. Select **Run DC sweep** to plot the measured probe voltage over 21 input voltages.

## Build and embed

From the SchematicLab source directory, use a new output directory on the artifact drive.

```powershell
$artifactRoot = if ($env:CODEX_ARTIFACT_ROOT) { $env:CODEX_ARTIFACT_ROOT } else { 'E:/Artifacts' }
if (!(Test-Path -LiteralPath $artifactRoot -PathType Container)) { throw 'Missing artifact root' }
if (!(Test-Path -LiteralPath 'Web' -PathType Container)) { throw 'Run from the SchematicLab directory' }
$out = Join-Path $artifactRoot ("SchematicLab-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
dotnet publish Web -c Release --artifacts-path (Join-Path $out 'build') -o (Join-Path $out 'publish')
```

Serve the published `wwwroot` over HTTP. The template uses `<base href="/">` for a host root. For a subdirectory deployment, set the base path to that directory with a trailing slash before publishing. The article can embed the hosted app with an iframe. The app processes the circuit locally in the browser.

```html
<iframe title="Interactive voltage-divider lesson" src="/schematic-lab/"
        style="width:100%;height:670px;border:0" loading="lazy"></iframe>
```

## Boundaries

The prototype supports one sheet, 19 generic symbols, terminal-to-terminal wiring and an analytical unloaded voltage divider. It keeps edits in browser memory. Select a part to move, rotate, mirror or duplicate it. Undo and redo restore earlier edits, and Check circuit identifies the terminals that need attention. The native RC charging lesson is available with the loopback solver host. Project files, hierarchy, symbol import and general-purpose wire routing are later work.

See [requirements](docs/requirements.sdoc), [design notes](docs/design.md), [catalog](docs/catalog.md), and [RSpice comparison](docs/rspice-requirements-scrub.md).


## Local agent access

Build the MCP host with the .NET 10 SDK and use its DLL as the argument to the
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

Publish the browser as described above. Set `SCHEMATIC_WEB_ROOT` to the published
`wwwroot` directory and `SPICE_SOLVER_PATH` to the absolute path of the trusted
Rust circuit-simulator executable. Run the loopback host from the SchematicLab
directory. The same executable path enables the RC tools in the stdio MCP host.

```powershell
if (!(Test-Path -LiteralPath 'WebHost' -PathType Container)) { throw 'Run from SchematicLab' }
if (!(Test-Path -LiteralPath $env:SCHEMATIC_WEB_ROOT -PathType Container)) { throw 'Missing published browser' }
if (!(Test-Path -LiteralPath $env:SPICE_SOLVER_PATH -PathType Leaf)) { throw 'Missing solver' }
dotnet run --project WebHost --artifacts-path (Join-Path $out 'host')
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
