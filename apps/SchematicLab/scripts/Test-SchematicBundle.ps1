#requires -Version 7.4
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
if(!(Test-Path -LiteralPath $Directory -PathType Container)){throw 'Bundle directory missing'}
$root=(Resolve-Path -LiteralPath $Directory).Path
$manifest=Join-Path $root schematic-bundle.json
if(!(Test-Path -LiteralPath $manifest -PathType Leaf)){throw 'Bundle manifest missing'}
if((Get-Item -LiteralPath $manifest).Length -gt 16MB){throw 'Bundle manifest exceeds its size budget'}
$data=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
if($data.schema -ne 'velosim.schematic.bundle.v1' -or $data.framework -ne 'net11.0' -or $data.self_contained -isnot [bool] -or !$data.self_contained -or $data.runtime -notin @('win-x64','linux-x64')){throw 'Unsupported bundle contract'}
if(!$data.files -or @($data.files).Count -gt 20000){throw 'Bundle file-count budget exceeded'}
if($data.package_kind -notin @('simulation','editor_only')){throw 'Unsupported package kind'}
$prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
$comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
$seen=[Collections.Generic.HashSet[string]]::new($(if($IsWindows){[StringComparer]::OrdinalIgnoreCase}else{[StringComparer]::Ordinal}))
foreach($item in $data.files){
    if(!$item.path -or $item.path.Contains([char]'\') -or $item.path.Contains(':') -or $item.path -match '(^|/)\.{1,2}(/|$)' -or [IO.Path]::IsPathRooted($item.path) -or !$seen.Add([string]$item.path)){throw 'Invalid or duplicate bundle entry'}
    $cursor=$root
    foreach($part in $item.path.Split('/')){
        if(!$part){throw 'Empty bundle path component'}
        $cursor=Join-Path $cursor $part
        if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked bundle paths are unsupported'}}
    }
    $path=[IO.Path]::GetFullPath((Join-Path $root $item.path))
    if(!$path.StartsWith($prefix,$comparison) -or !(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing or escaping bundle entry: $($item.path)"}
    if((Get-Item -LiteralPath $path).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $item.sha256){throw "Bundle hash mismatch: $($item.path)"}
}
foreach($file in Get-ChildItem -LiteralPath $root -Recurse -Force){
    if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked bundle paths are unsupported'}
    if(!$file.PSIsContainer){
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace([IO.Path]::DirectorySeparatorChar,[char]'/')
        if($relative -ne 'schematic-bundle.json' -and !$seen.Contains($relative)){throw "Unrecorded bundle file: $relative"}
    }
}
$suffix=if($data.runtime -eq 'win-x64'){'.exe'}else{''}
if($data.package_kind -eq 'simulation'){
    $expectedSolver="solver/circuit-simulator$suffix"
    if(!$data.solver -or $data.solver.file -ne $expectedSolver -or
       $data.solver.declared_source_commit -notmatch '^[0-9a-f]{40}$' -or
       !$seen.Contains($expectedSolver)){throw 'Simulation package requires recorded solver and source provenance'}
    if((Get-FileHash -LiteralPath (Join-Path $root $expectedSolver)).Hash.ToLowerInvariant() -ne $data.solver.sha256){throw 'Solver provenance hash mismatch'}
}elseif($data.solver){throw 'Editor-only package cannot claim an included solver'}
foreach($required in @('wwwroot/index.html',"SchematicLab.WebHost$suffix","mcp/SchematicLab.Mcp$suffix",'Launch-Schematic.ps1','Test-SchematicBundle.ps1','README.txt')){
    if(!$seen.Contains($required)){throw "Required bundle entry not recorded: $required"}
}
"Verified $($seen.Count) bundle files."
