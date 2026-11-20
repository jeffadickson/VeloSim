#requires -Version 7.4
param([ValidateRange(1024,65535)][int]$Port=5087,[string]$SolverPath='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(!(Test-Path -LiteralPath $root -PathType Container)){throw 'Missing application directory'}
& (Join-Path $root Test-SchematicBundle.ps1) -Directory $root
$executable=Join-Path $root $(if($IsWindows){'SchematicLab.WebHost.exe'}else{'SchematicLab.WebHost'})
if(!(Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Standalone application executable missing'}
$arguments=@('--port',[string]$Port,'--web-root',(Join-Path $root wwwroot))
if($SolverPath){$arguments+=@('--solver',$SolverPath)}
"Open http://localhost:$Port/ in your browser. Stop the server with Ctrl+C."
& $executable @arguments
exit $LASTEXITCODE
