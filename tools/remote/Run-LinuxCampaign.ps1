#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Output,
      [ValidateRange(1,43200)][int]$Seconds = 43200,
      [ValidateRange(1,16)][int]$MutationConcurrency = 2,
      [switch]$InstallPrerequisites,[switch]$Smoke)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'Remote.Common.ps1')
$summary = [ordered]@{ status = 'preparing'; startedUtc = [DateTime]::UtcNow.ToString('O'); seconds = $Seconds; limitations = @('Native runtime and managed IR validation only; no Surelog/UHDM frontend campaign','Stryker may finish before the deadline; unfinished runs are reported incomplete','No correctness proof or exhaustive mutation claim') }
$fuzz=$null;$ownsOutput=$false
try {
    Assert-RunDirectory $Source
    if([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'This campaign requires an x64 Linux environment'}
    if(([IO.DriveInfo]::new('/')).AvailableFreeSpace -lt 10GB){throw 'At least10GB free Linux disk space is required'}
    Assert-RunDirectory (Split-Path -Parent $Output)
    if (Test-Path -LiteralPath $Output) { throw 'Output already exists; choose a fresh run directory.' }
    Assert-RunDirectory $Output -Create
    $ownsOutput=$true
    foreach ($tool in @('cmake','ninja','clang++-18','llvm-profdata-18','llvm-cov-18','bash')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required tool missing: $tool. Re-run Windows setup with -InstallPrerequisites." }
    }
    $pin = (Get-Content -LiteralPath (Join-Path $Source 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $matching = $false
    if ($dotnetCommand) { $matching = @((& dotnet --list-sdks) -match ('^' + [regex]::Escape($pin) + ' ')).Count -gt 0 }
    if (-not $matching) {
        if (-not $InstallPrerequisites) { throw "Pinned SDK $pin missing. Re-run with -InstallPrerequisites." }
        $sdk = Join-Path $Output 'sdk'; Assert-RunDirectory $sdk -Create
        $installer = Join-Path $Output 'dotnet-install.sh'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.sh' -OutFile $installer
        Invoke-RunCommand bash @($installer,'--version',$pin,'--install-dir',$sdk,'--no-path') $Output -LogPrefix (Join-Path $Output 'sdk-install') | Out-Null
        $env:PATH = $sdk + ':' + $env:PATH
    }
    $dotnetItem = Get-Item -LiteralPath (Get-Command dotnet).Source
    $resolved = $dotnetItem.ResolveLinkTarget($true)
    $env:DOTNET_ROOT = Split-Path -Parent $(if ($resolved) { $resolved.FullName } else { $dotnetItem.FullName })
    foreach ($tool in @('dotnet','cmake','ninja','clang++-18','llvm-profdata-18')) {
        $versionArgs = if ($tool -eq 'dotnet') { @('--info') } else { @('--version') }
        Invoke-RunCommand $tool $versionArgs $Source -LogPrefix (Join-Path $Output ('version-' + $tool.Replace('+','p'))) | Out-Null
    }
    $summary.sourceSha256 = @{}
    foreach ($relative in @('global.json','tools/RuntimeFuzzCampaign.cs','tools/fuzz/runtime_fuzz.cpp','native/rtl/runtime.h','native/rtl/containers.h','native/rtl/timed_events.h','verification/stryker-remote.json')) {
        $summary.sourceSha256[$relative] = (Get-FileHash -LiteralPath (Join-Path $Source $relative) -Algorithm SHA256).Hash
    }
    # Build and instrumented NUnit jobs are serialized. The native campaign owns
    # separate snapshots and binaries and can run alongside the single Stryker job.
    $publish = Join-Path $Output 'controller'
    Invoke-RunCommand dotnet @('publish','tools/RuntimeFuzzCampaign.cs','-c','Release','-r','linux-x64','-p:PublishAot=true','-p:VeloSimNativeJobs=4','-o',$publish) $Source -LogPrefix (Join-Path $Output 'controller-build') | Out-Null
    Invoke-RunCommand dotnet @('tool','restore') $Source -LogPrefix (Join-Path $Output 'tool-restore') | Out-Null
    Invoke-RunCommand dotnet @('build','tests/VeloSim.Tests','-c','Release','-m:4') $Source -LogPrefix (Join-Path $Output 'managed-build') | Out-Null
    $env:VELOSIM_REAL_FIFO = '0'
    $testFilter = '(FullyQualifiedName~RtlDynamicArrayTests&FullyQualifiedName!~AllocationResizeAccessAndDeleteMatchOriginalHdl)|FullyQualifiedName~RtlIndexedTests|FullyQualifiedName~RtlProcessesTests.SharedClockProcessesEmitOneSnapshotAndCommit|FullyQualifiedName~RtlEvolutionRobustnessTests.MalformedShapeProperty'
    Invoke-RunCommand dotnet @('test','tests/VeloSim.Tests','-c','Release','--no-build','--filter',$testFilter,'--collect:XPlat Code Coverage','--settings','coverage.runsettings','--results-directory',(Join-Path $Output 'managed-tests'),'--logger','trx') $Source -LogPrefix (Join-Path $Output 'managed-tests') | Out-Null
    $trx=Get-ChildItem -LiteralPath (Join-Path $Output 'managed-tests') -Filter '*.trx' -File|Select-Object -First 1
    if(-not$trx){throw 'Managed test evidence missing'}
    [xml]$testXml=Get-Content -LiteralPath $trx.FullName -Raw
    $summary.managedTests=@{passed=[int]$testXml.TestRun.ResultSummary.Counters.passed;failed=[int]$testXml.TestRun.ResultSummary.Counters.failed;total=[int]$testXml.TestRun.ResultSummary.Counters.total;notExecuted=[int]$testXml.TestRun.ResultSummary.Counters.notExecuted}
    if($summary.managedTests.total -lt 1 -or $summary.managedTests.failed -ne 0){throw 'Managed qualification did not execute passing tests'}
    $coverage=Get-ChildItem -LiteralPath (Join-Path $Output 'managed-tests') -Filter coverage.cobertura.xml -Recurse -File|Select-Object -First 1
    if(-not$coverage){throw 'Managed coverage evidence missing'}
    [xml]$coverageXml=Get-Content -LiteralPath $coverage.FullName -Raw
    $summary.managedCoverage=@{lineRate=$coverageXml.coverage.'line-rate';branchRate=$coverageXml.coverage.'branch-rate';scope='Selected managed tests, not the full suite'}
    $summary.status = 'running'; $summary.testStartedUtc = [DateTime]::UtcNow.ToString('O')
    Write-RunJson (Join-Path $Output 'summary.json') $summary
    $fuzzInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $publish 'RuntimeFuzzCampaign'))
    $fuzzInfo.WorkingDirectory = $Source; $fuzzInfo.UseShellExecute = $false
    foreach ($arg in @($Source,(Join-Path $Output 'native'),[string]$Seconds)) { $fuzzInfo.ArgumentList.Add($arg) }
    $fuzz = [Diagnostics.Process]::Start($fuzzInfo)
    $mutationSeconds=if($Smoke){1800}else{$Seconds}
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max($Seconds,$mutationSeconds))
    try {
        $config=if($Smoke){'verification/stryker-dynamic.json'}else{'verification/stryker-remote.json'}
        $strykerArgs = @('tool','run','dotnet-stryker','--config-file',(Join-Path $Source $config),'--concurrency',[string]$MutationConcurrency,'--output',(Join-Path $Output 'stryker'))
        $stryker = Invoke-RunCommand dotnet $strykerArgs (Join-Path $Source 'tests/VeloSim.Tests') -TimeoutSeconds $mutationSeconds -LogPrefix (Join-Path $Output 'stryker') -AllowFailure
        $summary.strykerExit = $stryker.Code
        $mutationReport=Get-ChildItem -LiteralPath (Join-Path $Output 'stryker') -Filter mutation-report.json -Recurse -File|Select-Object -First 1
        if($stryker.Code -eq 0 -and -not$mutationReport){$summary.strykerExit=2;$summary.strykerIssue='No actual mutation report was produced'}
    } catch {
        $summary.strykerStatus = 'incomplete'; $summary.strykerIssue = $_.Exception.Message
    }
    # Allow preparation and the separate coverage replay a bounded margin.
    $remaining = [Math]::Max(1,[int]($deadline.AddMinutes(20) - [DateTime]::UtcNow).TotalSeconds)
    if (-not $fuzz.WaitForExit($remaining * 1000)) { $fuzz.Kill($true); $fuzz.WaitForExit(); $summary.nativeStatus = 'incomplete' }
    else { $summary.nativeExit = $fuzz.ExitCode }
    $fuzz.Dispose();$fuzz=$null
    $summary.status = if ($summary.Contains('nativeExit') -and $summary.nativeExit -eq 0 -and $summary.Contains('strykerExit') -and $summary.strykerExit -eq 0) { 'completed' } else { 'failed-or-incomplete' }
} catch {
    $summary.status = 'action-required'; $summary.issue = $_.Exception.Message
} finally {
    if($fuzz){if(-not$fuzz.HasExited){$fuzz.Kill($true);$fuzz.WaitForExit()};$fuzz.Dispose()}
    $summary.finishedUtc = [DateTime]::UtcNow.ToString('O')
    if ($ownsOutput -and (Test-Path -LiteralPath $Output -PathType Container)) { Write-RunJson (Join-Path $Output 'summary.json') $summary }
}
Write-Host ('Test result: ' + $summary.status + '. Results: ' + $Output)
if ($summary.status -ne 'completed') { exit 2 }
