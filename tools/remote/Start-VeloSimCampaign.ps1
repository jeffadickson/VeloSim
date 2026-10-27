#requires -Version 7.4
<#
.SYNOPSIS
Downloads source, prepares WSL, runs a bounded campaign and publishes small reports.
.DESCRIPTION
No assistant connection is required. Commit/signing/authentication failures pause
for recovery; -ResumePublish skips completed tests. Raw artifacts remain local.
#>
[CmdletBinding()]
param([string]$Repository='https://github.com/jeffadickson/VeloSim.git',
      [string]$Ref='procedural-types-arrays',
      [string]$Root=(Join-Path $env:USERPROFILE 'VeloSimRemote'),
      [string]$Distribution='Ubuntu-24.04',
      [ValidateRange(1,12)][double]$Hours=12,
      [ValidateRange(1,16)][int]$MutationConcurrency=2,
      [switch]$InstallPrerequisites,[switch]$ResumePublish,[switch]$PrepareOnly,[switch]$NonInteractive,[switch]$Smoke)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$state=$null;$runLock=$null
try {
    foreach($file in @('Remote.Common.ps1','Publish-RemoteResults.ps1')){
        $path=Join-Path $PSScriptRoot $file
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Download the complete script directory: $file missing"}
        . $path
    }
    if(-not$IsWindows){throw 'Run this launcher in Windows PowerShell7.4 or newer'}
    $Root=[IO.Path]::GetFullPath($Root)
    if($Repository -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$'){throw 'Use a GitHub HTTPS URL without embedded credentials'}
    Assert-RunDirectory (Split-Path -Parent $Root);Assert-RunDirectory $Root -Create
    $runLock=[IO.File]::Open((Join-Path $Root '.run.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $marker=Join-Path $Root '.velosim-remote-root.json'
    if(-not$ResumePublish -and -not(Test-Path -LiteralPath $marker -PathType Leaf)){
        $unknown=@(Get-ChildItem -LiteralPath $Root -Force|Where-Object {$_.Name -notin @('.run.lock','checkout')})
        if($unknown.Count){throw 'Run root contains unknown files. They are preserved; choose a fresh root'}
        Write-RunJson $marker @{schema='velosim.remote.root.v1';repository=$Repository}
    }elseif(-not$ResumePublish){
        $ownership=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
        if($ownership.schema -ne 'velosim.remote.root.v1' -or $ownership.repository -ne $Repository){throw 'Run-root ownership marker does not match this repository'}
    }
    $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($Root))
    if($drive.AvailableFreeSpace -lt 10GB){throw 'At least10GB free disk space is required before preparation'}
    $statePath=Join-Path $Root 'state.json'
    if($ResumePublish){
        if(-not(Test-Path -LiteralPath $statePath -PathType Leaf)){throw 'Saved publication state missing'}
        $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
        Publish-VeloSimResults $state $Root -NonInteractive:$NonInteractive;return
    }
    if(Test-Path -LiteralPath $statePath){throw 'Run root already has state. Use -ResumePublish or a new root'}
    foreach($tool in @('git','gh','wsl.exe')){if(-not(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Install $tool using the guide, then retry"}}
    $distros=(Invoke-RunCommand wsl.exe @('--list','--quiet') $Root).Out.Replace([string][char]0,'') -split '\r?\n'
    if($Distribution -notin @($distros|ForEach-Object{$_.Trim()})){throw "Run wsl --install -d $Distribution and finish its first launch, then retry"}
    $versions=(Invoke-RunCommand wsl.exe @('--list','--verbose') $Root).Out.Replace([string][char]0,'')
    if($versions -notmatch ('(?m)^\s*\*?\s*'+[regex]::Escape($Distribution)+'\s+.+\s+2\s*$')){throw "Use wsl --set-version $Distribution 2 before running the campaign"}
    $logs=Join-Path $Root 'logs';Assert-RunDirectory $logs -Create
    $architecture=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','uname','-m') $Root -LogPrefix (Join-Path $logs 'architecture')).Out.Trim()
    if($architecture -ne 'x86_64'){throw 'This NativeAOT campaign is qualified for x64 WSL only; ARM64 needs separate qualification'}
    $checkout=Join-Path $Root 'checkout'
    if(Test-Path -LiteralPath $checkout -PathType Container){
        if((Invoke-RunCommand git @('status','--porcelain') $checkout).Out.Trim()){throw 'Existing checkout is dirty: preserve it and choose a new root'}
        if((Invoke-RunCommand git @('symbolic-ref','--quiet','HEAD') $checkout -AllowFailure).Code -ne 0){throw 'Existing checkout is detached; preserve its commits and choose a fresh run root'}
        if((Invoke-RunCommand git @('remote','get-url','origin') $checkout).Out.Trim() -ne $Repository){throw 'Existing origin differs: choose a new root'}
        Invoke-GitWithRecovery @('fetch','origin',$Ref) $checkout (Join-Path $logs 'fetch') -NonInteractive:$NonInteractive|Out-Null
        Invoke-RunCommand git @('switch','--detach','FETCH_HEAD') $checkout|Out-Null
    }else{Invoke-GitWithRecovery @('clone','--single-branch','--branch',$Ref,$Repository,$checkout) $Root (Join-Path $logs 'clone') -NonInteractive:$NonInteractive|Out-Null}
    $sha=(Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim()
    if($sha -notmatch '^[a-f0-9]{40}$'){throw 'Source SHA invalid'}
    foreach($file in @('tools/remote/Run-LinuxCampaign.ps1','tools/RuntimeFuzzCampaign.cs','verification/stryker-remote.json','tools/remote/Worker-Bridge.ps1','tools/remote/Install-UbuntuPrerequisites.sh')){if(-not(Test-Path -LiteralPath (Join-Path $checkout $file) -PathType Leaf)){throw "Selected Git revision lacks $file. Download the published campaign revision"}}
    $bridge=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','wslpath','-a',(Join-Path $checkout 'tools/remote/Worker-Bridge.ps1')) $Root).Out.Trim()
    if($InstallPrerequisites){
        $installer=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','wslpath','-a',(Join-Path $checkout 'tools/remote/Install-UbuntuPrerequisites.sh')) $Root).Out.Trim()
        Invoke-RunCommand wsl.exe @('-d',$Distribution,'-u','root','--','bash',$installer) $Root -TimeoutSeconds 2400 -LogPrefix (Join-Path $logs 'prerequisites')|Out-Null
    }
    Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','pwsh','-NoProfile','-Command','[Environment]::Version.ToString()') $Root -LogPrefix (Join-Path $logs 'wsl-check')|Out-Null
    $id=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $state=[pscustomobject]@{id=$id;sourceCommit=$sha;checkout=$checkout;branch=('results/remote-'+$id);prepared=$false;committed=$false;pushed=$false;resultCommit='';phase='preparing';linuxRoot=''}
    Write-RunJson $statePath $state
    $archive=Join-Path $Root 'source.tar'
    if(Test-Path -LiteralPath $archive){throw 'Existing archive preserved; choose a fresh run root'}
    Invoke-RunCommand git @('archive','--format=tar','-o',$archive,$sha) $checkout|Out-Null
    $linuxArchive=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','wslpath','-a',$archive) $Root).Out.Trim()
    $linuxHome=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','sh','-c','printf %s "$HOME"') $Root).Out.Trim()
    if(-not$linuxHome.StartsWith('/')){throw 'Linux home lookup failed'}
    $state.linuxRoot=$linuxHome+'/.local/share/VeloSim/remote-'+$id;Write-RunJson $statePath $state
    if($PrepareOnly){Write-Host "Preparation passed: source $sha. No tests or push executed. Use a new root for an actual run.";return}
    $seconds=if($Smoke){30}else{[int]($Hours*3600)}
    $state.phase='testing';Write-RunJson $statePath $state
    $arguments=@('-d',$Distribution,'--','pwsh','-NoProfile','-File',$bridge,'-Mode','Run','-RunPath',$state.linuxRoot,'-Archive',$linuxArchive,'-Seconds',[string]$seconds,'-MutationConcurrency',[string]$MutationConcurrency)
    if($InstallPrerequisites){$arguments+='-InstallPrerequisites'}
    if($Smoke){$arguments+='-Smoke'}
    $test=Invoke-RunCommand wsl.exe $arguments $Root -TimeoutSeconds ([int]($Hours*3600)+7200) -LogPrefix (Join-Path $logs 'campaign') -AllowFailure
    $report=Join-Path $Root 'report';Assert-RunDirectory $report -Create
    $linuxReport=(Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','wslpath','-a',$report) $Root).Out.Trim()
    Invoke-RunCommand wsl.exe @('-d',$Distribution,'--','pwsh','-NoProfile','-File',$bridge,'-Mode','Collect','-RunPath',$state.linuxRoot,'-Destination',$linuxReport) $Root -LogPrefix (Join-Path $logs 'collect')|Out-Null
    Write-RunJson (Join-Path $report 'run.json') @{sourceCommit=$sha;campaignExit=$test.Code;linuxRoot=$state.linuxRoot;hours=$Hours;id=$id}
    $state.phase='publishing';Write-RunJson $statePath $state
    Publish-VeloSimResults $state $Root -NonInteractive:$NonInteractive
    Write-Host "Test result exit: $($test.Code). Raw results: $($state.linuxRoot)/output"
}catch{
    $issue=$_.Exception.Message
    if($state){
        $state.phase='action-required'
        try{Write-RunJson (Join-Path $Root 'state.json') $state}catch{Write-Host 'Checkpoint could not be updated; existing files and local commits remain in the run root.'}
    }
    Write-Host ('Paused: '+$issue)
    Write-Host "No local commits or test data were discarded. See $Root. Use -ResumePublish only to retry publication."
    if(-not$NonInteractive){try{[void](Read-Host 'Press Enter to return to PowerShell')}catch{}}
    exit 2
}finally{
    if($runLock){$runLock.Dispose()}
}
