#requires -Version 7.4
# A plain-file WSL entry point keeps launch arguments inspectable and avoids
# embedding scripts in command strings. All paths are passed as separate args.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Run','Collect')][string]$Mode,
      [Parameter(Mandatory)][string]$RunPath,[string]$Archive,[string]$Destination,
      [ValidateRange(1,43200)][int]$Seconds=43200,
      [ValidateRange(1,16)][int]$MutationConcurrency=2,
      [switch]$InstallPrerequisites,[switch]$Smoke)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
. (Join-Path $PSScriptRoot 'Remote.Common.ps1')
try {
    if($Mode -eq 'Run'){
        if(-not$IsLinux){throw 'Run mode requires Linux/WSL'}
        if(Test-Path -LiteralPath $RunPath){throw 'Linux run directory already exists'}
        if(-not(Test-Path -LiteralPath $Archive -PathType Leaf)){throw 'Source archive missing'}
        Assert-RunDirectory $HOME
        $parent=$HOME
        foreach($part in @('.local','share','VeloSim')){$next=Join-Path $parent $part;Assert-RunDirectory $next -Create;$parent=$next}
        Assert-RunDirectory (Split-Path -Parent $RunPath);Assert-RunDirectory $RunPath -Create
        $source=Join-Path $RunPath 'source';Assert-RunDirectory $source -Create
        Invoke-RunCommand tar @('-xf',$Archive,'-C',$source) $RunPath -LogPrefix (Join-Path $RunPath 'extract')|Out-Null
        & (Join-Path $source 'tools/remote/Run-LinuxCampaign.ps1') -Source $source -Output (Join-Path $RunPath 'output') -Seconds $Seconds -MutationConcurrency $MutationConcurrency -InstallPrerequisites:$InstallPrerequisites -Smoke:$Smoke
        exit $LASTEXITCODE
    }
    $output=Join-Path $RunPath 'output';Assert-RunDirectory $output;Assert-RunDirectory $Destination
    foreach($relative in @('summary.json','native/report.json','native/coverage.stdout.log','stryker.out.log','stryker.err.log')){
        $file=Join-Path $output $relative
        if(Test-Path -LiteralPath $file -PathType Leaf){
            if((Get-Item -LiteralPath $file).Length -lt 2MB){Copy-Item -LiteralPath $file -Destination (Join-Path $Destination ($relative.Replace('/','-')))}
        }
    }
    $reports=Join-Path $output 'stryker'
    if(Test-Path -LiteralPath $reports -PathType Container){
        $file=Get-ChildItem -LiteralPath $reports -Filter mutation-report.json -Recurse -File|Select-Object -First 1
        if($file){
            $json=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json;$counts=@{}
            foreach($entry in $json.files.PSObject.Properties){foreach($mutant in $entry.Value.mutants){if(-not$counts.ContainsKey($mutant.status)){$counts[$mutant.status]=0};$counts[$mutant.status]++}}
            Write-RunJson (Join-Path $Destination 'mutation-counts.json') $counts
        }
    }
}catch{
    Write-Host ('Worker needs attention: '+$_.Exception.Message)
    exit 2
}
