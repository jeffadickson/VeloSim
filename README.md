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

The prototype supports one sheet, 19 generic symbols, terminal-to-terminal wiring and an analytical unloaded voltage divider. It keeps edits in browser memory. The native RC charging lesson is available with the loopback solver host. Project files, undo/redo, hierarchy, symbol import and general-purpose wire routing are later work.

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