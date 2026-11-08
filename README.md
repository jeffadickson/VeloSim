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

The prototype supports one sheet, 18 generic symbols, terminal-to-terminal wiring and an analytical unloaded voltage divider. It keeps edits in browser memory. Simulator integration, project files, undo/redo, hierarchy, symbol import and general-purpose wire routing are later work.

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