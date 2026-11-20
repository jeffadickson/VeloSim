#requires -Version 7.4
[CmdletBinding()]
param([switch]$InstallPrerequisites)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
try {
    . (Join-Path $PSScriptRoot 'Remote.Common.ps1')
    $repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot);Assert-RunDirectory $repo
    $module=Get-Module -ListAvailable Pester|Where-Object {$_.Version -eq [version]'5.7.1'}|Select-Object -First 1
    if(-not$module){
        if(-not$InstallPrerequisites){throw 'Pester5.7.1 missing. Re-run with -InstallPrerequisites to install the pinned test tool'}
        Install-PSResource Pester -Version 5.7.1 -Scope CurrentUser -TrustRepository -ErrorAction Stop
    }
    Import-Module Pester -RequiredVersion 5.7.1 -Force
    $artifact=Join-Path $repo 'artifacts';Assert-RunDirectory $artifact -Create
    $output=Join-Path $artifact 'remote-script-tests';Assert-RunDirectory $output -Create
    $bad=$false
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File|ForEach-Object{
        $tokens=$null;$issues=$null
        [void][Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$issues)
        foreach($issue in $issues){$bad=$true;Write-Host ($_.Name+': '+$issue.Message)}
    }
    if($bad){throw 'PowerShell parser checks failed'}
    $config=New-PesterConfiguration;$config.Run.Path=Join-Path $repo 'tests/remote/RemoteCampaign.Tests.ps1';$config.Run.PassThru=$true
    $config.CodeCoverage.Enabled=$true;$config.CodeCoverage.Path=@((Join-Path $PSScriptRoot 'Remote.Common.ps1'),(Join-Path $PSScriptRoot 'Publish-RemoteResults.ps1'))
    $config.CodeCoverage.OutputPath=Join-Path $output 'coverage.xml'
    $config.TestResult.Enabled=$true;$config.TestResult.OutputPath=Join-Path $output 'tests.xml'
    $result=Invoke-Pester -Configuration $config
    Write-Host "Test results: $($result.PassedCount) passed, $($result.FailedCount) failed. Evidence: $output"
    if($result.FailedCount){exit 1}
}catch{
    Write-Host ('Test setup/result needs attention: '+$_.Exception.Message)
    exit 2
}
