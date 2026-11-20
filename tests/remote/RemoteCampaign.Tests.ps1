# Pester5 tests exercise actual process and local Git behavior; no GitHub upload.
BeforeAll {
    $repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $common=Join-Path $repo 'tools/remote/Remote.Common.ps1'
    $publisher=Join-Path $repo 'tools/remote/Publish-RemoteResults.ps1'
    . $common
    . $publisher
}
Describe 'Checked remote orchestration' {
    It 'rejects a missing directory without silently creating it' {
        {Assert-RunDirectory (Join-Path $TestDrive 'absent')}|Should -Throw '*Directory missing*'
    }
    It 'creates a directory only below an existing parent' {
        $target=Join-Path $TestDrive 'created';Assert-RunDirectory $target -Create
        Test-Path -LiteralPath $target -PathType Container|Should -BeTrue
        {Assert-RunDirectory (Join-Path $TestDrive 'missing/child') -Create}|Should -Throw '*Parent directory missing*'
    }
    It 'rejects an unavailable executable' {
        {Invoke-RunCommand 'velosim-missing-tool-82f4' @() $TestDrive}|Should -Throw '*Required tool missing*'
    }
    It 'retains exact native exit and stderr when failure is expected' {
        $result=Invoke-RunCommand pwsh @('-NoProfile','-Command',"[Console]::Error.WriteLine('retained');exit 7") $TestDrive -AllowFailure
        $result.Code|Should -Be 7;$result.Err|Should -Match 'retained'
    }
    It 'propagates unexpected command failure after writing durable logs' {
        $log=Join-Path $TestDrive 'failure'
        {Invoke-RunCommand pwsh @('-NoProfile','-Command','exit 3') $TestDrive -LogPrefix $log}|Should -Throw '*exit 3*'
        Test-Path -LiteralPath ($log+'.err.log') -PathType Leaf|Should -BeTrue
    }
    It 'retains useful error details when no log path was supplied' {
        {Invoke-RunCommand pwsh @('-NoProfile','-Command',"[Console]::Error.WriteLine('specific-detail');exit 3") $TestDrive}|Should -Throw '*specific-detail*'
    }
    It 'terminates an over-budget command' {
        $log=Join-Path $TestDrive 'timeout'
        {Invoke-RunCommand pwsh @('-NoProfile','-Command',"Write-Output 'started';Start-Sleep 10") $TestDrive -TimeoutSeconds 1 -LogPrefix $log}|Should -Throw '*deadline*'
        Test-Path -LiteralPath ($log+'.out.log') -PathType Leaf|Should -BeTrue
    }
    It 'preserves spaces and quotes in process arguments' {
        $file=Join-Path $TestDrive 'echo.ps1';'param([string]$Value) [Console]::Write($Value)'|Set-Content -LiteralPath $file
        $value="a b 'quoted' `$x"
        (Invoke-RunCommand pwsh @('-NoProfile','-File',$file,$value) $TestDrive).Out|Should -BeExactly $value
    }
    It 'retries only the failed Git step after authorization' {
        $script:attempt=0
        $script:checks=0
        $runner={param($a,$d,$l) $script:attempt++;[pscustomobject]@{Code=$(if($script:attempt -eq 1){128}else{0});Out='';Err='auth'}}
        $result=Invoke-GitWithRecovery @('push') $TestDrive '' -Runner $runner -Prompt {param($m) ''} -BeforeAttempt {param($d) $script:checks++}
        $result.Code|Should -Be 0;$script:attempt|Should -Be 2;$script:checks|Should -Be 2
    }
    It 'leaves a recoverable action-required state in unattended mode' {
        {Invoke-GitWithRecovery @('commit') $TestDrive '' -Runner {param($a,$d,$l) [pscustomobject]@{Code=128;Out='';Err='auth'}} -NonInteractive}|Should -Throw '*ACTION_REQUIRED*'
    }
    It 'honors a user pause without retrying or discarding results' {
        {Invoke-GitWithRecovery @('push') $TestDrive '' -Runner {param($a,$d,$l) [pscustomobject]@{Code=128;Out='';Err='auth'}} -Prompt {param($m) 'Q'}}|Should -Throw '*Publication paused*'
    }
    It 'does not spin on a closed authorization input stream' {
        {Invoke-GitWithRecovery @('push') $TestDrive '' -Runner {param($a,$d,$l) [pscustomobject]@{Code=128;Out='';Err='auth'}} -Prompt {param($m) $null}}|Should -Throw '*input closed*'
    }
    It 'writes a round-trippable checkpoint' {
        $file=Join-Path $TestDrive 'state.json';Write-RunJson $file @{phase='action-required';committed=$true}
        (Get-Content -LiteralPath $file -Raw|ConvertFrom-Json).phase|Should -Be 'action-required'
    }
    It 'collects only small summaries and actual mutation counts' {
        $run=Join-Path $TestDrive 'collect';Assert-RunDirectory $run -Create
        $output=Join-Path $run 'output';Assert-RunDirectory $output -Create
        $native=Join-Path $output 'native';Assert-RunDirectory $native -Create
        $mutations=Join-Path $output 'stryker';Assert-RunDirectory $mutations -Create
        Write-RunJson (Join-Path $output 'summary.json') @{status='completed'}
        Write-RunJson (Join-Path $native 'report.json') @{domains=@(@{exit=0})}
        Write-RunJson (Join-Path $mutations 'mutation-report.json') @{files=@{example=@{mutants=@(@{status='Killed'},@{status='Survived'})}}}
        $destination=Join-Path $run 'collected';Assert-RunDirectory $destination -Create
        $bridge=Join-Path $repo 'tools/remote/Worker-Bridge.ps1'
        (Invoke-RunCommand pwsh @('-NoProfile','-File',$bridge,'-Mode','Collect','-RunPath',$run,'-Destination',$destination) $TestDrive).Code|Should -Be 0
        $counts=Get-Content -LiteralPath (Join-Path $destination 'mutation-counts.json') -Raw|ConvertFrom-Json
        $counts.Killed|Should -Be 1;$counts.Survived|Should -Be 1
        Test-Path -LiteralPath (Join-Path $destination 'native-report.json') -PathType Leaf|Should -BeTrue
    }
    It 'refuses to overwrite an existing worker output summary' {
        $output=Join-Path $TestDrive 'prior-worker';Assert-RunDirectory $output -Create
        $summary=Join-Path $output 'summary.json';'prior evidence'|Set-Content -LiteralPath $summary
        $worker=Join-Path $repo 'tools/remote/Run-LinuxCampaign.ps1'
        (Invoke-RunCommand pwsh @('-NoProfile','-File',$worker,'-Source',$TestDrive,'-Output',$output,'-Seconds','1') $TestDrive -AllowFailure).Code|Should -Be 2
        (Get-Content -LiteralPath $summary -Raw).Trim()|Should -Be 'prior evidence'
    }
    It 'preserves unknown files in a proposed Windows run root' -Skip:(-not $IsWindows) {
        $output=Join-Path $TestDrive 'foreign';Assert-RunDirectory $output -Create
        $file=Join-Path $output 'unknown.txt';'preserve me'|Set-Content -LiteralPath $file
        $launcher=Join-Path $repo 'tools/remote/Start-VeloSimCampaign.ps1'
        (Invoke-RunCommand pwsh @('-NoProfile','-File',$launcher,'-Root',$output,'-NonInteractive','-PrepareOnly') $TestDrive -AllowFailure).Code|Should -Be 2
        (Get-Content -LiteralPath $file -Raw).Trim()|Should -Be 'preserve me'
        Test-Path -LiteralPath (Join-Path $output 'source.tar')|Should -BeFalse
    }
    It 'commits a summary and handoff to a fresh branch and verifies the remote SHA' {
        $root=Join-Path $TestDrive 'publish';Assert-RunDirectory $root -Create
        $bare=Join-Path $root 'bare.git';$checkout=Join-Path $root 'checkout'
        Invoke-RunCommand git @('init','--bare',$bare) $root|Out-Null
        Assert-RunDirectory $checkout -Create;Invoke-RunCommand git @('init','-b','main') $checkout|Out-Null
        Invoke-RunCommand git @('config','user.name','VeloSim fixture') $checkout|Out-Null
        Invoke-RunCommand git @('config','user.email','fixture@example.invalid') $checkout|Out-Null
        Invoke-RunCommand git @('config','commit.gpgsign','false') $checkout|Out-Null
        'Prior handoff'|Set-Content -LiteralPath (Join-Path $checkout 'prompt.md')
        Invoke-RunCommand git @('add','prompt.md') $checkout|Out-Null
        Invoke-RunCommand git @('commit','-m','Fixture source') $checkout|Out-Null
        Invoke-RunCommand git @('remote','add','origin',$bare) $checkout|Out-Null
        $sha=(Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim()
        $report=Join-Path $root 'report';Assert-RunDirectory $report -Create
        Write-RunJson (Join-Path $report 'summary.json') @{status='completed';source=$sha}
        Assert-RunDirectory (Join-Path $root 'logs') -Create
        $state=[pscustomobject]@{id='fixture';sourceCommit=$sha;checkout=$checkout;branch='results/fixture';prepared=$false;committed=$false;pushed=$false;resultCommit=''}
        Publish-VeloSimResults $state $root -NonInteractive
        $state.pushed|Should -BeTrue
        $head=(Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim();$head|Should -Be $state.resultCommit
        (Invoke-RunCommand git @('log','-1','--format=%s') $checkout).Out|Should -Match '\[skip ci\]'
        (Invoke-RunCommand git @('status','--porcelain') $checkout).Out.Trim()|Should -Be ''
        # Publication resume must not create another commit or rerun tests.
        Publish-VeloSimResults $state $root -NonInteractive
        (Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim()|Should -Be $head
        # Simulate losing only the checkpoint update after a successful commit.
        $state.committed=$false
        Publish-VeloSimResults $state $root -NonInteractive
        (Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim()|Should -Be $head
        'unrelated'|Set-Content -LiteralPath (Join-Path $checkout 'unrelated.txt')
        Invoke-RunCommand git @('add','unrelated.txt') $checkout|Out-Null
        $state.committed=$false
        {Publish-VeloSimResults $state $root -NonInteractive}|Should -Throw '*Unknown staged files*'
    }
}
