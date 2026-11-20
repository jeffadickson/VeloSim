#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName='Run')]
param(
    [Parameter(Mandatory,ParameterSetName='Run')][string]$OutputDirectory,
    [Parameter(Mandatory,ParameterSetName='Validate')][string]$ReportPath
)
$ErrorActionPreference='Stop'
function Confirm-SchematicReport([string]$Path){
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw 'Test result report missing'}
    [xml]$report=Get-Content -LiteralPath $Path -Raw
    $summary=$report.TestRun.ResultSummary
    $results=@($report.TestRun.Results.UnitTestResult)
    if($summary.outcome -ne 'Completed' -or [int]$summary.Counters.failed -ne 0 -or
       [int]$summary.Counters.error -ne 0 -or [int]$summary.Counters.aborted -ne 0 -or
       [int]$summary.Counters.executed -ne $results.Count -or !$results.Count){throw 'Test run is failed, incomplete or inconsistent'}
    # Some adapters return exit 0 and partial counters after an asynchronous
    # framework exception. Inspect the actual report, not only the process exit.
    if([string]$summary.Output.StdOut -match '(?im)Unhandled exception|Test Run Aborted|Fatal error'){throw 'Test framework reported an unhandled or fatal failure'}
    foreach($required in @('CommandTests.StaleEditLeavesDocumentAndRunIntact','StandaloneStartupTests.ArbitraryArgumentVectorsRemainTypedAndDeterministic','StandaloneStartupTests.McpParentLookupRequiresTheMarkedBundleAndTheMcpDirectory')){
        if(!@($results|Where-Object {$_.testName -eq $required -and $_.outcome -eq 'Passed'}).Count){throw "Required verification did not complete: $required"}
    }
    "Verified $($summary.Counters.passed) passed tests; $($summary.Counters.notExecuted) not executed."
}
if($PSCmdlet.ParameterSetName -eq 'Validate'){Confirm-SchematicReport $ReportPath;return}
$app=Split-Path -Parent $PSScriptRoot
foreach($p in @($app,(Split-Path -Parent ([IO.Path]::GetFullPath($OutputDirectory))))){if(!(Test-Path -LiteralPath $p -PathType Container)){throw "Missing directory: $p"}}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Fresh verification output required'}
if($IsWindows){
    $artifactRoot=if($env:CODEX_ARTIFACT_ROOT){$env:CODEX_ARTIFACT_ROOT}else{'E:/Artifacts'}
    if(!(Test-Path -LiteralPath $artifactRoot -PathType Container)){throw 'Missing artifact root'}
    $prefix=[IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(![IO.Path]::GetFullPath($OutputDirectory).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Windows output must be below the configured artifact root.'}
}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$out=(Resolve-Path -LiteralPath $OutputDirectory).Path
$location=Get-Location;$temp=$env:TEMP;$tmp=$env:TMP
try{
    $env:TEMP=$out;$env:TMP=$out
    Set-Location -LiteralPath $app
    dotnet test Tests/SchematicLab.Tests.csproj --artifacts-path (Join-Path $out build) --results-directory (Join-Path $out results) --logger 'trx;LogFileName=schematic.trx' --collect:'XPlat Code Coverage' -p:UseSharedCompilation=false --disable-build-servers -m:1
    if($LASTEXITCODE){throw "Test process failed: $LASTEXITCODE"}
    Confirm-SchematicReport (Join-Path $out 'results/schematic.trx')
}finally{Set-Location -LiteralPath $location.Path;$env:TEMP=$temp;$env:TMP=$tmp}
