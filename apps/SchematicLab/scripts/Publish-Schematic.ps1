#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('win-x64','linux-x64')][string]$Runtime = $(if($IsWindows){'win-x64'}else{'linux-x64'}),
    [string]$SolverPath = '',
    [string]$SolverSourceCommit = '',
    [switch]$EditorOnly
)
# Publish an independent local web application; do not invoke the HDL build graph.
$ErrorActionPreference='Stop'
$app=Split-Path -Parent $PSScriptRoot
foreach($p in @($app,(Split-Path -Parent ([IO.Path]::GetFullPath($OutputDirectory))))){
    if(!(Test-Path -LiteralPath $p -PathType Container)){throw "Missing directory: $p"}
}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'A fresh output directory is required; existing results are preserved.'}
# Refuse before creating outputs or invoking a compiler: a normal distribution
# promises in-place native simulation, not merely an editor with an empty bridge.
if(!$EditorOnly -and !$SolverPath){throw 'A simulation package requires SolverPath.'}
if($EditorOnly -and ($SolverPath -or $SolverSourceCommit)){throw 'EditorOnly cannot include solver provenance.'}
if($IsWindows){
    $artifactRoot=if($env:CODEX_ARTIFACT_ROOT){$env:CODEX_ARTIFACT_ROOT}else{'E:/Artifacts'}
    if(!(Test-Path -LiteralPath $artifactRoot -PathType Container)){throw 'Missing artifact root'}
    $prefix=[IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(!$out.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Windows output must be below the configured artifact root.'}
}
if($SolverPath){
    if(![IO.Path]::IsPathFullyQualified($SolverPath) -or !(Test-Path -LiteralPath $SolverPath -PathType Leaf)){throw 'SolverPath must select an existing absolute trusted executable.'}
    if($SolverSourceCommit -notmatch '^[0-9a-f]{40}$'){throw 'An included solver needs its declared 40-character source commit.'}
    $magic=[byte[]]::new(4);$file=[IO.File]::OpenRead($SolverPath)
    try{if($file.Read($magic,0,4) -ne 4){throw 'Solver executable is incomplete'}}finally{$file.Dispose()}
    if(($Runtime -eq 'win-x64' -and ($magic[0] -ne 0x4d -or $magic[1] -ne 0x5a)) -or
       ($Runtime -eq 'linux-x64' -and [Convert]::ToHexString($magic) -ne '7F454C46')){throw 'Solver executable format does not match the selected runtime.'}
}
$original=Get-Location
$savedTemp=$env:TEMP;$savedTmp=$env:TMP
try{
    Set-Location -LiteralPath $app
    $sdk=(& dotnet --version).Trim()
    if($LASTEXITCODE -or $sdk -notmatch '^11\.\d+\.\d+$'){throw 'Use a released .NET 11 SDK selected by global.json.'}
    New-Item -ItemType Directory -Path $out|Out-Null
    $env:TEMP=$out;$env:TMP=$out
    $build=Join-Path $out build;$bundle=Join-Path $out bundle;$web=Join-Path $out web
    dotnet publish Web/SchematicLab.Web.csproj -c Release --artifacts-path $build -o $web -p:UseSharedCompilation=false --disable-build-servers -m:1
    if($LASTEXITCODE){throw 'Browser publication failed'}
    dotnet publish WebHost/SchematicLab.WebHost.csproj -c Release -r $Runtime --self-contained true --artifacts-path $build -o $bundle -p:UseSharedCompilation=false --disable-build-servers -m:1
    if($LASTEXITCODE){throw 'Standalone server publication failed'}
    dotnet publish Mcp/SchematicLab.Mcp.csproj -c Release -r $Runtime --self-contained true --artifacts-path $build -o (Join-Path $bundle mcp) -p:UseSharedCompilation=false --disable-build-servers -m:1
    if($LASTEXITCODE){throw 'Standalone MCP publication failed'}
    $assets=Join-Path $web wwwroot
    if(!(Test-Path -LiteralPath $assets -PathType Container) -or !(Test-Path -LiteralPath $bundle -PathType Container)){throw 'Published outputs missing'}
    Copy-Item -LiteralPath $assets -Destination (Join-Path $bundle wwwroot) -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot Launch-Schematic.ps1) -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot Test-SchematicBundle.ps1) -Destination $bundle
    $solver=$null
    if($SolverPath){
        $directory=Join-Path $bundle solver
        if(!(Test-Path -LiteralPath $directory -PathType Container)){New-Item -ItemType Directory -Path $directory|Out-Null}
        $name=if($Runtime -eq 'win-x64'){'circuit-simulator.exe'}else{'circuit-simulator'}
        Copy-Item -LiteralPath $SolverPath -Destination (Join-Path $directory $name)
        $solver=@{file="solver/$name";sha256=(Get-FileHash -LiteralPath $SolverPath).Hash.ToLowerInvariant();declared_source_commit=$SolverSourceCommit;source_claim='Caller-declared revision; executable bytes verified by SHA-256. Optional third-party providers are not included.'}
    }
    @'
SchematicLab standalone local browser application
Run Launch-Schematic.ps1 with PowerShell 7, or run SchematicLab.WebHost directly.
Open http://localhost:5087/ in a browser. --port selects another local port.
The self-contained server and MCP host do not require an SDK or installed runtime.
Without an included/configured solver, editing and the analytical divider work;
native analyses report unavailable. Providers not in the package remain unavailable.
MCP clients can launch mcp/SchematicLab.Mcp directly using its stdio transport.
Linux: if ZIP extraction loses executable permissions, chmod +x the two apphosts,
createdump and any solver executable before running. Verify the manifest first.
This is a browser application with a local server, not a native desktop GUI.
'@ | Set-Content -LiteralPath (Join-Path $bundle README.txt)
    $files=@(Get-ChildItem -LiteralPath $bundle -Recurse -File|Sort-Object FullName|ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($bundle,$_.FullName).Replace([IO.Path]::DirectorySeparatorChar,[char]'/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length}
    })
    $revision=(& git rev-parse HEAD).Trim();if($LASTEXITCODE){throw 'Source revision unavailable'}
    $dirty=@(& git status --porcelain).Count -gt 0
    $sourceFiles=@(& git ls-files --cached --others --exclude-standard -- .)
    if($LASTEXITCODE){throw 'Source inventory unavailable'}
    $sourceHashes=@($sourceFiles|Sort-Object -Unique|ForEach-Object {@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $app $_)).Hash.ToLowerInvariant()}})
    $repository=(& git rev-parse --show-toplevel).Trim();if($LASTEXITCODE){throw 'Repository root unavailable'}
    $buildInputs=@('global.json','Directory.Build.props'|ForEach-Object {
        $inputPath=Join-Path $repository $_
        if(!(Test-Path -LiteralPath $inputPath -PathType Leaf)){throw "Build input missing: $_"}
        @{path=$_;sha256=(Get-FileHash -LiteralPath $inputPath).Hash.ToLowerInvariant()}
    })
    @{schema='velosim.schematic.bundle.v1';package_kind=$(if($EditorOnly){'editor_only'}else{'simulation'});runtime=$Runtime;framework='net11.0';sdk=$sdk;self_contained=$true;source_commit=$revision;source_dirty=$dirty;source_files=$sourceHashes;source_build_inputs=$buildInputs;files=$files;solver=$solver}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $bundle schematic-bundle.json)
    & (Join-Path $bundle Test-SchematicBundle.ps1) -Directory $bundle
    if($LASTEXITCODE){throw 'Bundle verification failed'}
    $archive=Join-Path $out ("SchematicLab-$Runtime.zip")
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
    @{archive=$archive;sha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant();bundle=$bundle}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out package.json)
    Write-Output $bundle
}finally{Set-Location -LiteralPath $original.Path;$env:TEMP=$savedTemp;$env:TMP=$savedTmp}
